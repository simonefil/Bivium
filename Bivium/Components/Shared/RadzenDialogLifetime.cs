using Radzen;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Tracks the ownership of a Radzen dialog without closing overlapping dialogs of other adapters
    /// </summary>
    internal sealed class RadzenDialogLifetime : IDisposable
    {
        #region Class Variables

        /// <summary>
        /// Scoped service shared by the circuit
        /// </summary>
        private readonly DialogService _dialogService;

        /// <summary>
        /// Options identifying the opening owned by the adapter
        /// </summary>
        private DialogOptions _ownedOptions;

        /// <summary>
        /// Number of dialogs open above the owned one
        /// </summary>
        private int _dialogsAbove;

        /// <summary>
        /// Indicates that the owned dialog is present in the Radzen stack
        /// </summary>
        private bool _isOpen;

        /// <summary>
        /// Indicates that the subscriptions have already been released
        /// </summary>
        private bool _isDisposed;

        /// <summary>Close deferred until the owned dialog returns to the top</summary>
        private bool _closeRequested;

        /// <summary>Circuit dispatcher for closes after the Radzen OnClose returns</summary>
        private readonly SynchronizationContext _dispatcher;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates the tracker for the dialog service of the current circuit
        /// </summary>
        /// <param name="dialogService">Scoped dialog service</param>
        public RadzenDialogLifetime(DialogService dialogService)
        {
            this._dialogService = dialogService;
            this._dispatcher = SynchronizationContext.Current;
            this._dialogService.OnOpen += this.HandleOpen;
            this._dialogService.OnClose += this.HandleClose;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Registers the unique options of the next owned opening
        /// </summary>
        /// <param name="options">Options passed to <see cref="DialogService.OpenAsync(string, Microsoft.AspNetCore.Components.RenderFragment{DialogService}, DialogOptions, CancellationToken?)"/></param>
        public void Begin(DialogOptions options)
        {
            this._ownedOptions = options;
            this._dialogsAbove = 0;
            this._isOpen = false;
            this._closeRequested = false;
        }

        /// <summary>
        /// Forgets the finished opening if it still belongs to the given options
        /// </summary>
        /// <param name="options">Options of the finished opening</param>
        public void Complete(DialogOptions options)
        {
            if (!ReferenceEquals(this._ownedOptions, options))
                return;

            this._ownedOptions = null;
            this._dialogsAbove = 0;
            this._isOpen = false;
        }

        /// <summary>
        /// Returns whether the owned dialog is open and at the top of the stack
        /// </summary>
        /// <returns><see langword="true"/> only when a close does not involve other owners' dialogs</returns>
        public bool CanClose()
        {
            return this._isOpen && this._dialogsAbove == 0;
        }

        /// <summary>
        /// Releases the subscriptions to the scoped service
        /// </summary>
        public void Dispose()
        {
            if (this._isDisposed)
                return;

            this._isDisposed = true;
            this.RequestClose();
            if (!this._isOpen)
                this.Unsubscribe();
        }

        /// <summary>Requests closing without involving other owners in the stack</summary>
        public void RequestClose()
        {
            this._closeRequested = true;
            if (this.CanClose())
                this._dialogService.Close();
        }

        /// <summary>Releases the subscriptions after the owned close</summary>
        private void Unsubscribe()
        {
            this._dialogService.OnOpen -= this.HandleOpen;
            this._dialogService.OnClose -= this.HandleClose;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Updates the depth when Radzen opens a dialog
        /// </summary>
        /// <param name="title">Title of the opening</param>
        /// <param name="componentType">Content type</param>
        /// <param name="parameters">Content parameters</param>
        /// <param name="options">Options identifying the opening</param>
        private void HandleOpen(string title, Type componentType, Dictionary<string, object> parameters, DialogOptions options)
        {
            if (ReferenceEquals(options, this._ownedOptions))
            {
                this._isOpen = true;
                this._dialogsAbove = 0;
            }
            else if (this._isOpen)
            {
                this._dialogsAbove++;
            }
        }

        /// <summary>
        /// Updates the depth when Radzen closes the dialog at the top of the stack
        /// </summary>
        /// <param name="result">Result of the closed dialog</param>
        private void HandleClose(dynamic result)
        {
            if (!this._isOpen)
                return;

            if (this._dialogsAbove > 0)
            {
                this._dialogsAbove--;
                if (this._dialogsAbove == 0 && this._closeRequested)
                    this._dispatcher?.Post(_ => this.CloseWhenTopmost(), null);
            }
            else
            {
                this._isOpen = false;
                this._ownedOptions = null;
                if (this._isDisposed)
                    this.Unsubscribe();
            }
        }

        /// <summary>Radzen removes the stack and task after OnClose: do not close recursively inside the event</summary>
        private void CloseWhenTopmost()
        {
            if (this._closeRequested && this.CanClose())
                this._dialogService.Close();
        }

        #endregion
    }
}
