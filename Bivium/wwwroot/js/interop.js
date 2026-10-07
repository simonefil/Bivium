// Bivium JS Interop
// Handles: workspace handoff, floating window stacking, keyboard capture, file panels, path editor

import { invokeCircuitMethod as invokeConnectedCircuitMethod, isCircuitConnected, registerCircuitParticipant } from './connection.js';

// Condiviso anche fra import con query string diverse: nessun publisher del desktop sfugge al drain.
const publicationKey = Symbol.for('bivium.desktopPublications');
const desktopPublications = globalThis[publicationKey] ??= { pending: new Set(), panelTrackers: new Set(), windows: new Set(), freeze: null, failures: 0, composing: false };
desktopPublications.pathTrackers ??= new Set();
desktopPublications.surfaceTrackers ??= new Set();
// Guardia per i test node, dove il modulo viene importato senza DOM
if (typeof window !== 'undefined' && !desktopPublications.compositionRegistered) {
    desktopPublications.compositionRegistered = true;
    window.addEventListener('compositionstart', function () { desktopPublications.composing = true; }, true);
    window.addEventListener('compositionend', function () { desktopPublications.composing = false; }, true);
}

/**
 * Esegue la hydration visuale al frame successivo. Le schede in background sospendono requestAnimationFrame:
 * il timeout garantisce che gli adapter escano da restoring e non blocchino il takeover.
 * @param {Function} callback - Lavoro da eseguire una sola volta.
 * @returns {Function} Annulla la richiesta se non è ancora partita.
 */
export function requestVisualFrame(callback) {
    let done = false;
    let frame = 0;
    let timer = 0;
    const cancel = function () {
        done = true;
        cancelAnimationFrame(frame);
        window.clearTimeout(timer);
    };
    const run = function () {
        if (done) return;
        cancel();
        callback();
    };
    frame = requestAnimationFrame(run);
    timer = window.setTimeout(run, 100);
    return cancel;
}

/** Proxy di trasporto per includere i callback terminali esistenti senza duplicarne la security. */
export function createDesktopPublicationReference(reference) {
    return { invokeMethodAsync: (...args) => {
        const pending = reference.invokeMethodAsync(...args);
        desktopPublications.pending.add(pending);
        pending.then(function () { desktopPublications.pending.delete(pending); }, function () {
            desktopPublications.pending.delete(pending);
            desktopPublications.failures++;
        });
        return pending;
    } };
}

function invokeCircuitMethod(...args) {
    const pending = invokeConnectedCircuitMethod(...args);
    desktopPublications.pending.add(pending);
    pending.then(function () { desktopPublications.pending.delete(pending); }, function () {
        desktopPublications.pending.delete(pending);
        desktopPublications.failures++;
    });
    return pending;
}

/** Blocca ingressi browser, non heartbeat/disconnessione; il server blocca separatamente i nuovi comandi. */
// Only called from an explicitly confirmed workspace clipboard request, never during hydration.
export async function writeWorkspaceClipboardText(text) {
    const pending = navigator.clipboard.writeText(text);
    desktopPublications.pending.add(pending);
    pending.then(function () { desktopPublications.pending.delete(pending); }, function () {
        desktopPublications.pending.delete(pending);
        desktopPublications.failures++;
    });
    await pending;
}

export function beginWorkspaceHandoff(id, remainingMilliseconds = 10000, workflowId = '') {
    const modal = getTopBlockingModal();
    const workflowModal = modal?.querySelector('[data-workspace-workflow-id]');
    if (!isCircuitConnected() || (modal && (!workflowId || workflowModal?.dataset.workspaceWorkflowId !== workflowId)) || desktopPublications.freeze || desktopPublications.composing) return false;
    const root = document.getElementById('workspace-desktop');
    if (!root) return false;
    // Le superfici dichiarate dall'app devono avere completato il mount dell'adapter
    const surfaceKey = Symbol.for('bivium.surfaceAdapter');
    for (const surface of document.querySelectorAll('[data-workspace-menu-surface], [data-workspace-context-surface], [data-workspace-format-surface], [data-workspace-dropdown-surface], [data-workspace-renamer-surface], [data-workspace-terminal-view], [data-workspace-terminal-strip], [data-workspace-surface]:not([data-workspace-surface=""])')) {
        if (!surface[surfaceKey]?.ready()) return false;
    }
    const active = document.activeElement;
    if (active?.classList.contains('terminal-ime-input') && active.value) return false;
    for (const registration of desktopPublications.windows) registration.captureFocus();
    for (const registration of desktopPublications.pathTrackers) registration.capture();
    // Capture precede mouseup/inert e qualsiasi chiusura di popup causata dal freeze
    for (const registration of desktopPublications.surfaceTrackers) {
        if (!registration.ready()) return false;
        registration.capture();
    }
    // I TextBox renamer sono Immediate: non reinviare onchange, che rigenererebbe preview Rand già materializzate.
    // Solo i Numeric mantengono un valore fino al change; il loro normale commit precede il freeze.
    if (active?.matches('input') && active.closest('.renamer-window .rz-numeric')) {
        active.dispatchEvent(new Event('change', { bubbles: true }));
    }
    const controller = new AbortController();
    const freeze = { id, root, controller, active, modal, workflowId, failures: desktopPublications.failures };
    desktopPublications.freeze = freeze;
    // Conclude drag/resize, ma i tracker visuali non pubblicano la chiusura sintetica
    document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    const block = function (event) {
        if (!desktopPublications.freeze) return;
        if (event.cancelable) event.preventDefault();
        event.stopImmediatePropagation();
    };
    for (const name of ['keydown', 'keyup', 'pointerdown', 'pointerup', 'pointermove', 'mousedown', 'mouseup', 'mousemove', 'click', 'dblclick', 'contextmenu', 'wheel', 'touchstart', 'touchmove', 'touchend', 'beforeinput', 'input', 'change', 'paste', 'cut', 'drop', 'dragstart', 'compositionstart', 'compositionupdate', 'compositionend', 'resize']) {
        window.addEventListener(name, block, { capture: true, passive: false, signal: controller.signal });
    }
    root.inert = true;
    if (modal) modal.inert = true;
    // Fallback browser-only: sblocca l'input, mai il lease, se il circuito non può più eseguire il cleanup
    freeze.timer = window.setTimeout(function () { endWorkspaceHandoff(id, isCircuitConnected()); }, Math.max(1, Math.min(10000, remainingMilliseconds)));
    return true;
}

/**
 * Attende che un ripristino visuale in corso termini, entro la vita del freeze che lo richiede.
 * @param {Function} isRestoring - Stato corrente del ripristino.
 * @param {object} freeze - Freeze proprietario del tentativo.
 * @returns {Promise<boolean>} False se il freeze è terminato o il circuito è caduto.
 */
async function waitForVisualRestore(isRestoring, freeze) {
    while (isRestoring()) {
        if (desktopPublications.freeze !== freeze || !isCircuitConnected()) return false;
        await new Promise(function (resolve) { window.setTimeout(resolve, 50); });
    }
    return desktopPublications.freeze === freeze;
}

/** Flush esplicito dei timer scroll e di tutte le chiamate già in volo, senza un'attesa sotto lock server. */
export async function flushDesktopPublications(id) {
    const freeze = desktopPublications.freeze;
    if (!freeze || freeze.id !== id || !isCircuitConnected()) return false;
    try {
        for (const registration of desktopPublications.surfaceTrackers) if (!registration.ready()) return false;
        // Un ripristino appena avviato (reload recente) si attende: rifiutarlo farebbe fallire il takeover
        for (const registration of desktopPublications.pathTrackers) {
            if (!await waitForVisualRestore(function () { return registration.restoring; }, freeze)) return false;
            registration.capture();
        }
        for (const tracker of desktopPublications.panelTrackers) {
            if (!tracker.scroller.isConnected) continue;
            if (!await waitForVisualRestore(function () { return tracker.restoring; }, freeze)) return false;
            window.clearTimeout(tracker.timer);
            tracker.timer = 0;
            const top = tracker.scroller.getBoundingClientRect().top;
            const row = Array.from(tracker.scroller.querySelectorAll('tr[data-entry-path]')).find(item => item.getBoundingClientRect().bottom > top + 1);
            if (row) await invokeCircuitMethod(tracker.dotNetReference, 'OnRadzenFileListScrollAnchorChanged', row.dataset.entryPath || '');
            tracker.measure();
        }
        while (desktopPublications.pending.size) {
            await Promise.all(Array.from(desktopPublications.pending));
            if (desktopPublications.freeze !== freeze || !isCircuitConnected()) return false;
        }
        // Dopo i commit Numeric ammessi prima del freeze: aggiorna solo il visuale Renamer,
        // conservando focus/popup pre-freeze e senza reinviare valori o eventi di form.
        for (const registration of desktopPublications.surfaceTrackers) registration.captureFinal?.();
        while (desktopPublications.pending.size) {
            await Promise.all(Array.from(desktopPublications.pending));
            if (desktopPublications.freeze !== freeze || !isCircuitConnected()) return false;
        }
        const modal = getTopBlockingModal();
        const compatibleModal = !modal || (freeze.workflowId && modal.querySelector('[data-workspace-workflow-id]')?.dataset.workspaceWorkflowId === freeze.workflowId);
        return desktopPublications.freeze === freeze && freeze.failures === desktopPublications.failures && isCircuitConnected() && compatibleModal && Array.from(desktopPublications.surfaceTrackers).every(registration => registration.ready());
    } catch {
        return false;
    }
}

