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
        /// Maximum revisions recoverable without a full handoff
        /// </summary>
        private const int PATCH_REVISION_WINDOW = 256;

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
                    this.AdvanceSessionRevisionLocked(session);
                    changed = true;
                }
            });

            if (changed)
                this.NotifySubscribers(new TerminalRuntimeEvent { SessionId = sessionId, Revision = this.GetRuntimeRevision(), SessionsChanged = true });
        }

        /// <summary>
        /// Renames a terminal tab
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="label">New label</param>
        public void RenameSession(WorkspaceClientToken token, int sessionId, string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return;

            TerminalSessionRuntime session = null;
            bool changed = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    session.Label = label.Trim();
                    this.AdvanceSessionRevisionLocked(session);
                    changed = true;
                }

                this.IncrementRuntimeRevision();
            });

            if (changed)
                this.ScheduleNotification(session, true);
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
        public void SendKey(WorkspaceClientToken token, int sessionId, string keyName, string character, bool shift, bool control, bool alt)
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
                    restart = !session.Running && session.Exited && !session.RestartPending;
                    if (restart)
                        session.RestartPending = true;
                    else if (session.Running)
                    {
                        string sequence = this.GenerateKeySequence(session, keyName, character, modifiers);
                        if (!string.IsNullOrEmpty(sequence))
                            session.Shell.SendInput(sequence);
                    }
                }
            });

            if (restart)
                this.RestartSessionInternal(sessionId);
        }

        /// <summary>
        /// Sends pasted text while applying bracketed-paste mode server-side
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
                    {
                        string sequence = session.Terminal.BracketedPasteMode ? "\x1b[200~" + text + "\x1b[201~" : text;
                        session.Shell.SendInput(sequence);
                    }
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
                    if (session.Cols == safeCols && session.Rows == safeRows)
                        return;

                    session.Cols = safeCols;
                    session.Rows = safeRows;
                    session.Terminal.Resize(safeCols, safeRows);
                    if (session.Running)
                        session.Shell.Resize(safeCols, safeRows);
                    this.AdvanceSessionRevisionLocked(session);
                    changed = true;
                }

                this.IncrementRuntimeRevision();
            });

            if (changed)
                this.ScheduleNotification(session, false);
        }

        /// <summary>
        /// Explicitly restarts an exited session
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="sessionId">Session identifier</param>
        public void RestartSession(WorkspaceClientToken token, int sessionId)
        {
            bool restart = false;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                TerminalSessionRuntime session = this.GetSession(sessionId);
                if (session == null)
                    return;

                lock (session.SyncRoot)
                {
                    if (session.Disposed || session.RestartPending)
                        return;
                    session.RestartPending = true;
                    restart = true;
                }
            });

            if (restart)
                this.RestartSessionInternal(sessionId);
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
                session.HyperlinksByLine.Clear();
                session.ClearActiveHyperlink();
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

        /// <summary>
        /// Explicitly closes every session
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        public void CloseAllSessions(WorkspaceClientToken token)
        {
            List<TerminalSessionRuntime> sessions = null;
            this._workspaceService.ExecuteMutation(token, () =>
            {
                lock (this._lock)
                {
                    sessions = new List<TerminalSessionRuntime>(this._sessions.Values);
                    this._sessions.Clear();
                    this._activeSessionId = 0;
                    this._runtimeRevision++;
                }
            });

            this.DisposeSessions(sessions);

            this.NotifySubscribers(new TerminalRuntimeEvent { SessionId = 0, Revision = this.GetRuntimeRevision(), SessionsChanged = true });
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
                result.ToRevision = session.Revision;
                result.RequiresResync = fromRevision > session.Revision || fromRevision < session.MinimumPatchRevision;
                if (!result.RequiresResync && fromRevision < session.Revision)
                    result.Session = this.CreateSessionSnapshot(session);
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
                return session.History.GetPage(start, count, session.Revision, cancellationToken);
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
            }

            try
            {
                session.Shell.Start(session.WorkingDirectory, session.Cols, session.Rows, data => this.HandleShellOutput(session.Id, generation, data), () => this.HandleShellExit(session.Id, generation));
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
            TerminalSessionRuntime session = this.GetSession(sessionId);
            if (session == null || string.IsNullOrEmpty(data))
                return;

            int activeSessionId;
            lock (this._lock)
            {
                activeSessionId = this._activeSessionId;
            }

            // XTerm.NET must always receive output even when no circuit is connected
            lock (session.SyncRoot)
            {
                if (generation != session.ShellGeneration || session.Disposed)
                    return;

                session.Terminal.Write(data);
                session.LastOutputUtc = DateTime.UtcNow;
                this.AdvanceSessionRevisionLocked(session);
                session.HasUnreadOutput = sessionId != activeSessionId;
            }

            // Process budgets and notifications after the session commit to avoid holding its lock
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

                session.Running = false;
                session.Exited = true;
                session.ExitedAtUtc = DateTime.UtcNow;
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
        private TerminalSessionSnapshot CreateSessionSnapshot(TerminalSessionRuntime session)
        {
            TerminalSessionSnapshot result = new TerminalSessionSnapshot();
            result.Id = session.Id;
            result.Label = session.Label;
            result.WorkingDirectory = session.WorkingDirectory;
            result.Running = session.Running;
            result.Exited = session.Exited;
            result.HasUnreadOutput = session.HasUnreadOutput;
            result.Cols = session.Cols;
            result.Rows = session.Rows;
            result.Revision = session.Revision;
            result.HistoryStart = session.History.StartIndex;
            result.HistoryEnd = session.History.EndIndex;
            result.HistoryTruncated = session.History.Truncated;
            result.HistoryBytes = session.History.RetainedBytes;
            result.HistoryPageRows = session.HistoryPageRows;
            result.Screen = this.CreateScreenSnapshot(session);
            return result;
        }

        /// <summary>
        /// Creates a snapshot of the current screen only
        /// </summary>
        /// <param name="session">Source session</param>
        /// <returns>Bounded screen snapshot</returns>
        private TerminalScreenSnapshot CreateScreenSnapshot(TerminalSessionRuntime session)
        {
            TerminalScreenSnapshot result = new TerminalScreenSnapshot();
            result.AlternateBuffer = session.Terminal.IsAlternateBufferActive;
            result.CursorX = session.Terminal.Buffer.X;
            result.CursorY = session.Terminal.Buffer.Y;
            result.CursorVisible = session.Terminal.CursorVisible;
            result.MouseTracking = session.Terminal.MouseTrackingMode != MouseTrackingMode.None;

            List<TerminalLineSnapshot> lines = new List<TerminalLineSnapshot>();
            int firstLine = session.Terminal.Buffer.BaseY;
            for (int i = 0; i < session.Rows; i++)
            {
                BufferLine line = firstLine + i < session.Terminal.Buffer.Lines.Length ? session.Terminal.Buffer.Lines[firstLine + i] : null;
                lines.Add(this.SerializeLine(session, line, -1));
            }

            result.Lines = lines.AsReadOnly();
            return result;
        }

        /// <summary>
        /// Creates an export screen without rows already archived before entering the alternate buffer
        /// </summary>
        /// <param name="session">Source session</param>
        /// <returns>Compact current rows not already present in the archive</returns>
        private TerminalScreenSnapshot CreateHistoryExportScreenSnapshot(TerminalSessionRuntime session)
        {
            TerminalScreenSnapshot result = this.CreateScreenSnapshot(session);
            if (result.AlternateBuffer || session.DeactivatedNormalLines.Count == 0)
                return result;

            List<TerminalLineSnapshot> lines = new List<TerminalLineSnapshot>();
            int firstLine = session.Terminal.Buffer.BaseY;
            bool skippedPreviousRow = false;
            for (int i = 0; i < result.Lines.Count; i++)
            {
                BufferLine line = firstLine + i < session.Terminal.Buffer.Lines.Length ? session.Terminal.Buffer.Lines[firstLine + i] : null;
                TerminalLineSnapshot snapshot = result.Lines[i];
                if (line != null && session.DeactivatedNormalLines.TryGetValue(line, out TerminalLineSnapshot archivedLine) && AreLineSnapshotsEquivalent(snapshot, archivedLine))
                {
                    skippedPreviousRow = true;
                    continue;
                }

                if (skippedPreviousRow)
                    snapshot.Wrapped = false;
                lines.Add(snapshot);
                skippedPreviousRow = false;
            }

            result.Lines = lines.AsReadOnly();
            result.CursorY = lines.Count - 1;
            return result;
        }

        /// <summary>
        /// Serializes an XTerm.NET row while preserving cells, attributes and wrapping
        /// </summary>
        /// <param name="session">Session owning the row</param>
        /// <param name="line">Headless row</param>
        /// <param name="index">Logical offset</param>
        /// <returns>Serialized row</returns>
        private TerminalLineSnapshot SerializeLine(TerminalSessionRuntime session, BufferLine line, long index)
        {
            TerminalLineSnapshot result = new TerminalLineSnapshot();
            result.Index = index;
            if (line == null)
                return result;

            // Serialize only significant cells while retaining attributes required by the remote renderer
            int length = line.GetTrimmedLength();
            List<TerminalCellSnapshot> cells = new List<TerminalCellSnapshot>();
            StringBuilder text = new StringBuilder();
            int serializedBytes = 32;
            for (int i = 0; i < length; i++)
            {
                BufferCell cell = line[i];
                TerminalCellSnapshot cellSnapshot = SerializeCell(cell);
                cellSnapshot.Hyperlink = this.GetCellHyperlink(session, line, i);
                cells.Add(cellSnapshot);
                if (cell.Width > 0)
                    text.Append(cellSnapshot.Content);
                serializedBytes += 24 + Encoding.UTF8.GetByteCount(cellSnapshot.Content) + Encoding.UTF8.GetByteCount(cellSnapshot.Hyperlink);
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
            result.SerializedBytes = serializedBytes + Encoding.UTF8.GetByteCount(result.Text);
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
            TerminalLineSnapshot snapshot = this.SerializeLine(session, line, session.History.EndIndex);
            if (args.Buffer == BufferType.Normal && args.Reason == LineExitReason.BufferDeactivated)
            {
                if (session.PendingDeactivatedNormalLines == null)
                    session.PendingDeactivatedNormalLines = new Dictionary<BufferLine, TerminalLineSnapshot>();

                bool unchanged = session.DeactivatedNormalLines.TryGetValue(line, out TerminalLineSnapshot archivedLine) && AreLineSnapshotsEquivalent(snapshot, archivedLine);
                if (!unchanged)
                    session.History.Append(snapshot);
                session.PendingDeactivatedNormalLines[line] = snapshot;
                return;
            }

            if (args.Buffer == BufferType.Normal && args.Reason == LineExitReason.Scrolled)
            {
                bool unchanged = session.DeactivatedNormalLines.TryGetValue(line, out TerminalLineSnapshot archivedLine) && AreLineSnapshotsEquivalent(snapshot, archivedLine);
                session.DeactivatedNormalLines.Remove(line);
                if (!unchanged)
                    session.History.Append(snapshot);
            }
            else
            {
                session.History.Append(snapshot);
            }

            if (args.Reason == LineExitReason.Scrolled)
                session.HyperlinksByLine.Remove(line);
        }

        /// <summary>
        /// Finalizes the normal-buffer checkpoint after XTerm.NET activates another buffer
        /// </summary>
        /// <param name="session">Affected session</param>
        /// <param name="args">Activated buffer</param>
        private void HandleBufferChanged(TerminalSessionRuntime session, TerminalEvents.BufferChangedEventArgs args)
        {
            if (args.Buffer != BufferType.Alternate)
                return;

            session.DeactivatedNormalLines = session.PendingDeactivatedNormalLines ?? new Dictionary<BufferLine, TerminalLineSnapshot>();
            session.PendingDeactivatedNormalLines = null;
        }

        /// <summary>
        /// Determines whether two serialized rows contain the same terminal state
        /// </summary>
        /// <param name="left">First row</param>
        /// <param name="right">Second row</param>
        /// <returns>True when text, wrapping and cells are equal</returns>
        private static bool AreLineSnapshotsEquivalent(TerminalLineSnapshot left, TerminalLineSnapshot right)
        {
            if (left == null || right == null || left.Text != right.Text || left.Wrapped != right.Wrapped || left.LineAttribute != right.LineAttribute || left.Cells.Count != right.Cells.Count)
                return false;

            for (int i = 0; i < left.Cells.Count; i++)
            {
                TerminalCellSnapshot leftCell = left.Cells[i];
                TerminalCellSnapshot rightCell = right.Cells[i];
                if (leftCell.Content != rightCell.Content || leftCell.Width != rightCell.Width || leftCell.Foreground != rightCell.Foreground || leftCell.ForegroundMode != rightCell.ForegroundMode || leftCell.Background != rightCell.Background || leftCell.BackgroundMode != rightCell.BackgroundMode || leftCell.Attributes != rightCell.Attributes || leftCell.Hyperlink != rightCell.Hyperlink)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Associates OSC 8 spans with public buffer rows
        /// </summary>
        /// <param name="session">Affected session</param>
        /// <param name="args">Hyperlink change emitted by XTerm.NET</param>
        private void HandleHyperlinkChanged(TerminalSessionRuntime session, TerminalEvents.HyperlinkEventArgs args)
        {
            BufferLine currentLine = this.GetCurrentBufferLine(session);

            // The opening event retains the anchor; closing materializes ranges on traversed rows
            if (!args.IsCleared)
            {
                session.ActiveHyperlinkUrl = args.Url ?? "";
                session.ActiveHyperlinkStartLine = currentLine;
                session.ActiveHyperlinkStartX = session.Terminal.Buffer.X;
                return;
            }

            if (string.IsNullOrEmpty(session.ActiveHyperlinkUrl) || session.ActiveHyperlinkStartLine == null || currentLine == null)
            {
                session.ClearActiveHyperlink();
                return;
            }

            int startLineIndex = this.FindBufferLineIndex(session, session.ActiveHyperlinkStartLine);
            int endLineIndex = this.FindBufferLineIndex(session, currentLine);
            if (startLineIndex >= 0 && endLineIndex >= startLineIndex)
            {
                for (int lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
                {
                    BufferLine line = session.Terminal.Buffer.Lines[lineIndex];
                    int startX = lineIndex == startLineIndex ? session.ActiveHyperlinkStartX : 0;
                    int endX = lineIndex == endLineIndex ? session.Terminal.Buffer.X : line.Length;
                    if (endX <= startX)
                        continue;

                    List<HyperlinkRange> ranges;
                    if (!session.HyperlinksByLine.TryGetValue(line, out ranges))
                    {
                        ranges = new List<HyperlinkRange>();
                        session.HyperlinksByLine.Add(line, ranges);
                    }
                    ranges.Add(new HyperlinkRange(startX, endX, session.ActiveHyperlinkUrl));
                }
            }

            session.ClearActiveHyperlink();
        }

        /// <summary>
        /// Returns the closed or still-active link associated with a cell
        /// </summary>
        /// <param name="session">Session owning the buffer</param>
        /// <param name="line">Cell row</param>
        /// <param name="cellIndex">Cell index</param>
        /// <returns>Associated URI or an empty string</returns>
        private string GetCellHyperlink(TerminalSessionRuntime session, BufferLine line, int cellIndex)
        {
            List<HyperlinkRange> ranges;
            if (session.HyperlinksByLine.TryGetValue(line, out ranges))
            {
                for (int i = ranges.Count - 1; i >= 0; i--)
                {
                    if (cellIndex >= ranges[i].StartX && cellIndex < ranges[i].EndX)
                        return ranges[i].Url;
                }
            }

            if (string.IsNullOrEmpty(session.ActiveHyperlinkUrl) || session.ActiveHyperlinkStartLine == null)
                return "";
            int startLineIndex = this.FindBufferLineIndex(session, session.ActiveHyperlinkStartLine);
            int currentLineIndex = this.FindBufferLineIndex(session, this.GetCurrentBufferLine(session));
            int lineIndex = this.FindBufferLineIndex(session, line);
            if (startLineIndex < 0 || currentLineIndex < startLineIndex || lineIndex < startLineIndex || lineIndex > currentLineIndex)
                return "";
            int startX = lineIndex == startLineIndex ? session.ActiveHyperlinkStartX : 0;
            int endX = lineIndex == currentLineIndex ? session.Terminal.Buffer.X : line.Length;
            return cellIndex >= startX && cellIndex < endX ? session.ActiveHyperlinkUrl : "";
        }

        /// <summary>
        /// Returns the row containing the cursor in the current buffer
        /// </summary>
        /// <param name="session">Session owning the buffer</param>
        /// <returns>Current row or null</returns>
        private BufferLine GetCurrentBufferLine(TerminalSessionRuntime session)
        {
            int index = session.Terminal.Buffer.BaseY + session.Terminal.Buffer.Y;
            return index >= 0 && index < session.Terminal.Buffer.Lines.Length ? session.Terminal.Buffer.Lines[index] : null;
        }

        /// <summary>
        /// Finds a row by identity through the public buffer API
        /// </summary>
        /// <param name="session">Session owning the buffer</param>
        /// <param name="line">Row to find</param>
        /// <returns>Row index or -1</returns>
        private int FindBufferLineIndex(TerminalSessionRuntime session, BufferLine line)
        {
            if (line == null)
                return -1;
            for (int i = 0; i < session.Terminal.Buffer.Lines.Length; i++)
            {
                if (ReferenceEquals(session.Terminal.Buffer.Lines[i], line))
                    return i;
            }
            return -1;
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
        /// <param name="session">Target session</param>
        /// <param name="keyName">DOM key name</param>
        /// <param name="character">Printable character</param>
        /// <param name="modifiers">XTerm.NET modifiers</param>
        /// <returns>Sequence to send to the PTY</returns>
        private string GenerateKeySequence(TerminalSessionRuntime session, string keyName, string character, KeyModifiers modifiers)
        {
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
                return session.Terminal.GenerateKeyInput(key.Value, modifiers);
            if (!string.IsNullOrEmpty(character) && character.Length == 1)
                return session.Terminal.GenerateCharInput(character[0], modifiers);
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
                    ConvertEol = true
                };
                this.Terminal = new Terminal(terminalOptions);
                this.Shell = new ShellService();
                this.Terminal.LineExitedViewport += (sender, args) => this._owner.CaptureExitedLine(this, args);
                this.Terminal.BufferChanged += (sender, args) => this._owner.HandleBufferChanged(this, args);
                this.Terminal.DataReceived += (sender, args) => this.Shell.SendInput(args.Data);
                this.Terminal.HyperlinkChanged += (sender, args) => this._owner.HandleHyperlinkChanged(this, args);
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

            /// <summary>
            /// Whether the tab has unread output
            /// </summary>
            public bool HasUnreadOutput { get; set; }

            /// <summary>
            /// Whether a notification is already queued
            /// </summary>
            public bool NotificationPending { get; set; }

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
            /// Latest normal-buffer rows archived before entering the alternate buffer
            /// </summary>
            public Dictionary<BufferLine, TerminalLineSnapshot> DeactivatedNormalLines { get; set; } = new Dictionary<BufferLine, TerminalLineSnapshot>();

            /// <summary>
            /// Normal-buffer checkpoint being collected during a buffer transition
            /// </summary>
            public Dictionary<BufferLine, TerminalLineSnapshot> PendingDeactivatedNormalLines { get; set; }

            /// <summary>
            /// OSC 8 ranges indexed by buffer row
            /// </summary>
            public Dictionary<BufferLine, List<HyperlinkRange>> HyperlinksByLine { get; } = new Dictionary<BufferLine, List<HyperlinkRange>>();

            /// <summary>
            /// URI of the still-open OSC 8 hyperlink
            /// </summary>
            public string ActiveHyperlinkUrl { get; set; } = "";

            /// <summary>
            /// Starting row of the still-open OSC 8 hyperlink
            /// </summary>
            public BufferLine ActiveHyperlinkStartLine { get; set; }

            /// <summary>
            /// Starting column of the still-open OSC 8 hyperlink
            /// </summary>
            public int ActiveHyperlinkStartX { get; set; }

            #endregion

            #region Public Methods

            /// <summary>
            /// Clears the still-open OSC 8 hyperlink state
            /// </summary>
            public void ClearActiveHyperlink()
            {
                this.ActiveHyperlinkUrl = "";
                this.ActiveHyperlinkStartLine = null;
                this.ActiveHyperlinkStartX = 0;
            }

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

        /// <summary>
        /// Cell range belonging to an OSC 8 hyperlink
        /// </summary>
        /// <param name="StartX">Inclusive starting column</param>
        /// <param name="EndX">Exclusive ending column</param>
        /// <param name="Url">URI associated with the range</param>
        private sealed record HyperlinkRange(int StartX, int EndX, string Url);

        #endregion
    }
}
