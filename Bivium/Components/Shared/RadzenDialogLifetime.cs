using Radzen;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Traccia l'ownership di un dialog Radzen senza chiudere dialog sovrapposti di altri adapter
    /// </summary>
    internal sealed class RadzenDialogLifetime : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Servizio scoped condiviso dal circuito
        /// </summary>
        private readonly DialogService _dialogService;

        /// <summary>
        /// Opzioni che identificano l'apertura posseduta dall'adapter
        /// </summary>
        private DialogOptions _ownedOptions;

        /// <summary>
        /// Numero di dialog aperti sopra quello posseduto
        /// </summary>
        private int _dialogsAbove;

        /// <summary>
        /// Indica che il dialog posseduto è presente nello stack Radzen
        /// </summary>
        private bool _isOpen;

        /// <summary>
        /// Indica che le sottoscrizioni sono già state rilasciate
        /// </summary>
        private bool _isDisposed;

        /// <summary>Chiusura differita finché il dialog posseduto torna in cima</summary>
        private bool _closeRequested;

        /// <summary>Dispatcher del circuito per chiusure dopo il ritorno di OnClose Radzen</summary>
        private readonly SynchronizationContext _dispatcher;

        #endregion

        #region Costruttore

        /// <summary>
        /// Crea il tracker per il servizio dialog del circuito corrente
        /// </summary>
        /// <param name="dialogService">Servizio dialog scoped</param>
        public RadzenDialogLifetime(DialogService dialogService)
        {
            this._dialogService = dialogService;
            this._dispatcher = SynchronizationContext.Current;
            this._dialogService.OnOpen += this.HandleOpen;
            this._dialogService.OnClose += this.HandleClose;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>Indica che l'owner non può aprire altre finestre</summary>
        public bool IsDisposed => this._isDisposed;

        /// <summary>
        /// Registra le opzioni univoche della prossima apertura posseduta
        /// </summary>
        /// <param name="options">Opzioni passate a <see cref="DialogService.OpenAsync(string, Microsoft.AspNetCore.Components.RenderFragment{DialogService}, DialogOptions, CancellationToken?)"/></param>
        public void Begin(DialogOptions options)
        {
            this._ownedOptions = options;
            this._dialogsAbove = 0;
            this._isOpen = false;
            this._closeRequested = false;
        }

        /// <summary>
        /// Dimentica l'apertura terminata se appartiene ancora alle opzioni indicate
        /// </summary>
        /// <param name="options">Opzioni dell'apertura terminata</param>
        public void Complete(DialogOptions options)
        {
            if (!ReferenceEquals(this._ownedOptions, options))
                return;

            this._ownedOptions = null;
            this._dialogsAbove = 0;
            this._isOpen = false;
        }

        /// <summary>
        /// Restituisce se il dialog posseduto è aperto e in cima allo stack
        /// </summary>
        /// <returns><see langword="true"/> solo quando una chiusura non coinvolge dialog altrui</returns>
        public bool CanClose()
        {
            return this._isOpen && this._dialogsAbove == 0;
        }

        /// <summary>
        /// Restituisce se l'apertura posseduta è ancora presente nello stack
        /// </summary>
        /// <returns><see langword="true"/> mentre il dialog posseduto è aperto</returns>
        public bool IsOpen()
        {
            return this._isOpen;
        }

        /// <summary>
        /// Rilascia le sottoscrizioni al servizio scoped
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

        /// <summary>Richiede la chiusura senza coinvolgere altri owner nello stack</summary>
        public void RequestClose()
        {
            this._closeRequested = true;
            if (this.CanClose())
                this._dialogService.Close();
        }

        /// <summary>Rilascia le sottoscrizioni dopo la chiusura posseduta</summary>
        private void Unsubscribe()
        {
            this._dialogService.OnOpen -= this.HandleOpen;
            this._dialogService.OnClose -= this.HandleClose;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Aggiorna la profondità quando Radzen apre un dialog
        /// </summary>
        /// <param name="title">Titolo dell'apertura</param>
        /// <param name="componentType">Tipo del contenuto</param>
        /// <param name="parameters">Parametri del contenuto</param>
        /// <param name="options">Opzioni che identificano l'apertura</param>
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
        /// Aggiorna la profondità quando Radzen chiude il dialog in cima allo stack
        /// </summary>
        /// <param name="result">Risultato del dialog chiuso</param>
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

        /// <summary>Radzen rimuove stack e task dopo OnClose: non chiudere ricorsivamente dentro l'evento</summary>
        private void CloseWhenTopmost()
        {
            if (this._closeRequested && this.CanClose())
                this._dialogService.Close();
        }

        #endregion
    }
}