/** Libera esclusivamente il freeze proprietario del tentativo; non sblocca una richiesta successiva. */
export function endWorkspaceHandoff(id, restoreFocus = false) {
    const freeze = desktopPublications.freeze;
    if (!freeze || freeze.id !== id) return;
    desktopPublications.freeze = null;
    window.clearTimeout(freeze.timer);
    freeze.controller.abort();
    freeze.root.inert = false;
    if (freeze.modal) freeze.modal.inert = false;
    if (restoreFocus && freeze.active?.isConnected) freeze.active.focus({ preventScroll: true });
}

/** Base dello stack modeless: sotto header/footer, dialog e popup Radzen (vedi --bivium-floating-window-zindex in app.css). */
function getFloatingWindowBaseZIndex() {
    const value = Number.parseInt(getComputedStyle(document.documentElement).getPropertyValue('--bivium-floating-window-zindex'), 10);
    return Number.isFinite(value) ? value : 100;
}
const FLOATING_WINDOW_FOCUS_SELECTOR = [
    '.terminal-ime-input',
    '.terminal-virtual-viewport',
    '.monaco-editor textarea',
    '.renamer-body input:not([disabled])',
    '.renamer-body select:not([disabled])',
    '.renamer-body button:not([disabled])'
].join(', ');
const FLOATING_WINDOW_MANAGER_KEY = Symbol.for('bivium.floatingWindowManager');

let floatingWindowManager = globalThis[FLOATING_WINDOW_MANAGER_KEY];
if (!floatingWindowManager) {
    floatingWindowManager = {
        windows: [],
        initializedDragElements: new WeakSet(),
        lastFocusedElements: new WeakMap(),
        nextMruOrder: 1
    };
    globalThis[FLOATING_WINDOW_MANAGER_KEY] = floatingWindowManager;
}

const initializedWindowDragElements = floatingWindowManager.initializedDragElements;
floatingWindowManager.activationSequence ??= 0;
floatingWindowManager.pendingActivation ??= null;
floatingWindowManager.interactionSequence ??= 0;
const windowDragRegistrations = new Map();
let workspacePresenceRegistration = null;
let keyboardCaptureRegistration = null;
let longPressRegistration = null;

function isVisibleElement(element) {
    if (!element?.isConnected) return false;
    const style = getComputedStyle(element);
    return style.display !== 'none' && style.visibility !== 'hidden' && element.getClientRects().length > 0;
}

function getTopBlockingModal() {
    const reconnect = Array.from(document.querySelectorAll('dialog[open]')).filter(isVisibleElement);
    if (reconnect.length) return reconnect[reconnect.length - 1];
    const dialogs = Array.from(document.querySelectorAll('.rz-dialog-wrapper')).filter(isVisibleElement);
    return dialogs[dialogs.length - 1] || null;
}

/**
 * Starts browser-originated workspace presence heartbeats.
 * @param {object} dotNetReference - Current Commander callback owner.
 * @param {string} attachmentId - Current browser attachment.
 * @param {number} leaseGeneration - Current authoritative lease generation.
 */
export function startWorkspacePresence(dotNetReference, attachmentId, leaseGeneration) {
    stopWorkspacePresence();
    if (!dotNetReference) return;

    const eventController = new AbortController();
    const eventSignal = eventController.signal;
    let disconnected = false;
    let pageActive = true;
    let heartbeatPending = false;
    let currentAttachmentId = attachmentId || '';
    let currentLeaseGeneration = Number(leaseGeneration) || 0;

    function updateLease(nextAttachmentId, nextLeaseGeneration) {
        currentAttachmentId = nextAttachmentId || '';
        currentLeaseGeneration = Number(nextLeaseGeneration) || 0;
    }

    function heartbeat() {
        if (!pageActive || !isCircuitConnected() || heartbeatPending) return;
        disconnected = false;
        heartbeatPending = true;
        invokeCircuitMethod(dotNetReference, 'OnWorkspaceHeartbeat').catch(function () { }).finally(function () {
            heartbeatPending = false;
        });
    }

    function disconnect() {
        if (disconnected) return;
        pageActive = false;
        disconnected = true;
        const attachmentSnapshot = currentAttachmentId;
        const generationSnapshot = currentLeaseGeneration;
        if (attachmentSnapshot && generationSnapshot > 0) {
            const disconnectUrl = new URL('api/workspace/disconnect', document.baseURI);
            fetch(disconnectUrl, {
                method: 'POST',
                headers: {
                    'X-Bivium-Attachment': attachmentSnapshot,
                    'X-Bivium-Lease-Generation': String(generationSnapshot)
                },
                credentials: 'same-origin',
                keepalive: true
            }).catch(function () { });
        }
        invokeCircuitMethod(dotNetReference, 'OnWorkspaceDisconnected').catch(function () { });
    }

    function reconnect() {
        pageActive = true;
        heartbeat();
    }

    const unregisterConnection = registerCircuitParticipant({
        phase: 'presence',
        suspend: function () { heartbeatPending = false; },
        recover: async function (isCurrent) {
            if (!pageActive || !isCurrent()) return;
            await dotNetReference.invokeMethodAsync('OnWorkspaceHeartbeat');
            if (!pageActive || !isCurrent()) return;
            disconnected = false;
        }
    });
    const interval = window.setInterval(heartbeat, 20000);
    window.addEventListener('pagehide', disconnect, { signal: eventSignal });
    window.addEventListener('pageshow', reconnect, { signal: eventSignal });
    window.addEventListener('online', heartbeat, { signal: eventSignal });
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') heartbeat();
    }, { signal: eventSignal });

    workspacePresenceRegistration = {
        updateLease: updateLease,
        dispose: function () {
            window.clearInterval(interval);
            eventController.abort();
            unregisterConnection();
        }
    };
    heartbeat();
}

/**
 * Updates the lease token captured by the active unload listener.
 * @param {string} attachmentId - Current browser attachment.
 * @param {number} leaseGeneration - Current authoritative lease generation.
 */
export function updateWorkspacePresenceLease(attachmentId, leaseGeneration) {
    if (!workspacePresenceRegistration) return;
    workspacePresenceRegistration.updateLease(attachmentId, leaseGeneration);
}

/**
 * Stops browser-originated workspace presence callbacks.
 */
export function stopWorkspacePresence() {
    if (!workspacePresenceRegistration) return;
    workspacePresenceRegistration.dispose();
    workspacePresenceRegistration = null;
}

function isFloatingWindowVisible(win) {
    return win && win.isConnected && win.classList.contains('visible');
}

function normalizeFloatingWindowStack() {
    floatingWindowManager.windows = floatingWindowManager.windows.filter(function (item) {
        return item && item.isConnected;
    });

    const baseZIndex = getFloatingWindowBaseZIndex();
    for (let i = 0; i < floatingWindowManager.windows.length; i++) {
        const zIndex = String(baseZIndex + i);
        if (floatingWindowManager.windows[i].style.zIndex !== zIndex)
            floatingWindowManager.windows[i].style.zIndex = zIndex;
    }
    floatingWindowManager.scheduleContext?.();
}

function getTopVisibleFloatingWindow() {
    for (let i = floatingWindowManager.windows.length - 1; i >= 0; i--) {
        const win = floatingWindowManager.windows[i];
        if (isFloatingWindowVisible(win)) return win;
    }

    return null;
}

function restoreFloatingWindowFocus(win) {
    if (isBlockingModalOpen()) return;
    const activationSequence = floatingWindowManager.activationSequence;
    let focusTarget = floatingWindowManager.lastFocusedElements.get(win);
    if (!focusTarget || !isVisibleElement(focusTarget) || !win.contains(focusTarget)) {
        const semanticTarget = win.dataset.focusTarget || '';
        if (semanticTarget === 'terminal-tab-rename') focusTarget = win.querySelector('.terminal-tab-rename');
        else if (semanticTarget === 'terminal-input') focusTarget = win.querySelector('.terminal-session.active .terminal-ime-input, .terminal-session.active .terminal-virtual-viewport');
        else if (semanticTarget === 'editor-text') {
            focusTarget = win.querySelector('.monaco-editor textarea');
            if (!focusTarget) return;
        }
        else if (semanticTarget.startsWith('control:')) {
            const identity = semanticTarget.substring('control:'.length);
            focusTarget = Array.from(win.querySelectorAll('[data-focus-key], [name], [id], [aria-label]')).find(function (element) {
                return (element.dataset.focusKey || element.getAttribute('name') || element.getAttribute('aria-label') || element.id) === identity && isVisibleElement(element);
            });
        } else if (semanticTarget === 'renamer-method') focusTarget = win.querySelector('[name="renamer-method"], #renamer-method');
        if (!focusTarget) focusTarget = win.querySelector(FLOATING_WINDOW_FOCUS_SELECTOR);
    }
    if (!focusTarget || !focusTarget.isConnected || !win.contains(focusTarget)) return;

    requestAnimationFrame(function () {
        if (activationSequence !== floatingWindowManager.activationSequence || isBlockingModalOpen() || !isFloatingWindowVisible(win) || getTopVisibleFloatingWindow() !== win || !isVisibleElement(focusTarget)) return;

        try {
            focusTarget.focus({ preventScroll: true });
        } catch {
            focusTarget.focus();
        }
    });
}

