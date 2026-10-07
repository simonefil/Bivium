// Bivium Advanced Rename - Escape locale della finestra modeless
import { isBlockingModalOpen } from './interop.js';

const registrations = new Map();

/**
 * Escape con il focus nella finestra renamer chiude la finestra e non raggiunge la tastiera del Commander.
 * Il listener in capture su window precede quello del Commander registrato su document.
 * @param {string} windowId - Id DOM della finestra renamer.
 * @param {object} dotNetRef - DotNetObjectReference del componente.
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
        // Un dropdown aperto consuma Escape per chiudere il proprio popup
        if (active.closest('[role="combobox"][aria-expanded="true"]')) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        dotNetRef.invokeMethodAsync('OnRenamerEscape').catch(function () { });
    }, { capture: true, signal: controller.signal });
    registrations.set(windowId, controller);
}

/**
 * Rimuove il listener Escape della finestra.
 * @param {string} windowId - Id DOM della finestra renamer.
 */
export function disposeRenamerEscape(windowId) {
    registrations.get(windowId)?.abort();
    registrations.delete(windowId);
}
