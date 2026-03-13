using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Draggable splitter bar for resizing panels
    /// </summary>
    public partial class ResizableSplitter : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Direction: "vertical" or "horizontal"
        /// </summary>
        [Parameter]
        public string Direction { get; set; } = "vertical";

        /// <summary>
        /// CSS custom property name to update on resize
        /// </summary>
        [Parameter]
        public string CssVariable { get; set; } = "";

        /// <summary>
        /// Unique id for this splitter element
        /// </summary>
        [Parameter]
        public string SplitterId { get; set; } = "";

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether JS interop has been initialized
        /// </summary>
        private bool _initialized = false;

        /// <summary>
        /// JS module reference
        /// </summary>
        private IJSObjectReference _jsModule;

        #endregion

        #region Overrides

        /// <summary>
        /// Initialize JS interop after first render
        /// </summary>
        /// <param name="firstRender">True on first render</param>
        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender && !this._initialized)
            {
                this._initialized = true;
                // Fire and forget - we need async here for JS interop but minimize its use
                this.InitializeResizer();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Initializes the JS resize handler
        /// </summary>
        private void InitializeResizer()
        {
            // Use InvokeVoidAsync but don't await - fire and forget
            _ = this.InitializeResizerInternal();
        }

        /// <summary>
        /// Internal async initialization for JS interop
        /// </summary>
        private async System.Threading.Tasks.Task InitializeResizerInternal()
        {
            this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            await this._jsModule.InvokeVoidAsync("initResizer", this.SplitterId, this.Direction, this.CssVariable);
        }

        #endregion
    }
}