function bringFloatingWindowToFront(win, restoreFocus) {
    if (!isFloatingWindowVisible(win) || isBlockingModalOpen()) return;
    floatingWindowManager.interactionSequence++;
    floatingWindowManager.windows = floatingWindowManager.windows.filter(function (item) {
        return item !== win && item && item.isConnected;
    });
    floatingWindowManager.windows.push(win);
    win.dataset.mruOrder = String(floatingWindowManager.nextMruOrder++);
    normalizeFloatingWindowStack();

    if (restoreFocus) restoreFloatingWindowFocus(win);
}

function activateTopVisibleFloatingWindow() {
    normalizeFloatingWindowStack();
    const topWindow = getTopVisibleFloatingWindow();
    if (topWindow) restoreFloatingWindowFocus(topWindow);
}

function registerFloatingWindow(win) {
    if (floatingWindowManager.windows.includes(win)) return;
    const savedMruOrder = Number(win.dataset.mruOrder || 0);
    if (!floatingWindowManager.windows.includes(win)) {
        floatingWindowManager.windows.push(win);
    }

    floatingWindowManager.windows.sort(function (left, right) {
        return Number(left.dataset.mruOrder || 0) - Number(right.dataset.mruOrder || 0);
    });
    floatingWindowManager.nextMruOrder = Math.max(floatingWindowManager.nextMruOrder, savedMruOrder + 1);

    const activeElement = document.activeElement;
    if (activeElement && win.contains(activeElement)) {
        floatingWindowManager.lastFocusedElements.set(win, activeElement);
    }

    normalizeFloatingWindowStack();
    if (isFloatingWindowVisible(win)) {
        if (savedMruOrder > 0) {
            if (getTopVisibleFloatingWindow() === win) restoreFloatingWindowFocus(win);
        } else if (!floatingWindowManager.pendingActivation) {
            bringFloatingWindowToFront(win, true);
        }
    }
}

/** Reserves the latest activation intent before Blazor makes the DOM visible. */
export function requestFloatingWindowActivation(windowId) {
    floatingWindowManager.interactionSequence++;
    if (isBlockingModalOpen()) {
        cancelFloatingWindowActivation(windowId);
        return 0;
    }
    const sequence = ++floatingWindowManager.activationSequence;
    floatingWindowManager.pendingActivation = { windowId, sequence };
    return sequence;
}

/** Invalidates only the activation still owned by the closing/minimizing window. */
export function cancelFloatingWindowActivation(windowId, sequence = null) {
    if (floatingWindowManager.pendingActivation?.windowId === windowId &&
        (sequence === null || floatingWindowManager.pendingActivation.sequence === sequence)) {
        floatingWindowManager.pendingActivation = null;
        floatingWindowManager.activationSequence++;
    }
}

/** Separates a modal rejection from an intent superseded by a newer activation. */
export function activateFloatingWindowWithResult(windowId, sequence = null) {
    if (isBlockingModalOpen()) {
        cancelFloatingWindowActivation(windowId, sequence);
        return 'blocked';
    }
    if (sequence !== null) {
        const pending = floatingWindowManager.pendingActivation;
        if (pending?.windowId !== windowId || pending.sequence !== sequence) return 'obsolete';
        floatingWindowManager.pendingActivation = null;
    } else {
        floatingWindowManager.pendingActivation = null;
        floatingWindowManager.activationSequence++;
    }
    const win = document.getElementById(windowId);
    if (!isFloatingWindowVisible(win)) return 'unavailable';
    bringFloatingWindowToFront(win, true);
    return 'activated';
}

/** Focus guards use the one shared stack, including deferred renderer frames. */
export function isTopVisibleFloatingWindow(windowId, sequence = null) {
    return (sequence === null || sequence === floatingWindowManager.activationSequence) &&
        (!floatingWindowManager.pendingActivation || floatingWindowManager.pendingActivation.windowId === windowId) &&
        !isBlockingModalOpen() && getTopVisibleFloatingWindow()?.id === windowId;
}

/**
 * Register global keyboard event listener
 * @param {object} dotNetRef - .NET DotNetObjectReference for callbacks
 */
export function captureKeyboard(dotNetRef) {
    disposeKeyboardCapture();
    const eventController = new AbortController();
    let disposed = false;
    let contextFrame = 0;
    let lastContext = '';
    let pendingContext = Promise.resolve();
    const semanticControlSelector = '[role="combobox"], [role="listbox"], [role="spinbutton"], [role="slider"], [role="tab"], .rz-column-picker';
    const nativeOwnerSelector = 'button, a, input, textarea, select, [contenteditable], [role="button"], [role="menu"], [role="menuitem"], [role="tree"], [role="treeitem"], .rz-tree, .radzen-panel-tree, .rz-menu, .rz-navigation-item, ' + semanticControlSelector;
    const dialogSelector = '[aria-modal="true"], .rz-dialog-wrapper, .rz-dialog-mask, .rz-context-menu';

    function reportContext() {
        contextFrame = 0;
        if (disposed || !isCircuitConnected()) return;
        const activeEl = document.activeElement;
        const modal = isBlockingModalOpen();
        const terminalWindow = document.getElementById('terminal-window');
        const editorWindow = document.getElementById('editor-window');
        const inTerminal = terminalWindow && terminalWindow.classList.contains('visible') &&
            (terminalWindow.contains(activeEl) || activeEl?.closest?.('.terminal-body'));
        const inEditor = editorWindow && editorWindow.classList.contains('visible') &&
            (editorWindow.contains(activeEl) || activeEl?.closest?.('#monaco-container'));
        const hasDialog = Array.from(document.querySelectorAll(dialogSelector)).some(isVisibleElement);
        const semanticOwner = activeEl?.closest?.(semanticControlSelector);
        const nativeOwner = activeEl?.closest?.(nativeOwnerSelector);
        const fileSurface = activeEl?.closest?.('.radzen-panel-filelist, .radzen-file-grid-host');
        const workspaceBody = (!activeEl || activeEl === document.body) && document.querySelector('.radzen-file-panel.active');
        const input = activeEl && ['INPUT', 'TEXTAREA'].includes(activeEl.tagName);
        const inputInDialog = input && activeEl.closest('.renamer-window');
        const available = !modal && !inTerminal && !inEditor && !hasDialog && !semanticOwner;
        const general = Boolean(available && !input);
        const control = Boolean(available && (!input || !inputInDialog));
        const navigation = Boolean(general && !nativeOwner && (fileSurface || workspaceBody));
        const panelSwitch = Boolean(general && !nativeOwner && activeEl?.closest?.('.radzen-panel-filelist'));
        const values = [general, control, navigation, panelSwitch, !modal, modal, getTopVisibleFloatingWindow()?.id || ''];
        const signature = values.join('|');
        if (signature === lastContext) return;
        lastContext = signature;
        // Serialize notifications; a callback already in flight cannot overtake a newer context.
        pendingContext = pendingContext.then(function () {
            if (disposed || !isCircuitConnected()) return;
            return invokeCircuitMethod(dotNetRef, 'OnKeyboardContextChanged', ...values);
        }).catch(function () { });
    }

    function scheduleContext() {
        if (!disposed && !contextFrame) contextFrame = requestAnimationFrame(reportContext);
    }
    floatingWindowManager.scheduleContext = scheduleContext;

    document.addEventListener('focusin', scheduleContext, { signal: eventController.signal });
    document.addEventListener('focusout', scheduleContext, { signal: eventController.signal });
    const contextLayerSelector = dialogSelector + ', dialog, .terminal-window, .editor-window, .renamer-window';
    const contextObserver = new MutationObserver(function (records) {
        if (records.some(function (record) {
            if (record.type === 'attributes') return record.target.matches(contextLayerSelector);
            return [...record.addedNodes, ...record.removedNodes].some(function (node) {
                return node.nodeType === 1 && (node.matches(contextLayerSelector) || node.querySelector(contextLayerSelector) || node.contains(document.activeElement));
            });
        })) scheduleContext();
    });
    contextObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'open', 'aria-modal'] });
    const unregisterContext = registerCircuitParticipant({
        phase: 'presence',
        recover: async function (isCurrent) {
            if (disposed || !isCurrent()) return;
            lastContext = '';
            scheduleContext();
        }
    });
    scheduleContext();
    document.addEventListener('keydown', function (e) {
        if (disposed) return;
        scheduleContext();
        const key = e.key;
        const ctrl = e.ctrlKey;
        const shift = e.shiftKey;
        const alt = e.altKey;

        // F12 belongs to Commander before terminal, Monaco or input dispatch.
        if (key === 'F12' && !ctrl && !shift && !alt && !e.metaKey) {
            e.preventDefault();
            e.stopImmediatePropagation();
            if (!isBlockingModalOpen()) {
                invokeCircuitMethod(dotNetRef, 'OnKeyDown', key, ctrl, shift, alt).catch(function () { });
            }
            return;
        }

        // Check if focus is inside the terminal renderer
        const terminalWindow = document.getElementById('terminal-window');
        const activeEl = document.activeElement;
        if (isBlockingModalOpen()) return;
        // Parent navigation is a native button action, not a Commander cursor action.
        if (activeEl?.closest?.('.bivium-parent-action') && ['Enter', ' ', 'Spacebar'].includes(key)) {
            return;
        }
        const inTerminal = terminalWindow &&
            terminalWindow.classList.contains('visible') &&
            (terminalWindow.contains(activeEl) || (activeEl && activeEl.closest && activeEl.closest('.terminal-body')));

        // Check if focus is inside the Monaco editor - let Monaco handle input
        const editorWindow = document.getElementById('editor-window');
        const inEditor = editorWindow &&
            editorWindow.classList.contains('visible') &&
            (editorWindow.contains(activeEl) || (activeEl && activeEl.closest && activeEl.closest('#monaco-container')));

        // If terminal has focus, don't intercept anything: every key belongs to the shell
        if (inTerminal) {
            return;
        }

        // If Monaco editor has focus, only block browser defaults (Ctrl+S save page)
        if (inEditor) {
            if (key === 's' && ctrl) {
                e.preventDefault();
            }
            return;
        }

        // If a dialog overlay is visible, let the dialog handle keyboard events
        const hasDialog = Array.from(document.querySelectorAll(dialogSelector)).some(function (element) {
            const style = globalThis.getComputedStyle(element);
            return style.display !== 'none' && style.visibility !== 'hidden' && element.getClientRects().length > 0;
        });
        if (hasDialog || isBlockingModalOpen()) {
            return;
        }

        const nativeOwner = activeEl?.closest?.(nativeOwnerSelector);
        // Semantic input widgets own their keys (including Ctrl chords), after reserved F12.
        if (activeEl?.closest?.(semanticControlSelector)) return;
        // Typeahead appartiene al menu prima del dispatch Commander; chord espliciti e F12 restano invariati
        if (!ctrl && !alt && !e.metaKey && /^[\p{L}\p{N}]$/u.test(key) && activeEl?.closest?.('[role="menubar"], [role="menu"], .rz-menu')) return;

        // Navigation belongs only to the file surface, never to native controls or trees.
        if (['Enter', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown', 'Delete', ' ', 'Spacebar'].includes(key)) {
            const fileSurface = activeEl?.closest?.('.radzen-panel-filelist, .radzen-file-grid-host');
            const workspaceBody = (!activeEl || activeEl === document.body) && document.querySelector('.radzen-file-panel.active');
            if (nativeOwner || (!fileSurface && !workspaceBody)) return;
        }

        // If an input or textarea has focus, check context
        const tagName = activeEl ? activeEl.tagName : '';
        if (tagName === 'INPUT' || tagName === 'TEXTAREA') {
            // Check if the input is inside a modeless window (renamer)
            const inDialog = activeEl.closest('.renamer-window');

            if (ctrl && !inDialog) {
                // Ctrl+key on path bar: blur and handle as file operation
                activeEl.blur();
                // Fall through to normal key handling
            } else {
                // Inside a dialog or regular typing: let the input handle it
                if (key === 'Escape') {
                    invokeCircuitMethod(dotNetRef, 'OnKeyDown', key, ctrl, shift, alt).catch(function () { });
                }
                return;
            }
        }

        // Intercept F5 to prevent browser refresh
        if (key === 'F5') {
            e.preventDefault();
        }

        // Intercept Ctrl+P to prevent browser print
        if (key === 'p' && e.ctrlKey) {
            e.preventDefault();
        }

        // Intercept Ctrl+A to prevent browser select all
        if (key === 'a' && e.ctrlKey) {
            e.preventDefault();
        }

        // Intercept Ctrl+C/X/V to prevent browser clipboard operations
        if ((key === 'c' || key === 'x' || key === 'v') && e.ctrlKey && !shift) {
            e.preventDefault();
        }

        // Intercept Ctrl+O to prevent browser open file
        if (key === 'o' && e.ctrlKey) {
            e.preventDefault();
        }

        // Intercept F4/Shift+F4 (edit, new file) and F7 (new folder) to prevent browser defaults
        if ((key === 'F4' && !ctrl && !alt) || (key === 'F7' && !ctrl && !shift && !alt)) {
            e.preventDefault();
        }

        // Intercept Shift+F10 for context menu
        if (key === 'F10' && shift) {
            e.preventDefault();
        }

        // Commander switches panels only while focus is on the file-list surface
        if (key === 'Tab') {
            const fileSurface = activeEl?.closest?.('.radzen-panel-filelist');
            if (!fileSurface || nativeOwner) return;
            e.preventDefault();
        }

        // Commander owns semantic navigation; do not also run Radzen's viewport-local keyboard reducer
        if (activeEl?.closest?.('.radzen-file-grid-host') && ['ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'].includes(key)) {
            e.preventDefault();
            e.stopImmediatePropagation();
        }

        // Send key event to .NET
        invokeCircuitMethod(dotNetRef, 'OnKeyDown', key, ctrl, shift, alt).catch(function () { });
    }, { capture: true, signal: eventController.signal });
    keyboardCaptureRegistration = {
        dispose: function () {
            disposed = true;
            cancelAnimationFrame(contextFrame);
            eventController.abort();
            contextObserver.disconnect();
            unregisterContext();
            if (floatingWindowManager.scheduleContext === scheduleContext)
                floatingWindowManager.scheduleContext = null;
        }
    };
}

