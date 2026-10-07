using System;

namespace Bivium.Models
{
    /// <summary>Measurements received from the JS adapter of the desktop windows</summary>
    public sealed class FloatingWindowGeometryUpdate
    {
        /// <summary>Identity captured by the JS mount, not read again from the DOM</summary>
        public string SessionId { get; set; } = "";
        /// <summary>Monotonic registration sequence used to discard reordered callbacks</summary>
        public long Sequence { get; set; }
        /// <summary>Lease generation captured by the JS registration</summary>
        public long LeaseGeneration { get; set; }
        /// <summary>Horizontal coordinate</summary>
        public double Left { get; set; }
        /// <summary>Vertical coordinate</summary>
        public double Top { get; set; }
        /// <summary>Measured width</summary>
        public double Width { get; set; }
        /// <summary>Measured height</summary>
        public double Height { get; set; }
        /// <summary>Source viewport width</summary>
        public double ViewportWidth { get; set; }
        /// <summary>Source viewport height</summary>
        public double ViewportHeight { get; set; }
        /// <summary>MRU order</summary>
        public long MruOrder { get; set; }
        /// <summary>Semantic identity of the focused control</summary>
        public string FocusTarget { get; set; } = "";
        /// <summary>Discards non-finite measurements or those without a visible rectangle</summary>
        public bool IsValid => double.IsFinite(this.Left) && double.IsFinite(this.Top) && double.IsFinite(this.Width) && double.IsFinite(this.Height) && double.IsFinite(this.ViewportWidth) && double.IsFinite(this.ViewportHeight) && this.Width > 0 && this.Height > 0 && this.ViewportWidth > 0 && this.ViewportHeight > 0 && this.MruOrder >= 0;
    }
}
