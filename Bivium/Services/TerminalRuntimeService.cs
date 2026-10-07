using Bivium.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XTerm;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Events;
using XTerm.Input;
using XTerm.Options;

namespace Bivium.Services
{
    /// <summary>
    /// Owns PTYs, headless emulators and terminal history independently from UI circuits
    /// </summary>
    public sealed class TerminalRuntimeService : SubscriptionServiceBase<TerminalRuntimeEvent>, IDisposable
    {
        #region Constants

        /// <summary>
        /// Minimum terminal notification aggregation window
        /// </summary>
        private const int NOTIFICATION_DELAY_MS = 16;

        /// <summary>
        /// Maximum wait for an atomic update before publishing resumes
        /// </summary>
        private const int SYNCHRONIZED_OUTPUT_TIMEOUT_MS = 1000;

        /// <summary>
        /// Maximum revisions recoverable without a full handoff
        /// </summary>
        private const int PATCH_REVISION_WINDOW = 256;

        /// <summary>
        /// Maximum decoded plain-text clipboard request accepted from a terminal application
        /// </summary>
        private const int MAX_CLIPBOARD_BYTES = 1024 * 1024;

        #endregion

        #region Class Variables

        /// <summary>
        /// Application logger without terminal content
        /// </summary>
        private readonly ILogger<TerminalRuntimeService> _logger;

        /// <summary>
        /// Workspace that authorizes mutations
        /// </summary>
        private readonly BiviumWorkspaceService _workspaceService;

        /// <summary>
        /// Settings update subscription
        /// </summary>
        private readonly IDisposable _settingsSubscription;

        /// <summary>
        /// Stops the periodic maintenance loop
        /// </summary>
        private readonly CancellationTokenSource _maintenanceCancellation = new CancellationTokenSource();

        /// <summary>
        /// Periodic retention and hardening task
        /// </summary>
        private readonly Task _maintenanceTask;

        /// <summary>
        /// Terminal sessions owned by the runtime
        /// </summary>
        private readonly Dictionary<int, TerminalSessionRuntime> _sessions = new Dictionary<int, TerminalSessionRuntime>();

        /// <summary>
        /// Current runtime limits
        /// </summary>
        private TerminalRuntimeSettings _settings;

        /// <summary>
        /// Next stable session identifier
        /// </summary>
        private int _nextSessionId = 1;

        /// <summary>
        /// Next number used in automatic labels
        /// </summary>
        private int _nextSessionNumber = 1;

        /// <summary>
        /// Active session identifier
        /// </summary>
        private int _activeSessionId = 0;

        /// <summary>
        /// Monotonic revision of the entire runtime
        /// </summary>
        private long _runtimeRevision = 0;

        /// <summary>
        /// Monotonic identifier for transient browser requests
        /// </summary>
        private long _nextClientEventId = 0;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates the global terminal runtime
        /// </summary>
        /// <param name="settings">Application settings</param>
        /// <param name="logger">Application logger</param>
        /// <param name="workspaceService">Workspace that validates the mutation lease</param>
        public TerminalRuntimeService(IOptionsMonitor<CommanderSettings> settings, ILogger<TerminalRuntimeService> logger, BiviumWorkspaceService workspaceService)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this._workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
            this._settings = settings.CurrentValue.TerminalRuntime ?? new TerminalRuntimeSettings();
            this._settingsSubscription = settings.OnChange(this.HandleSettingsChanged);
            this._maintenanceTask = Task.Run(() => this.RunMaintenanceAsync(this._maintenanceCancellation.Token));
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Returns the bounded runtime snapshot
        /// </summary>
        /// <param name="cancellationToken">Current request token</param>
        /// <returns>Current snapshot</returns>
        public TerminalRuntimeSnapshot GetSnapshot(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<TerminalSessionSnapshot> snapshots = new List<TerminalSessionSnapshot>();
            List<TerminalSessionRuntime> expiredSessions = null;
            int activeSessionId;

            try
            {
                lock (this._lock)
                {
                    this.ThrowIfStopped();

                    // Apply retention before composing the list so the snapshot does not expose expired tabs
                    expiredSessions = this.CleanupExpiredSessionsLocked();
                    activeSessionId = this._activeSessionId;
                    List<TerminalSessionRuntime> sessions = new List<TerminalSessionRuntime>(this._sessions.Values);
                    sessions.Sort((left, right) => left.Id.CompareTo(right.Id));
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lock (sessions[i].SyncRoot)
                        {
                            snapshots.Add(this.CreateSessionSnapshot(sessions[i]));
                        }
                    }
                }
            }
            finally
            {
                this.DisposeSessions(expiredSessions);
            }

            TerminalRuntimeSnapshot result = new TerminalRuntimeSnapshot();
            result.ActiveSessionId = activeSessionId;
            result.Sessions = snapshots.AsReadOnly();
            return result;
        }

        /// <summary>
        /// Returns aggregate metrics without serializing terminal content
        /// </summary>
        /// <returns>Current runtime metrics</returns>
        public TerminalRuntimeMetrics GetMetrics()
        {
            lock (this._lock)
            {
                TerminalRuntimeMetrics result = new TerminalRuntimeMetrics();
                result.SessionCount = this._sessions.Count;
                result.SubscriberCount = this.SubscriberCount;
                foreach (TerminalSessionRuntime session in this._sessions.Values)
                {
                    lock (session.SyncRoot)
                    {
                        if (session.Running)
                            result.RunningSessionCount++;
                        result.HistoryBytes += session.History.RetainedBytes;
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Creates and starts a new terminal session
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="workingDirectory">Initial directory</param>
        /// <returns>New session snapshot</returns>
        public TerminalSessionSnapshot CreateSession(WorkspaceClientToken token, string workingDirectory)
        {
            TerminalSessionRuntime session = null;
            List<TerminalSessionRuntime> expiredSessions = null;
            try
            {
                // Lease validation and session registration form one authoritative mutation
                this._workspaceService.ExecuteMutation(token, () =>
                {
                    lock (this._lock)
                    {
                        this.ThrowIfStopped();
                        expiredSessions = this.CleanupExpiredSessionsLocked();
                        TerminalRuntimeSettings settings = this._settings;
                        if (this._sessions.Count >= Math.Max(1, settings.MaxTabs))
                            throw new InvalidOperationException("Maximum number of terminal tabs reached");

                        int sessionId = this._nextSessionId++;
                        string label = "Term" + this._nextSessionNumber++;
                        string resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : workingDirectory;
                        session = new TerminalSessionRuntime(this, sessionId, label, resolvedWorkingDirectory, settings);
                        this._sessions.Add(sessionId, session);
                        this._activeSessionId = sessionId;
                        this._runtimeRevision++;
                    }
                });
            }
            finally
            {
                this.DisposeSessions(expiredSessions);
            }

            // PTY startup remains outside the global lock because it can perform I/O and callbacks
            if (this.StartShell(session))
                this.NotifySubscribers(new TerminalRuntimeEvent { SessionId = session.Id, Revision = this.GetRuntimeRevision(), SessionsChanged = true });
            lock (session.SyncRoot)
            {
                return this.CreateSessionSnapshot(session);
            }
        }

        /// <summary>
        /// Sets the active terminal tab
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="sessionId">Session identifier</param>
        public void SetActiveSession(WorkspaceClientToken token, int sessionId)
        {
            TerminalSessionRuntime session = null;
            bool changed = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                lock (this._lock)
                {
                    this.ThrowIfStopped();
                    if (!this._sessions.TryGetValue(sessionId, out session))
                        return;

                    this._activeSessionId = sessionId;
                    this._runtimeRevision++;
                }

                lock (session.SyncRoot)
                {
                    session.HasUnreadOutput = false;
                    session.AttentionRequested = false;
                    this.AdvanceSessionRevisionLocked(session);
                    changed = true;
                }
            });

            if (changed)
                this.NotifySubscribers(new TerminalRuntimeEvent { SessionId = sessionId, Revision = this.GetRuntimeRevision(), SessionsChanged = true });
        }

        /// <summary>
        /// Clears a pending attention request after the user opens its terminal tab
        /// </summary>
        public void AcknowledgeAttention(WorkspaceClientToken token, int sessionId)
        {
            TerminalSessionRuntime session = null;
            bool changed = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    if (!session.AttentionRequested)
                        return;
                    session.AttentionRequested = false;
                    this.AdvanceSessionRevisionLocked(session);
                    changed = true;
                }
                this.IncrementRuntimeRevision();
            });