export function disposeKeyboardCapture() {
    keyboardCaptureRegistration?.dispose();
    keyboardCaptureRegistration = null;
}

/**
 * Initialize long-press touch handler for the file-list context menu
 * Fires a synthetic contextmenu event after 500ms hold on a file-list panel
 */
export function initLongPress() {
    disposeLongPress();
    const eventController = new AbortController();
    const eventSignal = eventController.signal;
    const HOLD_DURATION = 500;
    const MOVE_THRESHOLD = 10;
    const CLICK_SUPPRESSION = 700;
    let timer = null;
    let startX = 0;
    let startY = 0;
    // True from the long-press (synthetic or native) until the finger is released
    let handled = false;
    let suppressUntil = 0;

    function cancel() {
        if (timer === null) return;
        clearTimeout(timer);
        timer = null;
    }

    document.addEventListener('touchstart', function (e) {
        cancel();
        handled = false;
        // Multi-touch is a pinch/zoom gesture, never a context request
        if (e.touches.length !== 1) return;
        const touch = e.touches[0];
        if (!(e.target instanceof Element) || !e.target.closest('.radzen-panel-filelist')) return;
        startX = touch.clientX;
        startY = touch.clientY;

        timer = setTimeout(function () {
            timer = null;
            const target = document.elementFromPoint(startX, startY);
            if (!target?.closest('.radzen-panel-filelist')) return;
            handled = true;
            target.dispatchEvent(new MouseEvent('contextmenu', {
                bubbles: true,
                cancelable: true,
                clientX: startX,
                clientY: startY
            }));
        }, HOLD_DURATION);
    }, { passive: true, signal: eventSignal });

    document.addEventListener('touchmove', function (e) {
        if (timer === null) return;
        if (e.touches.length !== 1) {
            cancel();
            return;
        }
        const touch = e.touches[0];
        // Cancel if finger moved too far (user is scrolling)
        if (Math.abs(touch.clientX - startX) > MOVE_THRESHOLD || Math.abs(touch.clientY - startY) > MOVE_THRESHOLD) cancel();
    }, { passive: true, signal: eventSignal });

    document.addEventListener('touchend', function (e) {
        cancel();
        if (!handled) return;
        handled = false;
        // The release after a long-press must not also activate the row or reopen the menu
        suppressUntil = performance.now() + CLICK_SUPPRESSION;
        if (e.cancelable) e.preventDefault();
    }, { passive: false, signal: eventSignal });
    document.addEventListener('touchcancel', function () {
        cancel();
        handled = false;
    }, { passive: true, signal: eventSignal });

    // A browser long-press also raises a trusted contextmenu: only one of the two may open the menu
    document.addEventListener('contextmenu', function (e) {
        if (!e.isTrusted) return;
        if (handled || performance.now() < suppressUntil) {
            e.preventDefault();
            e.stopImmediatePropagation();
        } else if (timer !== null) {
            cancel();
            handled = true;
        }
    }, { capture: true, signal: eventSignal });

    document.addEventListener('click', function (e) {
        if (performance.now() >= suppressUntil) return;
        suppressUntil = 0;
        e.preventDefault();
        e.stopImmediatePropagation();
    }, { capture: true, signal: eventSignal });

    longPressRegistration = {
        dispose: function () {
            cancel();
            eventController.abort();
        }
    };
}

export function disposeLongPress() {
    longPressRegistration?.dispose();
    longPressRegistration = null;
}

/** Returns true only for a visible native blocking modal (including reconnect). */
export function isBlockingModalOpen() {
    return getTopBlockingModal() !== null;
}

/**
 * Send a POST request with JSON body and return success status
 * @param {string} url - Request URL
 * @param {string} jsonBody - JSON string to send as a body
 * @returns {Promise<boolean>} True if response is OK
 */
export async function postJson(url, jsonBody) {
    try {
        const response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: jsonBody || '{}'
        });
        return response.ok;
    } catch (err) {
        return false;
    }
}

/**
 * Send a POST request and return parsed JSON payload
 * @param {string} url - Request URL
 * @param {string} jsonBody - JSON string to send as a body
 * @returns {Promise<object>} Response envelope
 */
export async function postJsonResult(url, jsonBody, attachmentId = '', leaseGeneration = 0) {
    return await sendJsonResult(url, 'POST', jsonBody || '{}', attachmentId, leaseGeneration);
}

/**
 * Send a PUT request and return parsed JSON payload
 * @param {string} url - Request URL
 * @param {string} jsonBody - JSON string to send as a body
 * @returns {Promise<object>} Response envelope
 */
export async function putJsonResult(url, jsonBody, attachmentId = '', leaseGeneration = 0) {
    return await sendJsonResult(url, 'PUT', jsonBody || '{}', attachmentId, leaseGeneration);
}

/**
 * Send a GET request and return parsed JSON payload
 * @param {string} url - Request URL
 * @returns {Promise<object>} Response envelope
 */
