// Bivium Advanced Rename - Local Escape handling of the modeless window
import { isBlockingModalOpen } from './interop.js';

const registrations = new Map();

/**
 * Escape with focus inside the renamer window closes the window and does not reach the Commander keyboard.
 * The capture listener on window runs before the Commander listener registered on document.
 * @param {string} windowId - DOM id of the renamer window.
 * @param {object} dotNetRef - DotNetObjectReference of the component.
 */
export function registerRenamerEscape(windowId, dotNetRef) {
    disposeRenamerEscape(windowId);
    const controller = new AbortController();
    window.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape' || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey || event.isComposing) return;
        const win = document.getElementById(windowId);
        const active = document.activeElement;
        if (!win?.classList.contains('visible') || !active || !win.contains(active)) return;
        if (isBlockingModalOpen() || globalThis[Symbol.for('bivium.desktopPublications')]?.freeze) return;
        // An open dropdown consumes Escape to close its own popup
        if (active.closest('[role="combobox"][aria-expanded="true"]')) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        dotNetRef.invokeMethodAsync('OnRenamerEscape').catch(function () { });
    }, { capture: true, signal: controller.signal });
    registrations.set(windowId, controller);
}

/**
 * Removes the Escape listener of the window.
 * @param {string} windowId - DOM id of the renamer window.
 */
export function disposeRenamerEscape(windowId) {
    registrations.get(windowId)?.abort();
    registrations.delete(windowId);
}