            if (changed)
                this.ScheduleNotification(session, false);
        }

        /// <summary>Mutation shared between lease-based APIs and an already admitted workspace plan</summary>
        private TerminalSessionRuntime RenameSessionState(int sessionId, string label)
        {
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null || string.IsNullOrWhiteSpace(label))
                return null;
            lock (session.SyncRoot)
            {
                session.Label = label.Trim();
                this.AdvanceSessionRevisionLocked(session);
            }
            this.IncrementRuntimeRevision();
            return session;
        }

        /// <summary>Internal runner entry point: receives no revocable browser tokens or circuit callbacks</summary>
        internal FileOperationResult ExecuteAdmittedWorkspaceAction(WorkspaceWorkflowKind kind, System.Collections.Immutable.ImmutableArray<int> sessionIds, string draft)
        {
            if (kind == WorkspaceWorkflowKind.TerminalRename && sessionIds.Length == 1)
            {
                TerminalSessionRuntime session = this.RenameSessionState(sessionIds[0], draft);
                if (session == null)
                    return FileOperationResult.Fail("The terminal session no longer exists or the label is empty.");
                this.ScheduleNotification(session, true);
                return FileOperationResult.Ok(1);
            }
            if (kind != WorkspaceWorkflowKind.TerminalClose)
                return FileOperationResult.Fail("Unsupported terminal action");
            List<TerminalSessionRuntime> removed = new List<TerminalSessionRuntime>();
            lock (this._lock)
            {
                foreach (int id in sessionIds)
                    if (this._sessions.Remove(id, out TerminalSessionRuntime session))
                        removed.Add(session);
                if (!this._sessions.ContainsKey(this._activeSessionId))
                    this._activeSessionId = this.GetLastSessionIdLocked();
                this._runtimeRevision++;
            }
            this.DisposeSessions(removed);
            this.NotifySubscribers(new TerminalRuntimeEvent { SessionId = 0, Revision = this.GetRuntimeRevision(), SessionsChanged = true });
            return FileOperationResult.Ok(removed.Count);
        }

        /// <summary>
        /// Sends input to the PTY without depending on a browser renderer
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="data">Input data</param>
        public void SendInput(WorkspaceClientToken token, int sessionId, string data)
        {
            if (string.IsNullOrEmpty(data))
                return;

            bool restart = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                TerminalSessionRuntime session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    restart = !session.Running && session.Exited && !session.RestartPending;
                    if (restart)
                        session.RestartPending = true;
                    else if (session.Running)
                        session.Shell.SendInput(data);
                }
            });

            if (restart)
                this.RestartSessionInternal(sessionId);
        }

        /// <summary>
        /// Generates a key sequence server-side according to current VT modes
        /// </summary>
        /// <param name="token">Client lease</param>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="keyName">DOM name of the special key</param>
        /// <param name="character">Printable character, when present</param>
        /// <param name="shift">Shift modifier</param>
        /// <param name="control">Control modifier</param>
        /// <param name="alt">Alt modifier</param>
        /// <param name="meta">Meta/Super modifier</param>
        /// <param name="code">Physical DOM identity of the key</param>
        /// <param name="repeat">Repeat of a held key press</param>
        /// <param name="release">Key release</param>
        public void SendKey(WorkspaceClientToken token, int sessionId, string keyName, string character, bool shift, bool control, bool alt, bool meta = false, string code = "", bool repeat = false, bool release = false)
        {
            KeyModifiers modifiers = this.CreateKeyModifiers(shift, control, alt);
            bool restart = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                TerminalSessionRuntime session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    restart = !release && !session.Running && session.Exited && !session.RestartPending;
                    if (restart)
                        session.RestartPending = true;
                    else if (session.Running)
                    {
                        string sequence = GenerateKeySequence(session.Terminal, keyName, character, modifiers, meta, code, repeat, release);
                        if (!string.IsNullOrEmpty(sequence))
                            session.Shell.SendInput(sequence);
                    }
                }
            });

            if (restart)
                this.RestartSessionInternal(sessionId);
        }

        /// <summary>
        /// Sends the paste through the sanitization and negotiated protocols of XTerm.NET
        /// </summary>
        /// <param name="token">Client lease</param>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="text">Pasted text</param>
        public void SendPaste(WorkspaceClientToken token, int sessionId, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            bool restart = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                TerminalSessionRuntime session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    restart = !session.Running && session.Exited && !session.RestartPending;
                    if (restart)
                        session.RestartPending = true;
                    else if (session.Running)
                        session.Terminal.Paste(text);
                }
            });

            if (restart)
                this.RestartSessionInternal(sessionId);
        }

        /// <summary>
        /// Generates a focus event only when requested by the terminal program
        /// </summary>
        /// <param name="token">Client lease</param>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="focused">True for focus-in</param>
        public void SendFocus(WorkspaceClientToken token, int sessionId, bool focused)
        {
            this._workspaceService.ExecuteMutation(token, () =>
            {
                TerminalSessionRuntime session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    if (!session.Running)
                        return;
                    string sequence = session.Terminal.GenerateFocusEvent(focused);
                    if (!string.IsNullOrEmpty(sequence))
                        session.Shell.SendInput(sequence);
                }
            });
        }

        /// <summary>
        /// Forwards a mouse event only when the terminal enabled the corresponding VT tracking
        /// </summary>
        /// <param name="token">Client lease</param>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="button">Normalized DOM button</param>
        /// <param name="x">Terminal column</param>
        /// <param name="y">Terminal row</param>
        /// <param name="eventType">Mouse event type</param>
        /// <param name="shift">Shift modifier</param>
        /// <param name="control">Control modifier</param>
        /// <param name="alt">Alt modifier</param>
        public void SendMouse(WorkspaceClientToken token, int sessionId, int button, int x, int y, string eventType, bool shift, bool control, bool alt)
        {
            MouseButton mouseButton = button switch
            {
                0 => MouseButton.Left,
                1 => MouseButton.Middle,
                2 => MouseButton.Right,
                64 => MouseButton.WheelUp,
                65 => MouseButton.WheelDown,
                _ => MouseButton.None
            };
            MouseEventType mouseEventType = eventType switch
            {
                "down" => MouseEventType.Down,
                "up" => MouseEventType.Up,
                "move" => MouseEventType.Move,
                "drag" => MouseEventType.Drag,
                "wheel-up" => MouseEventType.WheelUp,
                "wheel-down" => MouseEventType.WheelDown,
                _ => MouseEventType.Move
            };
            KeyModifiers modifiers = this.CreateKeyModifiers(shift, control, alt);

            this._workspaceService.ExecuteMutation(token, () =>
            {
                TerminalSessionRuntime session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    if (!session.Running)
                        return;
                    string sequence = session.Terminal.GenerateMouseEvent(mouseButton, Math.Clamp(x, 0, session.Cols - 1), Math.Clamp(y, 0, session.Rows - 1), mouseEventType, modifiers);
                    if (!string.IsNullOrEmpty(sequence))
                        session.Shell.SendInput(sequence);
                }
            });
        }

        /// <summary>
        /// Resizes the PTY and authoritative emulator
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="cols">Columns</param>
        /// <param name="rows">Rows</param>
        public void ResizeSession(WorkspaceClientToken token, int sessionId, int cols, int rows)
        {
            int safeCols = Math.Clamp(cols, 20, 500);
            int safeRows = Math.Clamp(rows, 5, 200);
            TerminalSessionRuntime session = null;
            bool changed = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    if (session.SynchronizedSnapshot != null)
                    {
                        session.SynchronizedSnapshot = null;
                        changed = true;
                    }
                    if (session.Cols == safeCols && session.Rows == safeRows)
                        return;

                    if (!session.Terminal.SynchronizedOutput)
                    {
                        this.BeginAlternateRedraw(session);
                        session.AlternateFrameReconciler.Reset();
                    }

                    foreach (KeyValuePair<BufferLine, TerminalLineSnapshot> checkpoint in session.DeactivatedNormalLines)
                        if (AreLineSnapshotsEquivalent(SerializeLine(checkpoint.Key, -1), checkpoint.Value))
                            TerminalHistoryRowState.Get(checkpoint.Key).Commit();
                    TerminalHistoryReflow normalReflow = new TerminalHistoryReflow(session.NormalBuffer, safeCols, safeRows, true, line => SerializeLine(line, -1));
                    TerminalHistoryReflow alternateReflow = session.AlternateBuffer == null ? null : new TerminalHistoryReflow(session.AlternateBuffer, safeCols, safeRows, false, line => SerializeLine(line, -1));
                    session.Cols = safeCols;
                    session.Rows = safeRows;
                    session.Terminal.Resize(safeCols, safeRows);
                    normalReflow.Complete(session.History);
                    alternateReflow?.Complete(session.History);
                    session.DeactivatedNormalLines.Clear();
                    for (int row = session.NormalBuffer.BaseY; row < Math.Min(session.NormalBuffer.Lines.Length, session.NormalBuffer.BaseY + session.Rows); row++)
                    {
                        BufferLine line = session.NormalBuffer.Lines[row];
                        TerminalLineSnapshot snapshot = SerializeLine(line, -1);
                        if (TerminalHistoryRowState.Get(line).Extract(snapshot).Count == 0)
                            session.DeactivatedNormalLines[line] = snapshot;
                    }
                    if (session.Running)
                        session.Shell.Resize(safeCols, safeRows);
                    this.AdvanceSessionRevisionLocked(session);
                    changed = true;
                }

                this.IncrementRuntimeRevision();
            });

            if (changed)
            {
                this.EnforceGlobalHistoryBudget();
                this.ScheduleNotification(session, false);
            }
        }

        /// <summary>
        /// Restarts a session after prior validation
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        private void RestartSessionInternal(int sessionId)
        {
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null)
                return;

            ShellService previousShell;
            lock (session.SyncRoot)
            {
                if (session.Disposed || !session.RestartPending)
                    return;
                session.ShellGeneration++;
                session.Running = false;
                previousShell = session.Shell;
                session.Shell = new ShellService();
                session.Terminal.Reset();
                session.DeactivatedNormalLines.Clear();
                session.Exited = false;
                session.ExitedAtUtc = null;
                this.AdvanceSessionRevisionLocked(session);
            }

            // Release the old PTY outside the lock before starting the new generation
            previousShell.Dispose();
            if (!this.StartShell(session))
                return;
            this.IncrementRuntimeRevision();
            this.ScheduleNotification(session, true);
        }

        /// <summary>
        /// Explicitly closes a session and terminates its PTY
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="sessionId">Session identifier</param>
        public void CloseSession(WorkspaceClientToken token, int sessionId)
        {
            TerminalSessionRuntime session = null;
            bool changed = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                lock (this._lock)
                {
                    if (!this._sessions.Remove(sessionId, out session))
                        return;

                    if (this._activeSessionId == sessionId)
                        this._activeSessionId = this.GetLastSessionIdLocked();
                    this._runtimeRevision++;
                    changed = true;
                }
            });

            if (changed)
            {
                session.Dispose();
                this.NotifySubscribers(new TerminalRuntimeEvent { SessionId = sessionId, Revision = this.GetRuntimeRevision(), SessionsChanged = true });
            }
        }

        /// <summary>Removes the PTYs under the caller's workspace lock; returns only the cleanup to run outside the lock</summary>
        /// <returns>Disposal and notification of exactly the removed sessions, never of new PTYs</returns>
        internal Action DetachAllSessionsForWorkspaceReset()
        {
            List<TerminalSessionRuntime> sessions;
            lock (this._lock)
            {
                sessions = new List<TerminalSessionRuntime>(this._sessions.Values);
                this._sessions.Clear();
                this._activeSessionId = 0;
                this._runtimeRevision++;
            }
            return () =>
            {
                this.DisposeSessions(sessions);
                this.NotifySubscribers(new TerminalRuntimeEvent { SessionId = 0, Revision = this.GetRuntimeRevision(), SessionsChanged = true });
            };
        }

        /// <summary>
        /// Returns a bounded session snapshot
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="cancellationToken">Current request token</param>
        /// <returns>Snapshot or null</returns>
        public TerminalSessionSnapshot GetSessionSnapshot(int sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null)
                return null;

            lock (session.SyncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.Disposed)
                    return null;
                return this.CreateSessionSnapshot(session);
            }
        }

        /// <summary>
        /// Atomically captures the snapshot and history tail at the same revision
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="cancellationToken">Requesting circuit token</param>
        /// <returns>Atomic handoff or null</returns>
        public TerminalAttachSnapshot GetAttachSnapshot(int sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null)
                return null;

            lock (session.SyncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.Disposed)
                    return null;
                // A handoff must immediately recover the current geometry and history
                if (session.SynchronizedSnapshot != null)
                {
                    session.SynchronizedSnapshot = null;
                    this.ScheduleNotification(session, false);
                }
                TerminalSessionSnapshot snapshot = this.CreateSessionSnapshot(session);
                long tailStart = Math.Max(snapshot.HistoryStart, snapshot.HistoryEnd - snapshot.HistoryPageRows);
                TerminalAttachSnapshot result = new TerminalAttachSnapshot();
                result.Session = snapshot;
                result.HistoryTail = session.History.GetPage(tailStart, snapshot.HistoryPageRows, snapshot.Revision, cancellationToken);
                return result;
            }
        }

        /// <summary>
        /// Returns a bounded patch after the client-declared revision
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="fromRevision">Revision currently applied by the client</param>
        /// <param name="cancellationToken">Requesting circuit token</param>
        /// <returns>Patch or explicit resynchronization request</returns>
        public TerminalSessionPatch GetSessionPatch(int sessionId, long fromRevision, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null)
                return new TerminalSessionPatch { FromRevision = fromRevision, ToRevision = fromRevision, RequiresResync = true };

            lock (session.SyncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.Disposed)
                    return new TerminalSessionPatch { FromRevision = fromRevision, ToRevision = fromRevision, RequiresResync = true };
                TerminalSessionPatch result = new TerminalSessionPatch();
                result.FromRevision = fromRevision;
                long visibleRevision = this.IsSynchronizedOutputHeld(session) ? session.SynchronizedSnapshot.Revision : session.Revision;
                result.ToRevision = visibleRevision;
                long minimumRevision = session.SynchronizedSnapshot != null ? Math.Max(0, visibleRevision - PATCH_REVISION_WINDOW) : session.MinimumPatchRevision;
                result.RequiresResync = fromRevision > visibleRevision || fromRevision < minimumRevision;
                if (!result.RequiresResync && fromRevision < visibleRevision)
                {
                    result.Session = this.CreateSessionSnapshot(session, fromRevision < session.PaletteRevision);
                    result.ToRevision = result.Session.Revision;
                }
                return result;
            }
        }

        /// <summary>
        /// Returns a bounded history page
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="start">First logical row</param>
        /// <param name="count">Maximum row count</param>
        /// <param name="cancellationToken">Current request token</param>
        /// <returns>History page</returns>
        public TerminalHistoryPage GetHistoryPage(int sessionId, long start, int count, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null)
                return new TerminalHistoryPage();

            lock (session.SyncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.Disposed)
                    return new TerminalHistoryPage();
                if (this.IsSynchronizedOutputHeld(session))
                {
                    TerminalSessionSnapshot visible = session.SynchronizedSnapshot;
                    long safeStart = Math.Clamp(start, visible.HistoryStart, visible.HistoryEnd);
                    int safeCount = (int)Math.Min(Math.Clamp(count, 1, 1000), visible.HistoryEnd - safeStart);
                    TerminalHistoryPage page = session.History.GetPage(safeStart, Math.Max(1, safeCount), visible.Revision, cancellationToken);
                    if (safeCount == 0)
                        page.Lines = Array.Empty<TerminalLineSnapshot>();
                    page.End = visible.HistoryEnd;
                    page.Truncated = visible.HistoryTruncated;
                    return page;
                }
                return session.History.GetPage(start, count, session.Revision, cancellationToken);
            }
        }

        /// <summary>
        /// Finds the adjacent OSC 133 prompt in the visible normal-buffer timeline
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="fromRow">Current logical history/screen row</param>
        /// <param name="previous">True to search backward</param>
        /// <returns>Logical prompt row, or -1 when unavailable</returns>
        public long FindPrompt(int sessionId, long fromRow, bool previous)
        {
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null)
                return -1;

            lock (session.SyncRoot)
            {
                if (session.Disposed)
                    return -1;
                TerminalSessionSnapshot snapshot = this.CreateSessionSnapshot(session);
                if (snapshot.Screen.AlternateBuffer || !snapshot.Screen.ShellIntegrationAvailable)
                    return -1;

                IReadOnlyList<TerminalLineSnapshot> lines = snapshot.Screen.Lines;
                long screenStart = snapshot.HistoryEnd;
                if (previous)
                {
                    int row = (int)Math.Clamp(fromRow - screenStart - 1L, -1L, lines.Count - 1L);
                    for (int i = row; i >= 0; i--)
                        if (lines[i].PromptColumn >= 0)
                            return screenStart + i;
                    long before = Math.Min(fromRow, snapshot.HistoryEnd);
                    return session.History.TryFindPreviousPrompt(before, out long index) ? index : -1;
                }

                if (fromRow < snapshot.HistoryEnd - 1 && session.History.TryFindNextPrompt(fromRow, out long historyIndex) && historyIndex < snapshot.HistoryEnd)
                    return historyIndex;
                int firstScreenRow = (int)Math.Clamp(fromRow - screenStart + 1L, 0L, lines.Count);
                for (int i = firstScreenRow; i < lines.Count; i++)
                    if (lines[i].PromptColumn >= 0)
                        return screenStart + i;
                return -1;
            }
        }

        /// <summary>
        /// Captures all retained history and the current screen without holding the session lock during download
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="cancellationToken">Current request token</param>
        /// <returns>Point-in-time export source or null</returns>
        internal TerminalHistoryExportSnapshot GetHistoryExportSnapshot(int sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null)
                return null;

            lock (session.SyncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.Disposed)
                    return null;

                TerminalHistoryExportSnapshot result = new TerminalHistoryExportSnapshot();
                result.SessionId = session.Id;
                result.Truncated = session.History.Truncated;
                result.HistoryLines = session.History.GetLinesSnapshot(cancellationToken);
                result.Screen = this.CreateHistoryExportScreenSnapshot(session);
                return result;
            }
        }

        /// <summary>
        /// Registers a temporary subscriber for bounded runtime events
        /// </summary>
        /// <param name="subscriber">Client callback</param>
        /// <param name="cancellationToken">Token that automatically detaches the subscriber</param>
        /// <returns>Subscription to release when detaching</returns>
        public IDisposable Subscribe(Action<TerminalRuntimeEvent> subscriber, CancellationToken cancellationToken = default)
        {
            IDisposable result;

            lock (this._lock)
            {
                result = this.RegisterSubscriber(subscriber, cancellationToken);
            }

            return result;
        }

        /// <summary>
        /// Stops every PTY during application shutdown
        /// </summary>
        public void Stop()
        {
            List<TerminalSessionRuntime> sessions;
            lock (this._lock)
            {
                if (!this.TryBeginStop())
                    return;

                sessions = new List<TerminalSessionRuntime>(this._sessions.Values);
                this._sessions.Clear();
            }

            this._maintenanceCancellation.Cancel();
            this._maintenanceTask.GetAwaiter().GetResult();

            this.DisposeSessions(sessions);
        }

        /// <summary>
        /// Releases the runtime idempotently
        /// </summary>
        public void Dispose()
        {
            this.Stop();
            this._settingsSubscription.Dispose();
            this._maintenanceCancellation.Dispose();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Protected Methods

        /// <summary>
        /// Logs a runtime callback failure without terminal content
        /// </summary>
        /// <param name="exception">Exception raised by the subscriber</param>
        protected override void HandleSubscriberException(Exception exception)
        {
            this._logger.LogWarning(exception, "Terminal subscriber notification failed");
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Starts the shell owned by a session
        /// </summary>
        /// <param name="session">Session to start</param>
        /// <returns>True when the session was not already closed</returns>
        private bool StartShell(TerminalSessionRuntime session)
        {
            int generation;

            // Freeze the generation before startup to discard late callbacks from previous shells
            lock (session.SyncRoot)
            {
                if (session.Disposed)
                    return false;
                generation = ++session.ShellGeneration;
                session.Running = true;
                session.Exited = false;
                session.RestartPending = false;
                session.ExitedAtUtc = null;
                session.ProgressState = TerminalProgressState.None;
                session.ProgressValue = 0;
            }

            try
            {
                session.Shell.Start(session.WorkingDirectory, session.Cols, session.Rows, data => this.HandleShellOutputBytes(session.Id, generation, data), () => this.HandleShellExit(session.Id, generation));
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "Failed to start terminal session {SessionId}", session.Id);
                this.HandleShellOutput(session.Id, generation, "\r\n[Failed to start terminal]\r\n");
                this.HandleShellExit(session.Id, generation);
            }

            return true;
        }

        /// <summary>
        /// Always feeds the headless emulator and history, even without clients
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="generation">Shell generation</param>
        /// <param name="data">PTY output</param>
        private void HandleShellOutput(int sessionId, int generation, string data)
        {
            if (string.IsNullOrEmpty(data))
                return;
            this.HandleShellOutputBytes(sessionId, generation, Encoding.UTF8.GetBytes(data));
        }

        /// <summary>
        /// Feeds raw PTY bytes to the emulator without an intermediate UTF-16 string
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="generation">Shell generation</param>
        /// <param name="data">PTY output bytes</param>
        private void HandleShellOutputBytes(int sessionId, int generation, ReadOnlyMemory<byte> data)
        {
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null || data.IsEmpty)
                return;

            int activeSessionId;
            lock (this._lock)
            {
                activeSessionId = this._activeSessionId;
            }

            // XTerm.NET must always receive output even when no circuit is connected
            TerminalClientEvent[] clipboardRequests;
            lock (session.SyncRoot)
            {
                if (generation != session.ShellGeneration || session.Disposed)
                    return;

                session.Terminal.Write(data.Span);
                this.ReconcileAlternateAfterWrite(session);
                session.LastOutputUtc = DateTime.UtcNow;
                this.AdvanceSessionRevisionLocked(session);
                if (session.PaletteChangePending)
                {
                    session.PaletteRevision = session.Revision;
                    session.PaletteChangePending = false;
                }
                session.HasUnreadOutput = sessionId != activeSessionId;
                clipboardRequests = session.WorkspaceClipboardRequests.ToArray();
                session.WorkspaceClipboardRequests.Clear();
            }

            // Process budgets and notifications after the session commit to avoid holding its lock
            foreach (TerminalClientEvent request in clipboardRequests)
                this._workspaceService.EnqueueTerminalClipboard(request, () => ReferenceEquals(this.GetSession(session.Id), session));
            this.EnforceGlobalHistoryBudget();
            this.IncrementRuntimeRevision();
            this.ScheduleNotification(session, false);
        }

        /// <summary>
        /// Marks a shell exited without removing the inspectable session
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="generation">Shell generation</param>
        private void HandleShellExit(int sessionId, int generation)
        {
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null)
                return;

            lock (session.SyncRoot)
            {
                if (generation != session.ShellGeneration || session.Disposed)
                    return;

                session.SynchronizedSnapshot = null;
                session.Running = false;
                session.Exited = true;
                session.ExitedAtUtc = DateTime.UtcNow;
                session.ProgressState = TerminalProgressState.None;
                session.ProgressValue = 0;
                session.Terminal.Write("\r\n[Process exited. Press any key to restart]\r\n");
                this.AdvanceSessionRevisionLocked(session);
            }

            this.IncrementRuntimeRevision();
            this.ScheduleNotification(session, true);
        }

        /// <summary>
        /// Creates a serializable session copy under its lock
        /// </summary>
        /// <param name="session">Source session</param>
        /// <returns>Bounded snapshot</returns>
        private TerminalSessionSnapshot CreateSessionSnapshot(TerminalSessionRuntime session, bool includeColors = true)
        {
            if (this.IsSynchronizedOutputHeld(session))
                return session.SynchronizedSnapshot;

            TerminalSessionSnapshot result = new TerminalSessionSnapshot();
            result.Id = session.Id;
            result.Label = session.Label;
            result.WorkingDirectory = session.WorkingDirectory;
            result.Running = session.Running;
            result.Exited = session.Exited;
            result.HasUnreadOutput = session.HasUnreadOutput;
            result.ProgressState = session.ProgressState;
            result.ProgressValue = session.ProgressValue;
            result.AttentionRequested = session.AttentionRequested;
            result.Cols = session.Cols;
            result.Rows = session.Rows;
            result.Revision = session.Revision;
            result.HistoryStart = session.History.StartIndex;
            result.HistoryEnd = session.History.EndIndex;
            result.HistoryTruncated = session.History.Truncated;
            result.HistoryBytes = session.History.RetainedBytes;
            result.HistoryPageRows = session.HistoryPageRows;
            result.Screen = this.CreateScreenSnapshot(session, includeColors);
            return result;
        }

        /// <summary>
        /// Creates a snapshot of the current screen only
        /// </summary>
        /// <param name="session">Source session</param>
        /// <returns>Bounded screen snapshot</returns>
        private TerminalScreenSnapshot CreateScreenSnapshot(TerminalSessionRuntime session, bool includeColors = true)
        {
            TerminalScreenSnapshot result = new TerminalScreenSnapshot();
            result.ColorsIncluded = includeColors;
            if (includeColors)
            {
                ColorSnapshot colors = session.Terminal.Colors.Take();
                int[] palette = new int[ColorPalette.Size];
                for (int i = 0; i < palette.Length; i++)
                    palette[i] = colors[i];
                result.Palette = Array.AsReadOnly(palette);
                result.DefaultForeground = colors.Foreground;
                result.DefaultBackground = colors.Background;
                result.CursorColor = colors.Cursor;
            }
            result.AlternateBuffer = session.Terminal.IsAlternateBufferActive;
            result.CursorX = session.Terminal.Buffer.X;
            result.CursorY = session.Terminal.Buffer.Y;
            result.CursorVisible = session.Terminal.CursorVisible;
            result.MouseTracking = session.Terminal.MouseTrackingMode != MouseTrackingMode.None;
            result.KittyKeyboardActive = session.Terminal.KittyKeyboardActive;
            result.ShellIntegrationAvailable = session.Terminal.ShellIntegrationState != null;

            List<TerminalLineSnapshot> lines = new List<TerminalLineSnapshot>();
            int firstLine = session.Terminal.Buffer.BaseY;
            for (int i = 0; i < session.Rows; i++)
            {
                BufferLine line = firstLine + i < session.Terminal.Buffer.Lines.Length ? session.Terminal.Buffer.Lines[firstLine + i] : null;
                lines.Add(SerializeLine(line, -1));
            }

            result.Lines = lines.AsReadOnly();
            return result;
        }

        /// <summary>
        /// Exports only the fragments not yet present in the history
        /// </summary>
        /// <param name="session">Source session</param>
        /// <returns>Compact current rows not already present in the archive</returns>
        private TerminalScreenSnapshot CreateHistoryExportScreenSnapshot(TerminalSessionRuntime session)
        {
            TerminalScreenSnapshot result = this.CreateScreenSnapshot(session);
            List<TerminalLineSnapshot> lines = new List<TerminalLineSnapshot>();
            int firstLine = session.Terminal.Buffer.BaseY;
            for (int i = 0; i < result.Lines.Count; i++)
            {
                BufferLine line = firstLine + i < session.Terminal.Buffer.Lines.Length ? session.Terminal.Buffer.Lines[firstLine + i] : null;
                if (line == null)
                    continue;
                TerminalHistoryRowState state = TerminalHistoryRowState.Get(line);
                if (!result.AlternateBuffer && session.DeactivatedNormalLines.TryGetValue(line, out TerminalLineSnapshot checkpoint) && AreLineSnapshotsEquivalent(result.Lines[i], checkpoint))
                    state.Commit();
                lines.AddRange(state.Extract(result.Lines[i]));
            }

            result.Lines = lines.AsReadOnly();
            result.CursorY = lines.Count - 1;
            return result;
        }

        /// <summary>
        /// Serializes an XTerm.NET row while preserving cells, attributes and wrapping
        /// </summary>
        /// <param name="line">Headless row</param>
        /// <param name="index">Logical offset</param>
        /// <returns>Serialized row</returns>
        private static TerminalLineSnapshot SerializeLine(BufferLine line, long index)
        {
            TerminalLineSnapshot result = new TerminalLineSnapshot();
            result.Index = index;
            result.PromptColumn = -1;
            if (line == null)
                return result;

            for (int i = 0; i < line.Marks.Count; i++)
            {
                if (line.Marks[i].Kind == ShellIntegrationMark.PromptStart)
                {
                    result.PromptColumn = line.Marks[i].Column;
                    break;
                }
            }

            // Serialize only significant cells while retaining attributes required by the remote renderer
            int length = line.GetTrimmedLength();
            List<TerminalCellSnapshot> cells = new List<TerminalCellSnapshot>();
            StringBuilder text = new StringBuilder();
            int serializedBytes = 32;
            IReadOnlyList<LineHyperlink> links = line.Links;
            int linkIndex = 0;
            for (int i = 0; i < length; i++)
            {
                BufferCell cell = line[i];
                TerminalCellSnapshot cellSnapshot = SerializeCell(cell);
                while (linkIndex < links.Count && links[linkIndex].EndColumn <= i)
                    linkIndex++;
                cellSnapshot.Hyperlink = linkIndex < links.Count && links[linkIndex].Column <= i ? links[linkIndex].Url : "";
                cells.Add(cellSnapshot);
                if (cell.Width > 0)
                    text.Append(cellSnapshot.Content);
                serializedBytes += 40 + Encoding.UTF8.GetByteCount(cellSnapshot.Content) + Encoding.UTF8.GetByteCount(cellSnapshot.Hyperlink);
            }

            result.Wrapped = line.IsWrapped;
            result.LineAttribute = line.LineAttribute switch
            {
                LineAttribute.DoubleWidth => TerminalLineAttribute.DoubleWidth,
                LineAttribute.DoubleHeightTop => TerminalLineAttribute.DoubleHeightTop,
                LineAttribute.DoubleHeightBottom => TerminalLineAttribute.DoubleHeightBottom,
                _ => TerminalLineAttribute.Normal
            };
            result.Cells = cells.AsReadOnly();
            result.Text = text.ToString();

            // The budget includes UTF-8 payload and stable row and cell overhead
            result.SerializedBytes = serializedBytes + 4 + Encoding.UTF8.GetByteCount(result.Text);
            return result;
        }

        /// <summary>
        /// Converts an XTerm.NET cell into the explicit renderer protocol
        /// </summary>
        /// <param name="cell">Public cell from the headless buffer</param>
        /// <returns>Snapshot with colors and styles separated from internal library bits</returns>
        internal static TerminalCellSnapshot SerializeCell(BufferCell cell)
        {
            TerminalCellSnapshot result = new TerminalCellSnapshot();
            result.Content = cell.Content ?? "";
            result.Width = cell.Width;
            result.Foreground = cell.Attributes.GetFgColor();
            result.ForegroundMode = (TerminalColorMode)cell.Attributes.GetFgColorMode();
            result.Background = cell.Attributes.GetBgColor();
            result.BackgroundMode = (TerminalColorMode)cell.Attributes.GetBgColorMode();
            result.UnderlineStyle = (TerminalUnderlineStyle)cell.Attributes.GetUnderlineStyle();
            if (cell.Attributes.TryGetUnderlineColor(out int underlineColor, out int underlineColorMode))
            {
                result.HasUnderlineColor = true;
                result.UnderlineColor = underlineColor;
                result.UnderlineColorMode = (TerminalColorMode)underlineColorMode;
            }
            TerminalCellAttributes attributes = TerminalCellAttributes.None;
            if (cell.Attributes.IsBold())
                attributes |= TerminalCellAttributes.Bold;
            if (cell.Attributes.IsDim())
                attributes |= TerminalCellAttributes.Dim;
            if (cell.Attributes.IsItalic())
                attributes |= TerminalCellAttributes.Italic;
            if (cell.Attributes.IsUnderline())
                attributes |= TerminalCellAttributes.Underline;
            if (cell.Attributes.IsBlink())
                attributes |= TerminalCellAttributes.Blink;
            if (cell.Attributes.IsInverse())
                attributes |= TerminalCellAttributes.Inverse;
            if (cell.Attributes.IsInvisible())
                attributes |= TerminalCellAttributes.Invisible;
            if (cell.Attributes.IsStrikethrough())
                attributes |= TerminalCellAttributes.Strikethrough;
            if (cell.Attributes.IsOverline())
                attributes |= TerminalCellAttributes.Overline;
            result.Attributes = attributes;
            return result;
        }

        /// <summary>
        /// Captures a row before it leaves the active terminal viewport
        /// </summary>
        /// <param name="session">Affected session</param>
        /// <param name="args">Exited row and source buffer</param>
        private void CaptureExitedLine(TerminalSessionRuntime session, TerminalEvents.LineExitedViewportEventArgs args)
        {
            BufferLine line = args.Line;
            TerminalLineSnapshot snapshot = SerializeLine(line, session.History.EndIndex);
            TerminalHistoryRowState state = TerminalHistoryRowState.Get(line);
            if (args.Buffer == BufferType.Normal && session.DeactivatedNormalLines.TryGetValue(line, out TerminalLineSnapshot archivedLine) && AreLineSnapshotsEquivalent(snapshot, archivedLine))
                state.Commit();

            foreach (TerminalLineSnapshot fragment in state.Extract(snapshot))
                session.History.Append(fragment);
            state.Commit();

            if (args.Buffer == BufferType.Normal && args.Reason == LineExitReason.BufferDeactivated)
                session.DeactivatedNormalLines[line] = snapshot;
            if (args.Reason == LineExitReason.Scrolled)
            {
                session.DeactivatedNormalLines.Remove(line);
            }
        }

        /// <summary>
        /// Preserves the public references of both buffers, resized together by XTerm
        /// </summary>
        private void HandleBufferChanged(TerminalSessionRuntime session, TerminalEvents.BufferChangedEventArgs args)
        {
            session.AlternateFrameReconciler.Reset();
            if (args.Buffer == BufferType.Alternate)
                session.AlternateBuffer = session.Terminal.Buffer;
        }

        /// <summary>
        /// Freezes the alternate frame at a potential redraw boundary
        /// </summary>
        private void HandleViewportRedrawStarting(TerminalSessionRuntime session)
        {
            if (!session.Terminal.SynchronizedOutput)
                this.BeginAlternateRedraw(session);
        }

        /// <summary>
        /// Freezes an alternate buffer boundary before the changes
        /// </summary>
        private void BeginAlternateRedraw(TerminalSessionRuntime session)
        {
            if (!session.Terminal.IsAlternateBufferActive)
                return;

            session.AlternateFrameReconciler.BeforeRedraw(session.Terminal.Buffer, session.Rows, line => SerializeLine(line, -1), session.History.Append);
        }

        /// <summary>
        /// Reconciles the redraw only when the previous boundary requires it
        /// </summary>
        /// <param name="session">Session under its own lock</param>
        /// <param name="complete">True at the end of an atomic update</param>
        private void ReconcileAlternateAfterWrite(TerminalSessionRuntime session, bool complete = false)
        {
            if (!session.Terminal.IsAlternateBufferActive || session.Terminal.SynchronizedOutput || !session.AlternateFrameReconciler.Pending)
                return;

            if (complete)
                session.AlternateFrameReconciler.CompleteWrite(session.Terminal.Buffer, session.Rows, line => SerializeLine(line, -1), session.History.Append);
            else
                session.AlternateFrameReconciler.AfterWrite(session.Terminal.Buffer, session.Rows, line => SerializeLine(line, -1), session.History.Append);
        }

        /// <summary>
        /// Marks the revision that must carry a new coherent palette to existing renderers
        /// </summary>
        /// <param name="session">Affected session</param>
        private void HandleColorChanged(TerminalSessionRuntime session)
        {
            session.PaletteChangePending = true;
        }

        /// <summary>
        /// Publishes a plain-text clipboard request for explicit browser confirmation
        /// </summary>
        private void HandleClipboardWriteRequested(TerminalSessionRuntime session, TerminalEvents.ClipboardWriteEventArgs args)
        {
            TerminalEvents.ClipboardFormat? plainText = null;
            for (int i = 0; i < args.Formats.Count; i++)
            {
                if (string.Equals(args.Formats[i].MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
                {
                    plainText = args.Formats[i];
                    break;
                }
            }
            if (plainText == null || plainText.Value.Data.Length > MAX_CLIPBOARD_BYTES)
                return;

            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(plainText.Value.Data);
            }
            catch (DecoderFallbackException)
            {
                return;
            }

            this.PublishClientEvent(session, TerminalClientEventType.ClipboardWrite, "Copy from " + session.Label, text);
        }

        /// <summary>
        /// Publishes one bounded in-page notification
        /// </summary>
        private void HandleNotificationReceived(TerminalSessionRuntime session, TerminalEvents.NotificationEventArgs args)
        {
            string title = string.IsNullOrWhiteSpace(args.Title) ? session.Label : args.Title;
            string body = string.IsNullOrWhiteSpace(args.Body) ? args.Text : args.Body;
            if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(title))
                return;
            this.PublishClientEvent(session, TerminalClientEventType.Notification, title, body);
        }

        /// <summary>
        /// Stores progress in the session snapshot
        /// </summary>
        private void HandleProgressChanged(TerminalSessionRuntime session, TerminalEvents.ProgressEventArgs args)
        {
            session.ProgressState = (TerminalProgressState)(int)args.State;
            session.ProgressValue = args.Value;
        }

        /// <summary>
        /// Marks the originating tab until the user opens it
        /// </summary>
        private void HandleAttentionRequested(TerminalSessionRuntime session)
        {
            session.AttentionRequested = true;
            this.PublishClientEvent(session, TerminalClientEventType.Attention, "", "");
        }

        /// <summary>
        /// Sends a transient UI request without logging terminal content
        /// </summary>
        private void PublishClientEvent(TerminalSessionRuntime session, TerminalClientEventType type, string title, string text)
        {
            TerminalClientEvent clientEvent = new TerminalClientEvent
            {
                Id = Interlocked.Increment(ref this._nextClientEventId),
                SessionId = session.Id,
                Type = type,
                Title = title ?? "",
                Text = text ?? ""
            };
            if (type == TerminalClientEventType.ClipboardWrite)
            {
                if (session.WorkspaceClipboardRequests.Count < 8)
                    session.WorkspaceClipboardRequests.Add(clientEvent);
                return;
            }
            this.NotifySubscribers(new TerminalRuntimeEvent
            {
                SessionId = session.Id,
                Revision = session.Revision,
                ClientEvents = new[] { clientEvent }
            });
        }

        /// <summary>
        /// Determines whether two serialized rows contain the same terminal state
        /// </summary>
        /// <param name="left">First row</param>
        /// <param name="right">Second row</param>
        /// <returns>True when text, wrapping and cells are equal</returns>
        private static bool AreLineSnapshotsEquivalent(TerminalLineSnapshot left, TerminalLineSnapshot right)
        {
            if (left == null || right == null || left.Text != right.Text || left.Wrapped != right.Wrapped || left.LineAttribute != right.LineAttribute || left.PromptColumn != right.PromptColumn || left.Cells.Count != right.Cells.Count)
                return false;

            for (int i = 0; i < left.Cells.Count; i++)
            {
                TerminalCellSnapshot leftCell = left.Cells[i];
                TerminalCellSnapshot rightCell = right.Cells[i];
                if (leftCell.Content != rightCell.Content || leftCell.Width != rightCell.Width || leftCell.Foreground != rightCell.Foreground || leftCell.ForegroundMode != rightCell.ForegroundMode || leftCell.Background != rightCell.Background || leftCell.BackgroundMode != rightCell.BackgroundMode || leftCell.Attributes != rightCell.Attributes || leftCell.UnderlineStyle != rightCell.UnderlineStyle || leftCell.HasUnderlineColor != rightCell.HasUnderlineColor || leftCell.UnderlineColor != rightCell.UnderlineColor || leftCell.UnderlineColorMode != rightCell.UnderlineColorMode || leftCell.Hyperlink != rightCell.Hyperlink)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Applies the global limit by removing the oldest history segments
        /// </summary>
        private void EnforceGlobalHistoryBudget()
        {
            List<TerminalSessionRuntime> trimmedSessions = new List<TerminalSessionRuntime>();
            lock (this._lock)
            {
                // Calculate the global budget on a consistent session-registry snapshot
                long totalBytes = 0;
                foreach (TerminalSessionRuntime session in this._sessions.Values)
                {
                    lock (session.SyncRoot)
                    {
                        totalBytes += session.History.RetainedBytes;
                    }
                }

                // Always remove the globally oldest segment to preserve fairness across tabs
                long maxBytes = Math.Max(1024 * 1024, this._settings.MaxHistoryBytesGlobal);
                while (totalBytes > maxBytes)
                {
                    TerminalSessionRuntime oldest = null;
                    DateTime oldestTimestamp = DateTime.MaxValue;
                    foreach (TerminalSessionRuntime session in this._sessions.Values)
                    {
                        lock (session.SyncRoot)
                        {
                            if (session.History.RetainedBytes > 0 && session.History.OldestTimestamp < oldestTimestamp)
                            {
                                oldest = session;
                                oldestTimestamp = session.History.OldestTimestamp;
                            }
                        }
                    }

                    if (oldest == null)
                        break;

                    lock (oldest.SyncRoot)
                    {
                        long removed = oldest.History.TrimOldestSegment();
                        if (removed <= 0)
                            break;
                        totalBytes -= removed;
                        this.AdvanceSessionRevisionLocked(oldest);
                        if (!trimmedSessions.Contains(oldest))
                            trimmedSessions.Add(oldest);
                    }
                }
            }

            if (trimmedSessions.Count > 0)
                this.IncrementRuntimeRevision();
            for (int i = 0; i < trimmedSessions.Count; i++)
                this.ScheduleNotification(trimmedSessions[i], false);
        }

        /// <summary>
        /// Freezes a consistent state before the block writes without stopping the emulator
        /// </summary>
        /// <param name="session">Session under its own lock</param>
        /// <param name="active">Negotiated block state</param>
        private void HandleSynchronizedOutputChanged(TerminalSessionRuntime session, bool active)
        {
            session.SynchronizedSnapshot = null;
            if (active)
            {
                this.BeginAlternateRedraw(session);
                session.SynchronizedSnapshot = this.CreateSessionSnapshot(session);
                session.SynchronizedStartedAt = Environment.TickCount64;
            }
            else
            {
                this.ReconcileAlternateAfterWrite(session, true);
            }
        }

        /// <summary>
        /// Bounds the wait using a monotonic clock, even when no more output arrives
        /// </summary>
        /// <param name="session">Session under its own lock</param>
        /// <returns>True while publishing is suspended</returns>
        private bool IsSynchronizedOutputHeld(TerminalSessionRuntime session)
        {
            if (session.SynchronizedSnapshot == null)
                return false;
            if (session.History.StartIndex == session.SynchronizedSnapshot.HistoryStart && Environment.TickCount64 - session.SynchronizedStartedAt < SYNCHRONIZED_OUTPUT_TIMEOUT_MS)
                return true;

            // Timeout and trimming must not keep a frame whose history is no longer available
            // The interrupted block stays ignored until the next off/on negotiation
            session.SynchronizedSnapshot = null;
            return false;
        }

        /// <summary>
        /// Queues at most one bounded notification per session
        /// </summary>
        /// <param name="session">Affected session</param>
        /// <param name="sessionsChanged">Whether the tab list changed</param>
        private void ScheduleNotification(TerminalSessionRuntime session, bool sessionsChanged)
        {
            // Accumulate changes during the debounce window without creating a queue for each PTY chunk
            lock (session.SyncRoot)
            {
                session.SessionsChangedPending |= sessionsChanged;
                if (session.NotificationPending)
                    return;
                session.NotificationPending = true;
            }

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(NOTIFICATION_DELAY_MS).ConfigureAwait(false);
                    bool changed;
                    long deliveredSessionRevision;
                    lock (session.SyncRoot)
                    {
                        if (session.Disposed)
                            return;
                        if (this.IsSynchronizedOutputHeld(session))
                            continue;
                        changed = session.SessionsChangedPending;
                        session.SessionsChangedPending = false;
                        deliveredSessionRevision = session.Revision;
                    }

                    TerminalRuntimeEvent runtimeEvent = new TerminalRuntimeEvent
                    {
                        SessionId = session.Id,
                        Revision = this.GetRuntimeRevision(),
                        SessionsChanged = changed
                    };
                    this.NotifySubscribers(runtimeEvent);

                    // Complete the worker only when no new revision arrived during delivery
                    lock (session.SyncRoot)
                    {
                        if (session.Disposed)
                            return;
                        if (session.Revision == deliveredSessionRevision && !session.SessionsChangedPending)
                        {
                            session.NotificationPending = false;
                            return;
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Returns a session without retaining the global lock
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <returns>Session or null</returns>
        private TerminalSessionRuntime GetSession(int sessionId)
        {
            TerminalSessionRuntime result;
            lock (this._lock)
            {
                if (this.IsStopped || !this._sessions.TryGetValue(sessionId, out result))
                    return null;
            }

            return result;
        }

        /// <summary>
        /// Generates a sequence from DOM input using authoritative XTerm.NET state
        /// </summary>
        /// <param name="terminal">Emulator with the authoritative negotiated state</param>
        /// <param name="keyName">DOM key name</param>
        /// <param name="character">Printable character</param>
        /// <param name="modifiers">XTerm.NET modifiers</param>
        /// <returns>Sequence to send to the PTY</returns>
        /// <param name="meta">Meta/Super modifier</param>
        /// <param name="code">Physical DOM identity</param>
        /// <param name="repeat">Key repeat</param>
        /// <param name="release">Key release</param>
        internal static string GenerateKeySequence(Terminal terminal, string keyName, string character, KeyModifiers modifiers, bool meta = false, string code = "", bool repeat = false, bool release = false)
        {
            if (terminal.KittyKeyboardActive)
            {
                KeyEvent keyEvent = new KeyEvent
                {
                    Key = keyName ?? "",
                    Code = code ?? "",
                    ShiftKey = (modifiers & KeyModifiers.Shift) != 0,
                    CtrlKey = (modifiers & KeyModifiers.Control) != 0,
                    AltKey = (modifiers & KeyModifiers.Alt) != 0,
                    MetaKey = meta
                };
                KittyKeyboardEventType eventType = release ? KittyKeyboardEventType.Release : repeat ? KittyKeyboardEventType.Repeat : KittyKeyboardEventType.Press;
                return terminal.GenerateKittyKeyInput(keyEvent, eventType) ?? "";
            }
            if (release || meta)
                return "";

            Key? key = keyName switch
            {
                "Enter" => Key.Enter,
                "Tab" => Key.Tab,
                "Backspace" => Key.Backspace,
                "Escape" => Key.Escape,
                " " => Key.Space,
                "ArrowUp" => Key.UpArrow,
                "ArrowDown" => Key.DownArrow,
                "ArrowRight" => Key.RightArrow,
                "ArrowLeft" => Key.LeftArrow,
                "Home" => Key.Home,
                "End" => Key.End,
                "PageUp" => Key.PageUp,
                "PageDown" => Key.PageDown,
                "Insert" => Key.Insert,
                "Delete" => Key.Delete,
                "F1" => Key.F1,
                "F2" => Key.F2,
                "F3" => Key.F3,
                "F4" => Key.F4,
                "F5" => Key.F5,
                "F6" => Key.F6,
                "F7" => Key.F7,
                "F8" => Key.F8,
                "F9" => Key.F9,
                "F10" => Key.F10,
                "F11" => Key.F11,
                "F12" => Key.F12,
                _ => null
            };
            if (key.HasValue)
                return terminal.GenerateKeyInput(key.Value, modifiers);
            if (!string.IsNullOrEmpty(character) && character.Length == 1)
                return terminal.GenerateCharInput(character[0], modifiers);
            return character ?? "";
        }

        /// <summary>
        /// Converts browser modifiers into XTerm.NET flags
        /// </summary>
        /// <param name="shift">Shift modifier</param>
        /// <param name="control">Control modifier</param>
        /// <param name="alt">Alt modifier</param>
        /// <returns>Combined XTerm.NET flags</returns>
        private KeyModifiers CreateKeyModifiers(bool shift, bool control, bool alt)
        {
            KeyModifiers result = KeyModifiers.None;
            if (shift)
                result |= KeyModifiers.Shift;
            if (control)
                result |= KeyModifiers.Control;
            if (alt)
                result |= KeyModifiers.Alt;
            return result;
        }

        /// <summary>
        /// Returns the current global revision
        /// </summary>
        /// <returns>Runtime revision</returns>
        private long GetRuntimeRevision()
        {
            lock (this._lock)
            {
                return this._runtimeRevision;
            }
        }

        /// <summary>
        /// Advances the session revision and bounded journal limit
        /// </summary>
        /// <param name="session">Session already protected by its lock</param>
        private void AdvanceSessionRevisionLocked(TerminalSessionRuntime session)
        {
            session.Revision++;
            session.MinimumPatchRevision = Math.Max(0, session.Revision - PATCH_REVISION_WINDOW);
        }

        /// <summary>
        /// Increments the global revision
        /// </summary>
        private void IncrementRuntimeRevision()
        {
            lock (this._lock)
            {
                this._runtimeRevision++;
            }
        }

        /// <summary>
        /// Returns the last remaining session identifier
        /// </summary>
        /// <returns>Identifier or zero</returns>
        private int GetLastSessionIdLocked()
        {
            int result = 0;
            foreach (int sessionId in this._sessions.Keys)
            {
                if (sessionId > result)
                    result = sessionId;
            }

            return result;
        }

        /// <summary>
        /// Releases sessions already removed from the runtime registry
        /// </summary>
        /// <param name="sessions">Sessions to close outside runtime locks</param>
        private void DisposeSessions(List<TerminalSessionRuntime> sessions)
        {
            if (sessions == null)
                return;

            for (int i = 0; i < sessions.Count; i++)
            {
                sessions[i].Dispose();
            }
        }

        /// <summary>
        /// Removes exited sessions beyond configured retention
        /// </summary>
        /// <returns>Removed sessions to release outside the runtime lock</returns>
        private List<TerminalSessionRuntime> CleanupExpiredSessionsLocked()
        {
            DateTime threshold = DateTime.UtcNow.AddHours(-Math.Max(1, this._settings.ExitedSessionRetentionHours));

            // Identify expired sessions first without modifying the dictionary during enumeration
            List<int> expired = new List<int>();
            foreach (KeyValuePair<int, TerminalSessionRuntime> pair in this._sessions)
            {
                lock (pair.Value.SyncRoot)
                {
                    if (pair.Value.ExitedAtUtc.HasValue && pair.Value.ExitedAtUtc.Value < threshold)
                        expired.Add(pair.Key);
                }
            }

            // Remove from the registry under lock; the caller performs actual disposal outside the lock
            List<TerminalSessionRuntime> result = new List<TerminalSessionRuntime>();
            for (int i = 0; i < expired.Count; i++)
            {
                TerminalSessionRuntime session = this._sessions[expired[i]];
                this._sessions.Remove(expired[i]);
                result.Add(session);
            }

            if (expired.Count > 0)
            {
                if (!this._sessions.ContainsKey(this._activeSessionId))
                    this._activeSessionId = this.GetLastSessionIdLocked();
                this._runtimeRevision++;
            }

            return result;
        }

        /// <summary>
        /// Applies retention and budgets even when no browser is connected
        /// </summary>
        /// <param name="cancellationToken">Application shutdown token</param>
        private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
        {
            try
            {
                using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    List<TerminalSessionRuntime> expiredSessions;

                    // The tick keeps retention atomic while releasing PTYs and notifications outside the global lock
                    lock (this._lock)
                    {
                        if (this.IsStopped)
                            return;
                        expiredSessions = this.CleanupExpiredSessionsLocked();
                    }

                    this.DisposeSessions(expiredSessions);
                    this.EnforceGlobalHistoryBudget();
                    if (expiredSessions.Count > 0)
                        this.NotifySubscribers(new TerminalRuntimeEvent { SessionId = 0, Revision = this.GetRuntimeRevision(), SessionsChanged = true });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Updates configurable limits without destroying current sessions
        /// </summary>
        /// <param name="settings">New settings</param>
        /// <param name="name">Options name</param>
        private void HandleSettingsChanged(CommanderSettings settings, string name)
        {
            lock (this._lock)
            {
                this._settings = settings.TerminalRuntime ?? new TerminalRuntimeSettings();
            }

            this.EnforceGlobalHistoryBudget();
        }

        #endregion

        #region Nested Classes

        /// <summary>
        /// Private runtime state of a session
        /// </summary>
        private sealed class TerminalSessionRuntime : IDisposable
        {
            #region Class Variables

            /// <summary>
            /// Owning runtime used by XTerm.NET callbacks
            /// </summary>
            private readonly TerminalRuntimeService _owner;

            #endregion

            #region Constructor

            /// <summary>
            /// Creates the runtime state of a terminal session
            /// </summary>
            /// <param name="owner">Runtime owning the XTerm.NET callbacks</param>
            /// <param name="id">Stable identifier</param>
            /// <param name="label">Initial label</param>
            /// <param name="workingDirectory">Initial working directory</param>
            /// <param name="settings">Runtime limits to freeze in the session</param>
            public TerminalSessionRuntime(TerminalRuntimeService owner, int id, string label, string workingDirectory, TerminalRuntimeSettings settings)
            {
                this._owner = owner ?? throw new ArgumentNullException(nameof(owner));
                this.Id = id;
                this.Label = label;
                this.WorkingDirectory = workingDirectory;
                this.Cols = 120;
                this.Rows = 30;
                this.HistoryPageRows = Math.Clamp(settings.HistoryPageRows, 25, 1000);
                this.History = new TerminalHistoryArchive(settings.MaxHistoryBytesPerTab, settings.HistorySegmentBytes);
                TerminalOptions terminalOptions = new TerminalOptions
                {
                    Cols = this.Cols,
                    Rows = this.Rows,
                    Scrollback = Math.Max(256, settings.HeadlessScrollbackRows),
                    TermName = "xterm-256color",
                    ConvertEol = true,
                    SixelEnabled = false,
                    KittyGraphicsEnabled = false,
                    ITerm2ImagesEnabled = false,
                    KittyKeyboardEnabled = true,
                    KittyNotificationsEnabled = true,
                    ClipboardWriteEnabled = true,
                    ClipboardReadEnabled = false,
                    PointerShapesEnabled = false,
                    AllowPasteControls = false,
                    MaxClipboardBytes = MAX_CLIPBOARD_BYTES,
                    WindowOptions = new WindowOptions { RequestAttention = true }
                };
                this.Terminal = new Terminal(terminalOptions);
                this.NormalBuffer = this.Terminal.Buffer;
                this.Shell = new ShellService();
                this.Terminal.LineExitedViewport += (sender, args) => this._owner.CaptureExitedLine(this, args);
                this.Terminal.ViewportRedrawStarting += (sender, args) => this._owner.HandleViewportRedrawStarting(this);
                this.Terminal.BufferChanged += (sender, args) => this._owner.HandleBufferChanged(this, args);
                this.Terminal.SynchronizedOutputChanged += (sender, args) => this._owner.HandleSynchronizedOutputChanged(this, args.Active);
                this.Terminal.Colors.ColorChanged += (sender, args) => this._owner.HandleColorChanged(this);
                this.Terminal.ClipboardWriteRequested += (sender, args) => this._owner.HandleClipboardWriteRequested(this, args);
                this.Terminal.NotificationReceived += (sender, args) => this._owner.HandleNotificationReceived(this, args);
                this.Terminal.ProgressChanged += (sender, args) => this._owner.HandleProgressChanged(this, args);
                this.Terminal.AttentionRequested += (sender, args) => this._owner.HandleAttentionRequested(this);
                this.Terminal.DataReceived += (sender, args) => this.Shell.SendInput(args.Data);
            }

            #endregion

            #region Properties

            /// <summary>
            /// Synchronizes individual session state
            /// </summary>
            public object SyncRoot { get; } = new object();

            /// <summary>
            /// Stable session identifier
            /// </summary>
            public int Id { get; }

            /// <summary>
            /// Label displayed in the tab
            /// </summary>
            public string Label { get; set; }

            /// <summary>
            /// Initial working directory
            /// </summary>
            public string WorkingDirectory { get; }

            /// <summary>
            /// Current columns
            /// </summary>
            public int Cols { get; set; }

            /// <summary>
            /// Current rows
            /// </summary>
            public int Rows { get; set; }

            /// <summary>
            /// Configured history page size
            /// </summary>
            public int HistoryPageRows { get; }

            /// <summary>
            /// Whether the process is running
            /// </summary>
            public bool Running { get; set; }

            /// <summary>
            /// Whether the process has exited
            /// </summary>
            public bool Exited { get; set; }

            /// <summary>
            /// Whether a restart was requested
            /// </summary>
            public bool RestartPending { get; set; }
            /// <summary>Publications captured after Terminal.Write, before the browser notification</summary>
            public List<TerminalClientEvent> WorkspaceClipboardRequests { get; } = new List<TerminalClientEvent>();

            /// <summary>
            /// Whether the tab has unread output
            /// </summary>
            public bool HasUnreadOutput { get; set; }

            /// <summary>
            /// Progress explicitly reported by the terminal application
            /// </summary>
            public TerminalProgressState ProgressState { get; set; }

            /// <summary>
            /// Determinate progress percentage
            /// </summary>
            public int ProgressValue { get; set; }

            /// <summary>
            /// Whether the application requested attention
            /// </summary>
            public bool AttentionRequested { get; set; }

            /// <summary>
            /// Whether a notification is already queued
            /// </summary>
            public bool NotificationPending { get; set; }

            /// <summary>
            /// Last complete state, with consistent revision and history boundary, during DEC 2026
            /// </summary>
            public TerminalSessionSnapshot SynchronizedSnapshot { get; set; }

            /// <summary>
            /// Monotonic start of the wait for the current block
            /// </summary>
            public long SynchronizedStartedAt { get; set; }

            /// <summary>
            /// Accumulates a session-list change
            /// </summary>
            public bool SessionsChangedPending { get; set; }

            /// <summary>
            /// Whether session resources were released
            /// </summary>
            public bool Disposed { get; private set; }

            /// <summary>
            /// Shell generation used to discard stale callbacks
            /// </summary>
            public int ShellGeneration { get; set; }

            /// <summary>
            /// Monotonic session revision
            /// </summary>
            public long Revision { get; set; }

            /// <summary>
            /// Minimum revision still covered by the bounded journal
            /// </summary>
            public long MinimumPatchRevision { get; set; }

            /// <summary>
            /// Last revision that changed the palette or a special color
            /// </summary>
            public long PaletteRevision { get; set; }

            /// <summary>
            /// Whether the current PTY chunk changed terminal colors
            /// </summary>
            public bool PaletteChangePending { get; set; }

            /// <summary>
            /// Creation UTC instant
            /// </summary>
            public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

            /// <summary>
            /// Last-output UTC instant
            /// </summary>
            public DateTime LastOutputUtc { get; set; } = DateTime.UtcNow;

            /// <summary>
            /// Process-exit UTC instant
            /// </summary>
            public DateTime? ExitedAtUtc { get; set; }

            /// <summary>
            /// PTY connection owned by the session
            /// </summary>
            public ShellService Shell { get; set; }

            /// <summary>
            /// Authoritative XTerm.NET emulator
            /// </summary>
            public Terminal Terminal { get; }

            /// <summary>
            /// Segmented history archive
            /// </summary>
            public TerminalHistoryArchive History { get; }

            /// <summary>
            /// Bounded candidate used only for alternate buffer frames
            /// </summary>
            public TerminalAlternateFrameReconciler AlternateFrameReconciler { get; } = new TerminalAlternateFrameReconciler();

            /// <summary>
            /// Checkpoint of the fully archived normal rows, including re-entries after resize
            /// </summary>
            public Dictionary<BufferLine, TerminalLineSnapshot> DeactivatedNormalLines { get; set; } = new Dictionary<BufferLine, TerminalLineSnapshot>();

            /// <summary>
            /// Normal buffer preserved even while the alternate one is active
            /// </summary>
            public TerminalBuffer NormalBuffer { get; }

            /// <summary>
            /// Alternate buffer acquired at first activation
            /// </summary>
            public TerminalBuffer AlternateBuffer { get; set; }

            #endregion

            #region Public Methods

            /// <summary>
            /// Terminates the PTY and releases the emulator idempotently
            /// </summary>
            public void Dispose()
            {
                ShellService shell;
                Terminal terminal;
                lock (this.SyncRoot)
                {
                    if (this.Disposed)
                        return;
                    this.Disposed = true;
                    this.SynchronizedSnapshot = null;
                    this.Running = false;
                    this.ShellGeneration++;
                    shell = this.Shell;
                    terminal = this.Terminal;
                }

                shell.Dispose();
                terminal.Dispose();
            }

            #endregion
        }

        #endregion
    }
}