export async function getJsonResult(url) {
    try {
        const response = await fetch(url, {
            method: 'GET'
        });
        const text = await response.text();
        let data = null;
        if (text) {
            try {
                data = JSON.parse(text);
            } catch (err) {
                data = null;
            }
        }
        return {
            ok: response.ok,
            status: response.status,
            text: text,
            data: data
        };
    } catch (err) {
        return {
            ok: false,
            status: 0,
            text: '',
            data: null
        };
    }
}

/**
 * Reload the current browser page
 */
export function reloadPage() {
    window.location.reload();
}

/**
 * Copy text to the system clipboard
 * @param {string} text - Text to copy
 * @returns {Promise<boolean>} True if copy succeeded
 */
export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text || '');
        return true;
    } catch (err) {
        return false;
    }
}

function createMutationHeaders(attachmentId, leaseGeneration) {
    const headers = { 'Content-Type': 'application/json' };
    if (attachmentId) {
        headers['X-Bivium-Attachment'] = attachmentId;
        headers['X-Bivium-Lease-Generation'] = String(leaseGeneration);
    }
    return headers;
}

async function sendJsonResult(url, method, jsonBody, attachmentId, leaseGeneration) {
    try {
        const response = await fetch(url, {
            method: method,
            headers: createMutationHeaders(attachmentId, leaseGeneration),
            body: jsonBody
        });
        const text = await response.text();
        let data = null;
        if (text) {
            try {
                data = JSON.parse(text);
            } catch (err) {
                data = null;
            }
        }
        return {
            ok: response.ok,
            status: response.status,
            text: text,
            data: data
        };
    } catch (err) {
        return {
            ok: false,
            status: 0,
            text: '',
            data: null
        };
    }
}

/**
 * Scrolls the active panel's cursor row into view
 */
export function scrollCursorIntoView(cursorIndex = -1) {
    const radzenPanel = document.querySelector('.radzen-file-panel.active');
    const scroller = radzenPanel?.querySelector('.rz-data-grid-data');
    if (!scroller) return;
    const focusedRow = radzenPanel.querySelector('tr.bivium-focused');
    const index = cursorIndex >= 0 ? cursorIndex : Number(focusedRow?.dataset.entryIndex ?? -1);
    scrollToFileListEntry(scroller, index, '', false, function () {
        // Il focus DOM segue il cursore solo se è già sulla superficie file (o nel body): mai rubato a input, menu o finestre
        const active = document.activeElement;
        if (active && active !== document.body && !active.closest?.('.radzen-panel-filelist')) return;
        if (isBlockingModalOpen() || desktopPublications.freeze) return;
        const row = radzenPanel.querySelector('tr.bivium-focused');
        if (row?.isConnected && row !== active) row.focus({ preventScroll: true });
    });
}

const fileListScrollRequests = new WeakMap();

/**
 * Materializes a virtual entry using measured row geometry, then aligns the real row.
 * Indices are transient hints only; restoration still resolves the semantic path.
 */
function scrollToFileListEntry(scroller, index, path, alignStart, onComplete) {
    fileListScrollRequests.get(scroller)?.();
    if (index < 0) {
        onComplete?.();
        return;
    }

    const deadline = performance.now() + 2000;
    const quietPeriod = 160;
    let stableSince = 0;
    let stableGeometry = null;
    let stableRows = '';
    let finished = false;
    let frame = 0;
    // Le schede in background sospendono requestAnimationFrame: la scadenza non può dipendere dai frame
    let deadlineTimer = 0;
    const inputs = ['wheel', 'touchstart', 'pointerdown', 'keydown'];
    function finish() {
        if (finished) return;
        finished = true;
        cancelAnimationFrame(frame);
        window.clearTimeout(deadlineTimer);
        for (const input of inputs) scroller.removeEventListener(input, finish);
        if (fileListScrollRequests.get(scroller) === finish) fileListScrollRequests.delete(scroller);
        onComplete?.();
    }
    function position() {
        if (finished) return;
        if (!scroller.isConnected || performance.now() >= deadline) {
            finish();
            return;
        }

        const viewport = scroller.getBoundingClientRect();
        const scale = scroller.offsetHeight > 0 ? viewport.height / scroller.offsetHeight : 1;
        const headerHeight = scroller.querySelector('thead')?.getBoundingClientRect().height || 0;
        const rows = Array.from(scroller.querySelectorAll('tr[data-entry-path]'));
        const target = rows.find(row => path ? row.dataset.entryPath === path : Number(row.dataset.entryIndex) === index);
        if (target) {
            const rect = target.getBoundingClientRect();
            const top = viewport.top + headerHeight;
            const bottom = viewport.top + scroller.clientHeight * scale;
            const previousScrollTop = scroller.scrollTop;
            if ((alignStart && Math.abs(rect.top - top) > 0.5) || rect.top < top - 0.5) scroller.scrollTop += (rect.top - top) / scale;
            else if (!alignStart && rect.bottom > bottom + 0.5) scroller.scrollTop += (rect.bottom - bottom) / scale;
            // Virtualize can adjust spacer heights after the target first materializes.
            // Keep ownership until real geometry and the rendered row identities stay quiet.
            const alignedRect = target.getBoundingClientRect();
            const visible = alignedRect.height > 0 && alignedRect.top >= top - 0.5 && alignedRect.bottom <= bottom + 0.5;
            const geometry = [scroller.scrollTop, scroller.scrollHeight, viewport.top, viewport.height, headerHeight, alignedRect.top, alignedRect.bottom];
            const materializedRows = JSON.stringify(rows.map(row => [row.dataset.entryPath, row.dataset.entryIndex, row.dataset.entryRenderIndex]));
            const realigned = Math.abs(scroller.scrollTop - previousScrollTop) > 0.5;
            const changed = !stableGeometry || geometry.some((value, offset) => Math.abs(value - stableGeometry[offset]) > 0.5) || materializedRows !== stableRows;
            if (!visible || realigned || changed) {
                stableSince = performance.now();
                stableGeometry = geometry;
                stableRows = materializedRows;
            } else if (performance.now() - stableSince >= quietPeriod) {
                finish();
                return;
            }
            frame = requestAnimationFrame(position);
            return;
        }

        stableSince = 0;
        stableGeometry = null;
        stableRows = '';
        const sample = rows[0];
        const rect = sample?.getBoundingClientRect();
        const height = rect && rect.height >= 1 ? rect.height / scale : 20;
        const parentHost = scroller.closest('[data-parent-row-count]') || scroller.querySelector('[data-parent-row-count]');
        const parentCount = Number(parentHost?.dataset.parentRowCount ?? 0);
        const hasVisualIndex = sample?.hasAttribute('data-entry-render-index');
        const sampleIndex = hasVisualIndex ? Number(sample.dataset.entryRenderIndex) : Number(sample?.dataset.entryIndex ?? 0) + (sample && parentHost ? parentCount : 0);
        const visualOffset = hasVisualIndex ? Number(sample.dataset.entryRenderIndex) - Number(sample.dataset.entryIndex) : parentCount;
        const visualIndexForTarget = hasVisualIndex || parentHost ? index + visualOffset : index;
        const sampleTop = rect ? scroller.scrollTop + (rect.top - viewport.top - headerHeight) / scale : 0;
        scroller.scrollTop = Math.max(0, sampleTop + (visualIndexForTarget - sampleIndex) * height);
        frame = requestAnimationFrame(position);
    }

    fileListScrollRequests.set(scroller, finish);
    for (const input of inputs) scroller.addEventListener(input, finish, { passive: true });
    deadlineTimer = window.setTimeout(finish, deadline - performance.now());
    frame = requestAnimationFrame(position);
}

const radzenFilePanelTrackers = new Map();
const radzenPathAdapters = new Map();
const radzenColumnResizers = new Map();
const MIN_RADZEN_FILE_COLUMN_WIDTH = 48;

/**
 * Registra scroll semantico e misura pagina sul contenitore Radzen stabile.
 * @param {string} panelId - Identificatore applicativo del pannello.
 * @param {object} dotNetReference - Proprietario dei callback.
 */
export function registerRadzenFilePanel(panelId, dotNetReference) {
    unregisterRadzenFilePanel(panelId);
    const host = document.getElementById(panelId + '-filelist');
    const scroller = host?.querySelector('.rz-data-grid-data');
    if (!host || !scroller || !dotNetReference) return;

    const tracker = { host, scroller, dotNetReference, timer: 0, restoring: false, pageSize: 0 };
    desktopPublications.panelTrackers.add(tracker);
    tracker.measure = function () {
        if (!isCircuitConnected()) return;
        const rows = Array.from(scroller.querySelectorAll('tr[data-entry-path]'));
        const sampleHeight = rows[0]?.getBoundingClientRect().height || 28;
        const pageSize = Math.max(1, Math.floor(scroller.clientHeight / sampleHeight));
        if (pageSize === tracker.pageSize) return;
        invokeCircuitMethod(dotNetReference, 'OnRadzenFileListPageSizeChanged', pageSize).then(function () {
            tracker.pageSize = pageSize;
        }).catch(function () { });
    };
    tracker.listener = function () {
        if (tracker.restoring) return;
        window.clearTimeout(tracker.timer);
        tracker.timer = window.setTimeout(function () {
            const top = scroller.getBoundingClientRect().top;
            const rows = scroller.querySelectorAll('tr[data-entry-path]');
            for (const row of rows) {
                if (row.getBoundingClientRect().bottom > top + 1) {
                    invokeCircuitMethod(dotNetReference, 'OnRadzenFileListScrollAnchorChanged', row.dataset.entryPath || '').catch(function () { });
                    break;
                }
            }
            tracker.measure();
        }, 120);
    };
    scroller.addEventListener('scroll', tracker.listener, { passive: true });
    tracker.resizeObserver = new ResizeObserver(tracker.measure);
    tracker.resizeObserver.observe(scroller);
    tracker.measure();
    radzenFilePanelTrackers.set(panelId, tracker);
}

