using System;

namespace Bivium.Models
{
    /// <summary>Misure ricevute dall'adapter JS delle finestre desktop</summary>
    public sealed class FloatingWindowGeometryUpdate
    {
        /// <summary>Identità catturata dal mount JS, non letta nuovamente dal DOM</summary>
        public string SessionId { get; set; } = "";
        /// <summary>Sequenza monotona della registrazione per scartare callback riordinati</summary>
        public long Sequence { get; set; }
        /// <summary>Generazione del lease catturata dalla registrazione JS</summary>
        public long LeaseGeneration { get; set; }
        /// <summary>Coordinata orizzontale</summary>
        public double Left { get; set; }
        /// <summary>Coordinata verticale</summary>
        public double Top { get; set; }
        /// <summary>Larghezza misurata</summary>
        public double Width { get; set; }
        /// <summary>Altezza misurata</summary>
        public double Height { get; set; }
        /// <summary>Larghezza viewport sorgente</summary>
        public double ViewportWidth { get; set; }
        /// <summary>Altezza viewport sorgente</summary>
        public double ViewportHeight { get; set; }
        /// <summary>Ordine MRU</summary>
        public long MruOrder { get; set; }
        /// <summary>Identità semantica del controllo focalizzato</summary>
        public string FocusTarget { get; set; } = "";
        /// <summary>Scarta misure non finite o prive di un rettangolo visibile</summary>
        public bool IsValid => double.IsFinite(this.Left) && double.IsFinite(this.Top) && double.IsFinite(this.Width) && double.IsFinite(this.Height) && double.IsFinite(this.ViewportWidth) && double.IsFinite(this.ViewportHeight) && this.Width > 0 && this.Height > 0 && this.ViewportWidth > 0 && this.ViewportHeight > 0 && this.MruOrder >= 0;
    }
}