/**
 * Rimuove tracker e richieste di scroll di un pannello Radzen.
 * @param {string} panelId - Identificatore applicativo del pannello.
 */
export function unregisterRadzenFilePanel(panelId) {
    const tracker = radzenFilePanelTrackers.get(panelId);
    if (!tracker) return;
    fileListScrollRequests.get(tracker.scroller)?.();
    window.clearTimeout(tracker.timer);
    tracker.scroller.removeEventListener('scroll', tracker.listener);
    desktopPublications.panelTrackers.delete(tracker);
    tracker.resizeObserver.disconnect();
    radzenFilePanelTrackers.delete(panelId);
}

/**
 * Ripristina una riga Radzen per percorso usando l'indice soltanto come hint transitorio.
 * @param {string} panelId - Identificatore applicativo del pannello.
 * @param {string} path - Percorso semantico da risolvere.
 * @param {number} index - Indice corrente usato per materializzare la riga.
 */
export function restoreRadzenFilePanelScroll(panelId, path, index) {
    const tracker = radzenFilePanelTrackers.get(panelId);
    if (!tracker || index < 0) return;
    tracker.restoring = true;
    scrollToFileListEntry(tracker.scroller, index, path, true, function () {
        tracker.restoring = false;
    });
}

/**
 * Misura le larghezze visibili delle cinque colonne Radzen.
 * @param {string} hostId - Identificatore del wrapper stabile della griglia.
 * @returns {number[]} Larghezze dei cinque header, oppure un array vuoto.
 */
export async function measureRadzenFileColumns(hostId) {
    const host = document.getElementById(hostId);
    if (!host) return [];
    for (let attempt = 0; attempt < 30; attempt++) {
        const headers = Array.from(host.querySelectorAll('thead th')).filter(function (header) {
            return header.getClientRects().length > 0;
        });
        const widths = headers.map(function (header) { return header.getBoundingClientRect().width; });
        if (widths.length === 5 && widths.every(function (width) { return Number.isFinite(width) && width > 0; })) return widths;
        await new Promise(requestAnimationFrame);
    }
    return [];
}

/**
 * Registra un resize immediato sui resizer Radzen senza attendere il roundtrip Blazor Server del pointerdown.
 * @param {string} hostId - Identificatore del wrapper stabile della griglia.
 * @param {object} dotNetReference - Proprietario del layout semantico.
 */
export function registerRadzenColumnResizer(hostId, dotNetReference) {
    const host = document.getElementById(hostId);
    const table = host?.querySelector('.rz-grid-table');
    if (!host || !table || !dotNetReference) return;
    const handles = Array.from(table.querySelectorAll('thead .rz-column-resizer'));
    const existing = radzenColumnResizers.get(hostId);
    if (existing?.table === table && existing.handleCount === handles.length) return;
    existing?.dispose();

    const controller = new AbortController();
    let drag = null;
    function finish(event) {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const completed = drag;
        drag = null;
        if (completed.handle.hasPointerCapture?.(completed.pointerId)) completed.handle.releasePointerCapture(completed.pointerId);
        // pointercancel annulla il gesto: le larghezze tornano al layout semantico al prossimo render
        if (!completed.changed || event.type === 'pointercancel') return;
        invokeCircuitMethod(dotNetReference, 'OnRadzenColumnWidthsChanged', completed.ids, completed.widths).catch(function () { });
        event.preventDefault();
    }
    handles.forEach(function (handle, handleIndex) {
        // Impedisce al browser touch di trasformare il trascinamento in scroll
        handle.style.touchAction = 'none';
        handle.addEventListener('pointerdown', function (event) {
            if (!event.isPrimary || event.button !== 0) return;
            const headers = Array.from(table.querySelectorAll('thead th')).filter(function (header) { return header.getClientRects().length > 0; });
            const columns = Array.from(table.querySelectorAll(':scope > colgroup > col'));
            if (headers.length !== columns.length || handleIndex >= headers.length) return;
            const ids = headers.map(function (header) {
                const semanticClass = Array.from(header.classList).find(value => value.startsWith('bivium-column-')) || '';
                return semanticClass.substring('bivium-column-'.length);
            });
            if (ids.some(id => !id)) return;
            drag = {
                pointerId: event.pointerId,
                handle,
                startX: event.clientX,
                index: handleIndex,
                startWidth: headers[handleIndex].getBoundingClientRect().width,
                widths: headers.map(header => header.getBoundingClientRect().width),
                ids,
                columns,
                changed: false
            };
            handle.setPointerCapture?.(event.pointerId);
            event.preventDefault();
            event.stopImmediatePropagation();
        }, { capture: true, signal: controller.signal });
        // Radzen avvia il proprio resize sul mousedown compatibile: resta di competenza dell'adapter
        handle.addEventListener('mousedown', function (event) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }, { capture: true, signal: controller.signal });
    });
    document.addEventListener('pointermove', function (event) {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const index = drag.index;
        const width = Math.max(MIN_RADZEN_FILE_COLUMN_WIDTH, drag.startWidth + event.clientX - drag.startX);
        if (Math.abs(width - drag.widths[index]) < 0.01) return;
        drag.widths[index] = width;
        drag.columns[index].style.width = width + 'px';
        drag.changed = true;
        event.preventDefault();
    }, { signal: controller.signal });
    document.addEventListener('pointerup', finish, { signal: controller.signal });
    document.addEventListener('pointercancel', finish, { signal: controller.signal });
    // Il mouseup sintetico di beginWorkspaceHandoff abbandona il gesto senza pubblicarlo
    document.addEventListener('mouseup', function (event) { if (!event.isTrusted) drag = null; }, { signal: controller.signal });

    const registration = {
        table,
        handleCount: handles.length,
        dispose: function () {
            drag = null;
            controller.abort();
            if (radzenColumnResizers.get(hostId) === registration) radzenColumnResizers.delete(hostId);
        }
    };
    radzenColumnResizers.set(hostId, registration);
}

/**
 * Rimuove l'adapter di resize colonne di una griglia Radzen.
 * @param {string} hostId - Identificatore del wrapper stabile della griglia.
 */
export function unregisterRadzenColumnResizer(hostId) {
    radzenColumnResizers.get(hostId)?.dispose();
}

/**
 * Installa Tab, conferma Enter, annullamento Escape e guardia del focus sull'input interno di RadzenAutoComplete.
 * @param {string} hostId - Identificatore del wrapper applicativo.
 * @param {object} dotNetReference - Proprietario del callback autocomplete.
 * @param {number} generation - Lease del mount.
 * @param {object} draft - Draft visuale da idratare.
 * @returns {boolean} True quando l'adapter è installato sull'input corrente.
 */
export function installRadzenPathAdapter(hostId, dotNetReference, generation, draft) {
    const host = document.getElementById(hostId);
    const input = host?.querySelector('input');
    if (!host || !input || !dotNetReference || !draft) return false;
    const existing = radzenPathAdapters.get(hostId);
    if (existing?.input === input && existing.generation === generation && existing.basePath === draft.basePath) return true;
    existing?.dispose();
    // La lista viene portata nel body all'apertura: il riferimento si cattura finché è ancora nel wrapper
    let list = host.querySelector('.rz-autocomplete-list');

    const controller = new AbortController();
    let disposed = false;
    let inputGeneration = 0;
    let restoring = true;
    let pending = Promise.resolve();
    function track(promise) {
        desktopPublications.pending.add(promise);
        promise.then(() => desktopPublications.pending.delete(promise), () => { desktopPublications.pending.delete(promise); desktopPublications.failures++; });
        return promise;
    }
    function capture() {
        if (disposed || restoring || !input.isConnected) return;
        const value = input.value;
        const start = input.selectionStart ?? 0;
        const end = input.selectionEnd ?? start;
        const focused = desktopPublications.freeze ? desktopPublications.freeze.active === input : document.activeElement === input;
        pending = track(pending.then(async function () {
            if (disposed) return;
            const accepted = await invokeCircuitMethod(dotNetReference, 'OnRadzenPathDraftChanged', generation, draft.basePath, value, start, end, focused);
            if (!accepted) desktopPublications.failures++;
        }));
    }
    input.addEventListener('input', function () {
        inputGeneration++;
        capture();
    }, { signal: controller.signal });
    for (const name of ['select', 'keyup', 'mouseup', 'focus', 'blur']) input.addEventListener(name, function () { if (!desktopPublications.freeze) capture(); }, { signal: controller.signal });
    function getList() {
        if (!list?.isConnected) list = document.getElementById(input.getAttribute('aria-controls') || '') || host.querySelector('.rz-autocomplete-list');
        return list;
    }
    // Il popup portato nel body ruberebbe il focus: il blur chiuderebbe l'editor prima della selezione
    document.addEventListener('mousedown', function (event) {
        if (document.activeElement !== input || !(event.target instanceof Element)) return;
        const panel = event.target.closest('.rz-autocomplete-panel');
        if (panel && panel.contains(getList())) event.preventDefault();
    }, { capture: true, signal: controller.signal });
    input.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') {
            if (event.isComposing || desktopPublications.freeze) return;
            pending = track(pending.then(function () {
                if (disposed) return;
                return invokeCircuitMethod(dotNetReference, 'OnRadzenPathCancel', generation, draft.basePath);
            }));
            return;
        }
        if (event.key === 'Enter') {
            // Con un suggerimento evidenziato Enter appartiene a Radzen, che lo seleziona
            const current = getList();
            if (current && isVisibleElement(current) && current.querySelector('.rz-state-highlight')) return;
            if (event.isComposing || desktopPublications.freeze) return;
            // Conferma con il valore DOM reale, non con un ValueChanged eventualmente ancora in volo
            const value = input.value;
            pending = track(pending.then(function () {
                if (disposed) return;
                return invokeCircuitMethod(dotNetReference, 'OnRadzenPathCommit', generation, draft.basePath, value);
            }));
            return;
        }
        if (event.key !== 'Tab') return;
        event.preventDefault();
        event.stopImmediatePropagation();
        const requestedGeneration = inputGeneration;
        pending = track(pending.then(function () {
            if (disposed) return null;
            return invokeCircuitMethod(dotNetReference, 'OnRadzenPathTab', generation, draft.basePath, input.value);
        }).then(function (value) {
            if (disposed || requestedGeneration !== inputGeneration || typeof value !== 'string') return;
            input.value = value;
            queueMicrotask(capture);
        }));
    }, { capture: true, signal: controller.signal });
    const registration = {
        input,
        generation,
        basePath: draft.basePath,
        capture,
        get restoring() { return restoring; },
        dispose: function () {
            disposed = true;
            controller.abort();
            desktopPublications.pathTrackers.delete(registration);
            if (radzenPathAdapters.get(hostId) === registration) radzenPathAdapters.delete(hostId);
        }
    };
    radzenPathAdapters.set(hostId, registration);
    desktopPublications.pathTrackers.add(registration);
    requestVisualFrame(function () {
        if (disposed || !input.isConnected) return;
        // Hydration visuale senza input/change né navigazione
        input.value = draft.text;
        input.setSelectionRange(draft.selectionStart, draft.selectionEnd);
        if (draft.focused && !desktopPublications.freeze && !isBlockingModalOpen()) input.focus({ preventScroll: true });
        restoring = false;
    });
    return true;
}

/**
 * Rimuove l'adapter di un editor percorso Radzen.
 * @param {string} hostId - Identificatore del wrapper applicativo.
 */
export function uninstallRadzenPathAdapter(hostId) {
    radzenPathAdapters.get(hostId)?.dispose();
}

/**
 * Scales and clamps persisted floating-window geometry to a viewport.
 * @param {object} geometry - Persisted geometry and its source viewport.
 * @param {number} viewportWidth - Current viewport width.
 * @param {number} viewportHeight - Current viewport height.
 * @param {boolean} scaleFromSavedViewport - Whether coordinates should be scaled proportionally.
 * @param {number} usableTop - First usable viewport coordinate below fixed application chrome.
 * @returns {object} Reachable geometry.
 */
export function computeWindowGeometry(geometry, viewportWidth, viewportHeight, scaleFromSavedViewport, usableTop = 0) {
    const safeViewportWidth = Number.isFinite(Number(viewportWidth)) && Number(viewportWidth) > 0 ? Number(viewportWidth) : 1;
    const safeViewportHeight = Number.isFinite(Number(viewportHeight)) && Number(viewportHeight) > 0 ? Number(viewportHeight) : 1;
    const safeUsableTop = Number.isFinite(Number(usableTop)) ? Math.max(0, Math.min(Number(usableTop), safeViewportHeight - 1)) : 0;
    let left = Number(geometry?.left);
    let top = Number(geometry?.top);
    let width = Number(geometry?.width);
    let height = Number(geometry?.height);
    const savedViewportWidth = Number(geometry?.viewportWidth);
    const savedViewportHeight = Number(geometry?.viewportHeight);
    if (!Number.isFinite(left)) left = 0;
    if (!Number.isFinite(top)) top = safeUsableTop;
    if (!Number.isFinite(width) || width <= 0) width = 800;
    if (!Number.isFinite(height) || height <= 0) height = 400;
    const savedWidth = Number.isFinite(savedViewportWidth) && savedViewportWidth > 0 ? savedViewportWidth : safeViewportWidth;
    const savedHeight = Number.isFinite(savedViewportHeight) && savedViewportHeight > 0 ? savedViewportHeight : safeViewportHeight;
    if (scaleFromSavedViewport && savedWidth > 0 && savedHeight > 0) {
        left *= safeViewportWidth / savedWidth;
        top *= safeViewportHeight / savedHeight;
    }
    const maxWidth = Math.max(1, safeViewportWidth - 16);
    const maxHeight = Math.max(1, safeViewportHeight - safeUsableTop - 16);
    const minWidth = Math.min(300, maxWidth);
    const minHeight = Math.min(150, maxHeight);
    width = Math.max(minWidth, Math.min(width, maxWidth));
    height = Math.max(minHeight, Math.min(height, maxHeight));
    left = Math.max(0, Math.min(left, Math.max(0, safeViewportWidth - width)));
    top = Math.max(safeUsableTop, Math.min(top, Math.max(safeUsableTop, safeViewportHeight - height)));
    return { left, top, width, height };
}

/**
 * Measures the application chrome that fixed windows must remain below.
 * @returns {number} First usable vertical viewport coordinate.
 */
function getFloatingWindowUsableTop() {
    const menuBar = document.querySelector('.bivium-radzen-menu-bar');
    if (!menuBar) return 0;
    const bottom = Number(menuBar.getBoundingClientRect().bottom);
    return Number.isFinite(bottom) ? Math.max(0, bottom) : 0;
}

/**
 * Initialize drag and resize for a floating window element
 * @param {string} windowId - DOM id of the window container
 * @param {string} titlebarId - DOM id of the draggable titlebar
 * @param {string} resizeHandleId - DOM id of the resize handle
 * @param {object} dotNetReference - Optional callback owner for persisted geometry
 */
export function initWindowDrag(windowId, titlebarId, resizeHandleId, dotNetReference = null, sessionId = '', leaseGeneration = 0) {
    const win = document.getElementById(windowId);
    const titlebar = document.getElementById(titlebarId);
    const resizeHandle = document.getElementById(resizeHandleId);
    if (!win || !titlebar) return;
    const previousRegistration = windowDragRegistrations.get(windowId);
    if (previousRegistration?.element === win && previousRegistration.sessionId === sessionId && previousRegistration.leaseGeneration === leaseGeneration) return;
    if (previousRegistration) previousRegistration.dispose(false);
    registerFloatingWindow(win);
    initializedWindowDragElements.add(win);
    const eventController = new AbortController();
    const eventSignal = eventController.signal;

    let isDragging = false;
    let isResizing = false;
    let activePointerId = null;
    let dragOffsetX = 0;
    let dragOffsetY = 0;
    let geometryTimer = 0;
    let lastNotifiedGeometry = '';
    let geometryInitialized = false;
    let geometrySequence = 0;
    let geometryFlight = Promise.resolve(true);

    function getFocusTarget() {
        const active = document.activeElement;
        if (active && win.contains(active)) {
            if (active.classList.contains('terminal-virtual-viewport') || active.classList.contains('terminal-ime-input')) return 'terminal-input';
            if (active.classList.contains('terminal-tab-rename')) return 'terminal-tab-rename';
            if (windowId === 'editor-window' && active.closest('.monaco-editor')) return 'editor-text';
            const identity = active.dataset.focusKey || active.getAttribute('name') || active.getAttribute('aria-label') || active.id;
            if (identity) return 'control:' + identity;
        }
        return win.dataset.focusTarget || (windowId === 'editor-window' ? 'editor-text' : windowId === 'renamer-window' ? 'renamer-method' : 'terminal-input');
    }

    function clampToViewport(scaleFromSavedViewport) {
        const previous = {
            left: parseFloat(win.style.left),
            top: parseFloat(win.style.top),
            width: parseFloat(win.style.width),
            height: parseFloat(win.style.height)
        };
        const geometry = computeWindowGeometry({
            left: Number.isFinite(previous.left) ? previous.left : win.offsetLeft,
            top: Number.isFinite(previous.top) ? previous.top : win.offsetTop,
            width: Number.isFinite(previous.width) ? previous.width : win.offsetWidth,
            height: Number.isFinite(previous.height) ? previous.height : win.offsetHeight,
            viewportWidth: parseFloat(win.dataset.viewportWidth),
            viewportHeight: parseFloat(win.dataset.viewportHeight)
        }, window.innerWidth, window.innerHeight, scaleFromSavedViewport, getFloatingWindowUsableTop());
        win.style.left = geometry.left + 'px';
        win.style.top = geometry.top + 'px';
        win.style.width = geometry.width + 'px';
        win.style.height = geometry.height + 'px';
        return !Number.isFinite(previous.left) || !Number.isFinite(previous.top) ||
            !Number.isFinite(previous.width) || !Number.isFinite(previous.height) ||
            Math.abs(previous.left - geometry.left) > 0.01 ||
            Math.abs(previous.top - geometry.top) > 0.01 ||
            Math.abs(previous.width - geometry.width) > 0.01 ||
            Math.abs(previous.height - geometry.height) > 0.01;
    }

    function notifyGeometry(force = false) {
        if (!dotNetReference || !isFloatingWindowVisible(win)) return Promise.resolve(true);
        const rect = win.getBoundingClientRect();
        if (![rect.left, rect.top, rect.width, rect.height].every(Number.isFinite) || rect.width <= 0 || rect.height <= 0) return Promise.resolve(false);
        const update = {
            sessionId,
            leaseGeneration,
            sequence: geometrySequence + 1,
            left: rect.left,
            top: rect.top,
            width: rect.width,
            height: rect.height,
            viewportWidth: window.innerWidth,
            viewportHeight: window.innerHeight,
            mruOrder: Number(win.dataset.mruOrder || 0),
            focusTarget: getFocusTarget()
        };
        const signature = [update.left, update.top, update.width, update.height, update.viewportWidth, update.viewportHeight, update.mruOrder, update.focusTarget].join('|');
        if (!force && signature === lastNotifiedGeometry) return geometryFlight;
        geometrySequence = update.sequence;
        lastNotifiedGeometry = signature;
        const interactionSequence = floatingWindowManager.interactionSequence;
        geometryFlight = geometryFlight.then(async function () {
            if (eventSignal.aborted || !isCircuitConnected()) return false;
            const snapshot = await invokeCircuitMethod(dotNetReference, 'OnWindowGeometryChanged', update);
            const acknowledged = Boolean(snapshot && ['left', 'top', 'width', 'height', 'viewportWidth', 'viewportHeight', 'mruOrder', 'focusTarget'].every(key => snapshot[key] === update[key]));
            // The terminal's existing callback returns no value; preserve that adapter contract.
            if (!snapshot || eventSignal.aborted || update.sequence !== geometrySequence || interactionSequence !== floatingWindowManager.interactionSequence || isDragging || isResizing) return acknowledged;
            if (snapshot.width > 0 && snapshot.height > 0) {
                win.style.left = snapshot.left + 'px';
                win.style.top = snapshot.top + 'px';
                win.style.width = snapshot.width + 'px';
                win.style.height = snapshot.height + 'px';
            }
            win.dataset.mruOrder = String(snapshot.mruOrder);
            win.dataset.focusTarget = snapshot.focusTarget;
            floatingWindowManager.windows.sort(function (left, right) {
                return Number(left.dataset.mruOrder || 0) - Number(right.dataset.mruOrder || 0);
            });
            normalizeFloatingWindowStack();
            return acknowledged;
        }).catch(function () { return false; });
        return geometryFlight;
    }

    function scheduleGeometryNotification() {
        if (geometryTimer) return;
        geometryTimer = window.setTimeout(function () {
            geometryTimer = 0;
            notifyGeometry();
        }, 150);
    }

    if (isFloatingWindowVisible(win)) {
        geometryInitialized = true;
        clampToViewport(true);
        scheduleGeometryNotification();
    }

    win.addEventListener('pointerdown', function () {
        if (isBlockingModalOpen() || !isFloatingWindowVisible(win)) return;
        floatingWindowManager.pendingActivation = null;
        floatingWindowManager.activationSequence++;
        bringFloatingWindowToFront(win, false);
        scheduleGeometryNotification();
    }, { capture: true, signal: eventSignal });

    win.addEventListener('focusin', function (e) {
        if (isBlockingModalOpen() || !isFloatingWindowVisible(win)) return;
        floatingWindowManager.interactionSequence++;
        if (e.target && typeof e.target.focus === 'function') {
            floatingWindowManager.lastFocusedElements.set(win, e.target);
        }
        if (getTopVisibleFloatingWindow() !== win) {
            floatingWindowManager.pendingActivation = null;
            floatingWindowManager.activationSequence++;
            bringFloatingWindowToFront(win, false);
        }
        scheduleGeometryNotification();
    }, { signal: eventSignal });

    let wasVisible = isFloatingWindowVisible(win);
    const visibilityObserver = new MutationObserver(function () {
        const isVisible = isFloatingWindowVisible(win);
        if (isVisible && !wasVisible) {
            floatingWindowManager.interactionSequence++;
            clampToViewport(!geometryInitialized);
            geometryInitialized = true;
            normalizeFloatingWindowStack();
            scheduleGeometryNotification();
        } else if (!isVisible && wasVisible) {
            floatingWindowManager.interactionSequence++;
            cancelFloatingWindowActivation(windowId);
            activateTopVisibleFloatingWindow();
        }
        wasVisible = isVisible;
    });
    visibilityObserver.observe(win, { attributes: true, attributeFilter: ['class'] });

    // Drag e resize con pointer events: mouse, touch e penna, con capture sul controllo che avvia il gesto
    titlebar.style.touchAction = 'none';
    titlebar.addEventListener('pointerdown', function (e) {
        if (!e.isPrimary || e.button !== 0 || activePointerId !== null || isBlockingModalOpen() || e.target.closest('button, input, select, textarea, a, [role="tab"], [role="button"], [contenteditable="true"], .rz-tabview-nav, .terminal-tab')) return;
        bringFloatingWindowToFront(win, true);
        isDragging = true;
        activePointerId = e.pointerId;
        dragOffsetX = e.clientX - win.offsetLeft;
        dragOffsetY = e.clientY - win.offsetTop;
        titlebar.setPointerCapture?.(e.pointerId);
        e.preventDefault();
    }, { signal: eventSignal });

    if (resizeHandle) {
        resizeHandle.style.touchAction = 'none';
        resizeHandle.addEventListener('pointerdown', function (e) {
            if (!e.isPrimary || e.button !== 0 || activePointerId !== null) return;
            isResizing = true;
            activePointerId = e.pointerId;
            resizeHandle.setPointerCapture?.(e.pointerId);
            e.preventDefault();
            e.stopPropagation();
        }, { signal: eventSignal });
    }

    document.addEventListener('pointermove', function (e) {
        if (e.pointerId !== activePointerId) return;
        if (isDragging) {
            floatingWindowManager.interactionSequence++;
            win.style.left = e.clientX - dragOffsetX + 'px';
            win.style.top = e.clientY - dragOffsetY + 'px';
            clampToViewport(false);
            scheduleGeometryNotification();
        }

        if (isResizing) {
            floatingWindowManager.interactionSequence++;
            win.style.width = e.clientX - win.offsetLeft + 'px';
            win.style.height = e.clientY - win.offsetTop + 'px';
            clampToViewport(false);
            scheduleGeometryNotification();

            // Recalculate terminal dimensions after resize
            window.dispatchEvent(new Event('resize'));
        }
    }, { signal: eventSignal });

    function endInteraction() {
        const changed = isDragging || isResizing;
        isDragging = false;
        isResizing = false;
        activePointerId = null;
        if (changed) {
            clampToViewport(false);
            notifyGeometry();
        }
    }
    document.addEventListener('pointerup', function (e) { if (e.pointerId === activePointerId) endInteraction(); }, { signal: eventSignal });
    document.addEventListener('pointercancel', function (e) { if (e.pointerId === activePointerId) endInteraction(); }, { signal: eventSignal });
    // Il mouseup sintetico di beginWorkspaceHandoff conclude il gesto prima del freeze
    document.addEventListener('mouseup', function (e) { if (!e.isTrusted) endInteraction(); }, { signal: eventSignal });

    window.addEventListener('resize', function () {
        if (!geometryInitialized) return;
        floatingWindowManager.interactionSequence++;
        clampToViewport(false);
        scheduleGeometryNotification();
    }, { signal: eventSignal });

    window.addEventListener('pagehide', notifyGeometry, { signal: eventSignal });
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'hidden') notifyGeometry();
    }, { signal: eventSignal });

    const registration = {
        element: win,
        sessionId,
        leaseGeneration,
        captureFocus: function () { win.dataset.focusTarget = getFocusTarget(); },
        flush: async function () {
            window.clearTimeout(geometryTimer);
            geometryTimer = 0;
            isDragging = false;
            isResizing = false;
            activePointerId = null;
            await geometryFlight;
            if (eventSignal.aborted || !isCircuitConnected()) return false;
            return await notifyGeometry(true);
        },
        dispose: function (cancelActivation = true) {
            const activation = floatingWindowManager.pendingActivation;
            window.clearTimeout(geometryTimer);
            eventController.abort();
            visibilityObserver.disconnect();
            initializedWindowDragElements.delete(win);
            floatingWindowManager.windows = floatingWindowManager.windows.filter(function (item) { return item !== win; });
            // Replacement releases only the old adapter, not the new opening's activation intent.
            if (cancelActivation) {
                if (activation?.windowId === windowId) cancelFloatingWindowActivation(windowId, activation.sequence);
                activateTopVisibleFloatingWindow();
            }
            if (windowDragRegistrations.get(windowId) === registration) windowDragRegistrations.delete(windowId);
            desktopPublications.windows.delete(registration);
        }
    };
    windowDragRegistrations.set(windowId, registration);
    desktopPublications.windows.add(registration);
}

/**
 * Removes every global listener and callback owned by one floating window.
 * @param {string} windowId - DOM id used during initialization.
 */
export function disposeWindowDrag(windowId) {
    const registration = windowDragRegistrations.get(windowId);
    if (registration) registration.dispose();
}

/** Conferma la geometria finale della registrazione ancora corrente, anche dopo un timer throttled. */
export async function flushWindowGeometry(windowId) {
    const registration = windowDragRegistrations.get(windowId);
    if (!registration) return !document.getElementById(windowId);
    const accepted = await registration.flush();
    return accepted && windowDragRegistrations.get(windowId) === registration;
}
