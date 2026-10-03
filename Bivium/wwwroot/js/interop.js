// Bivium JS Interop
// Handles: resize drag, floating window stacking, keyboard capture, theme switching

import { invokeCircuitMethod, isCircuitConnected, registerCircuitParticipant } from './connection.js';

const FLOATING_WINDOW_BASE_Z_INDEX = 2000;
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
const windowDragRegistrations = new Map();
let workspacePresenceRegistration = null;
let keyboardCaptureRegistration = null;
let longPressRegistration = null;
let popupLayerRegistration = null;

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

/** Layout-owned adapter for native Radzen portals; never a global popup CSS override. */
export function registerNativePopupLayers() {
    disposeNativePopupLayers();
    const layers = new Map();
    const headerLayers = new Map();
    let previousTop = getTopBlockingModal();

    function ownerFor(popup, info, popups, depth = 0) {
        if (depth > 4) return null;
        const ariaOwner = Array.from(document.querySelectorAll('[aria-controls], [aria-owns]')).find(function (control) {
            return [control.getAttribute('aria-controls'), control.getAttribute('aria-owns')]
                .some(value => value?.split(/\s+/).includes(popup.id));
        });
        const control = ariaOwner || info?.parent;
        if (!control?.isConnected) return null;
        const owner = control.closest('.rz-dialog-wrapper, .terminal-window, .editor-window, .renamer-window');
        if (owner) return owner;
        const parentPopup = control.closest('.rz-popup, .rz-overlaypanel');
        if (parentPopup && parentPopup !== popup) {
            return ownerFor(parentPopup, popups.find(item => item.id === parentPopup.id), popups, depth + 1);
        }
        return null;
    }

    function restore(popup) {
        const layer = layers.get(popup);
        if (!layer) return;
        if (popup.style.zIndex !== layer.originalZ) popup.style.zIndex = layer.originalZ;
        layers.delete(popup);
    }

    function update() {
        const radzen = globalThis.Radzen;
        if (!radzen) return;
        const top = getTopBlockingModal();
        const topChanged = top !== previousTop;
        previousTop = top;
        const popups = (radzen.popups || []).slice();
        const active = new Set();
        const modelessTop = Math.max(FLOATING_WINDOW_BASE_Z_INDEX,
            ...Array.from(document.querySelectorAll('.terminal-window.visible, .editor-window.visible, .renamer-window.visible'))
                .map(element => Number(getComputedStyle(element).zIndex) || FLOATING_WINDOW_BASE_Z_INDEX));

        for (const info of popups) {
            const popup = document.getElementById(info.id);
            if (!isVisibleElement(popup) || popup.classList.contains('rz-close')) continue;
            const owner = ownerFor(popup, info, popups);
            if ((top && owner !== top) || (owner && !isVisibleElement(owner))) {
                restore(popup);
                // Verified 11.4.2 API: preserve callback cleanup but not focus beneath the new modal.
                radzen.closePopup(info.id, info.instance, info.callback, null, true);
                continue;
            }
            active.add(popup);
            if (!layers.has(popup)) layers.set(popup, { originalZ: popup.style.zIndex });
            const ownerZ = owner ? Number(getComputedStyle(owner).zIndex) || modelessTop : Math.max(4000, modelessTop);
            const zIndex = String(ownerZ + 1);
            if (popup.style.zIndex !== zIndex) popup.style.zIndex = zIndex;
        }
        for (const popup of layers.keys()) {
            if (!active.has(popup)) restore(popup);
        }

        // Inline navigation menus inherit the header stacking context, not a body portal.
        for (const header of document.querySelectorAll('.commander-layout > .rz-header')) {
            const menuOpen = Array.from(header.querySelectorAll('.rz-navigation-menu')).some(isVisibleElement);
            if (top && topChanged) {
                for (const item of header.querySelectorAll('.rz-navigation-item-active')) radzen.closeMenuItem(item);
            }
            if (menuOpen && !top) {
                if (!headerLayers.has(header)) headerLayers.set(header, header.style.zIndex);
                const zIndex = String(Math.max(4000, modelessTop + 1));
                if (header.style.zIndex !== zIndex) header.style.zIndex = zIndex;
            } else if (headerLayers.has(header)) {
                header.style.zIndex = headerLayers.get(header);
                headerLayers.delete(header);
            }
        }
    }

    const layerSelector = '.rz-popup, .rz-overlaypanel, .rz-dialog-wrapper, dialog, .terminal-window, .editor-window, .renamer-window, .rz-header';
    const observer = new MutationObserver(function (records) {
        // Terminal renderer churn does not change popup ownership or native layer geometry.
        const relevant = records.some(function (record) {
            if (record.type === 'attributes') {
                return record.target.matches(layerSelector) || record.target.closest('.rz-header') ||
                    (['aria-controls', 'aria-owns'].includes(record.attributeName));
            }
            return [...record.addedNodes, ...record.removedNodes].some(function (node) {
                return node.nodeType === 1 && (node.matches(layerSelector) || node.querySelector(layerSelector));
            });
        });
        if (relevant) update();
    });
    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'open', 'aria-controls', 'aria-owns'] });
    update();
    popupLayerRegistration = {
        dispose: function () {
            observer.disconnect();
            for (const popup of layers.keys()) restore(popup);
            for (const [header, originalZ] of headerLayers) header.style.zIndex = originalZ;
            headerLayers.clear();
        }
    };
}

/** Releases only this layout's observer and restores native inline presentation. */
export function disposeNativePopupLayers() {
    popupLayerRegistration?.dispose();
    popupLayerRegistration = null;
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

    for (let i = 0; i < floatingWindowManager.windows.length; i++) {
        floatingWindowManager.windows[i].style.zIndex = String(FLOATING_WINDOW_BASE_Z_INDEX + i);
    }
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
    let focusTarget = floatingWindowManager.lastFocusedElements.get(win);
    if (!focusTarget || !focusTarget.isConnected || !win.contains(focusTarget)) {
        const semanticTarget = win.dataset.focusTarget || '';
        if (semanticTarget === 'terminal-tab-rename') focusTarget = win.querySelector('.terminal-tab-rename');
        else if (semanticTarget === 'terminal-input') focusTarget = win.querySelector('.terminal-ime-input, .terminal-virtual-viewport');
        if (!focusTarget) focusTarget = win.querySelector(FLOATING_WINDOW_FOCUS_SELECTOR);
    }
    if (!focusTarget || !focusTarget.isConnected || !win.contains(focusTarget)) return;

    requestAnimationFrame(function () {
        if (isBlockingModalOpen() || !isFloatingWindowVisible(win) || getTopVisibleFloatingWindow() !== win || !focusTarget.isConnected) return;

        try {
            focusTarget.focus({ preventScroll: true });
        } catch {
            focusTarget.focus();
        }
    });
}

function bringFloatingWindowToFront(win, restoreFocus) {
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
        } else {
            bringFloatingWindowToFront(win, true);
        }
    }
}

/**
 * Initialize a resizable splitter element
 * @param {string} splitterId - DOM id of the splitter element
 * @param {string} direction - "vertical" or "horizontal"
 * @param {string} cssVarName - CSS custom property to update on the parent grid
 */
export function initResizer(splitterId, direction, cssVarName) {
    const splitter = document.getElementById(splitterId);
    if (!splitter) return;

    const parent = splitter.parentElement;
    let isResizing = false;

    splitter.addEventListener('mousedown', function (e) {
        isResizing = true;
        e.preventDefault();
    });

    document.addEventListener('mousemove', function (e) {
        if (!isResizing) return;

        const parentRect = parent.getBoundingClientRect();

        if (direction === 'vertical') {
            const offsetX = e.clientX - parentRect.left;
            const percent = (offsetX / parentRect.width) * 100;
            const clamped = Math.max(20, Math.min(80, percent));
            parent.style.setProperty('--left-panel-width', clamped + '%');
            parent.style.setProperty('--right-panel-width', (100 - clamped) + '%');
        } else {
            const offsetY = e.clientY - parentRect.top;
            const percent = (offsetY / parentRect.height) * 100;
            const clamped = Math.max(15, Math.min(85, percent));
            parent.style.setProperty(cssVarName, clamped + '%');
        }
    });

    document.addEventListener('mouseup', function () {
        isResizing = false;
    });
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
    const nativeOwnerSelector = 'button, a, input, textarea, select, [contenteditable], [role="button"], [role="menu"], [role="menuitem"], [role="tree"], [role="treeitem"], .rz-tree, .radzen-panel-tree, .rz-menu, .rz-menu-popup, .rz-navigation-item, ' + semanticControlSelector;
    const dialogSelector = '[aria-modal="true"], .context-menu-overlay, .rz-dialog-wrapper, .rz-dialog-mask, .rz-context-menu, .rz-menu-popup';

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
        const fileSurface = activeEl?.closest?.('.radzen-panel-filelist, .panel-filelist, .radzen-file-grid-host');
        const workspaceBody = (!activeEl || activeEl === document.body) && document.querySelector('.radzen-file-panel.active, .file-panel.active');
        const input = activeEl && ['INPUT', 'TEXTAREA'].includes(activeEl.tagName);
        const inputInDialog = input && (activeEl.closest('.context-menu') || activeEl.closest('.renamer-window'));
        const available = !modal && !inTerminal && !inEditor && !hasDialog && !semanticOwner;
        const general = Boolean(available && !input);
        const control = Boolean(available && (!input || !inputInDialog));
        const navigation = Boolean(general && !nativeOwner && (fileSurface || workspaceBody));
        const panelSwitch = Boolean(general && !nativeOwner && activeEl?.closest?.('.radzen-panel-filelist, .panel-filelist'));
        const values = [general, control, navigation, panelSwitch, !modal, modal];
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

        // Navigation belongs only to the file surface, never to native controls or trees.
        if (['Enter', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown', 'Delete', ' ', 'Spacebar'].includes(key)) {
            const fileSurface = activeEl?.closest?.('.radzen-panel-filelist, .panel-filelist, .radzen-file-grid-host');
            const workspaceBody = (!activeEl || activeEl === document.body) && document.querySelector('.radzen-file-panel.active, .file-panel.active');
            if (nativeOwner || (!fileSurface && !workspaceBody)) return;
        }

        // If an input or textarea has focus, check context
        const tagName = activeEl ? activeEl.tagName : '';
        if (tagName === 'INPUT' || tagName === 'TEXTAREA') {
            // Check if the input is inside a dialog (context menu, renamer, etc.)
            const inDialog = activeEl.closest('.context-menu') || activeEl.closest('.renamer-window');

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

        // Intercept Ctrl+N to prevent browser new window
        if (key === 'n' && e.ctrlKey && !shift) {
            e.preventDefault();
        }

        // Intercept F4 to prevent browser address bar
        if (key === 'F4' && !ctrl && !shift && !alt) {
            e.preventDefault();
        }

        // Intercept Shift+F10 for context menu
        if (key === 'F10' && shift) {
            e.preventDefault();
        }

        // Commander switches panels only while focus is on the file-list surface
        if (key === 'Tab') {
            const fileSurface = activeEl?.closest?.('.radzen-panel-filelist, .panel-filelist');
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
        }
    };
}

export function disposeKeyboardCapture() {
    keyboardCaptureRegistration?.dispose();
    keyboardCaptureRegistration = null;
}

/**
 * Initialize long-press touch handler for context menu
 * Fires a synthetic contextmenu event after 500ms hold
 */
export function initLongPress() {
    disposeLongPress();
    const eventController = new AbortController();
    const eventSignal = eventController.signal;
    let timer = null;
    let startX = 0;
    let startY = 0;
    const HOLD_DURATION = 500;
    const MOVE_THRESHOLD = 10;

    document.addEventListener('touchstart', function (e) {
        const touch = e.touches[0];
        startX = touch.clientX;
        startY = touch.clientY;

        timer = setTimeout(function () {
            timer = null;

            // Find the closest table row or panel-filelist
            const target = document.elementFromPoint(startX, startY);
            if (!target) return;

            // Dispatch synthetic contextmenu event
            const contextEvent = new MouseEvent('contextmenu', {
                bubbles: true,
                cancelable: true,
                clientX: startX,
                clientY: startY
            });
            target.dispatchEvent(contextEvent);
        }, HOLD_DURATION);
    }, { passive: true, signal: eventSignal });

    document.addEventListener('touchmove', function (e) {
        if (timer === null) return;

        const touch = e.touches[0];
        const dx = Math.abs(touch.clientX - startX);
        const dy = Math.abs(touch.clientY - startY);

        // Cancel if finger moved too far (user is scrolling)
        if (dx > MOVE_THRESHOLD || dy > MOVE_THRESHOLD) {
            clearTimeout(timer);
            timer = null;
        }
    }, { passive: true, signal: eventSignal });

    document.addEventListener('touchend', function () {
        if (timer !== null) {
            clearTimeout(timer);
            timer = null;
        }
    }, { signal: eventSignal });
    longPressRegistration = {
        dispose: function () {
            if (timer !== null) clearTimeout(timer);
            eventController.abort();
        }
    };
}

export function disposeLongPress() {
    longPressRegistration?.dispose();
    longPressRegistration = null;
}

/**
 * Focus a DOM element by id
 * @param {string} elementId - DOM id
 */
export function focusElement(elementId) {
    const el = document.getElementById(elementId);
    if (el?.closest('.terminal-window, .editor-window, .renamer-window') && isBlockingModalOpen()) return;
    if (el) el.focus();
}

/** Focuses a modeless control only while no blocking modal owns focus. */
export function focusModelessElement(element) {
    if (!element?.isConnected || isBlockingModalOpen()) return;
    element.focus({ preventScroll: true });
}

/** Returns true only for a visible native blocking modal (including reconnect). */
export function isBlockingModalOpen() {
    return getTopBlockingModal() !== null;
}

/**
 * Selects the leading portion of a text input.
 * @param {HTMLInputElement} input - Input element.
 * @param {number} selectionEnd - Exclusive end of the selection.
 */
export function selectInputText(input, selectionEnd) {
    if (!input || typeof input.setSelectionRange !== 'function') return;

    const boundedEnd = Math.max(0, Math.min(Number(selectionEnd) || 0, input.value.length));
    input.setSelectionRange(0, boundedEnd);
}

/**
 * Send a PUT request with JSON body and return success status
 * @param {string} url - Request URL
 * @param {string} jsonBody - JSON string to send as body
 * @returns {Promise<boolean>} True if response is OK
 */
export async function putJson(url, jsonBody, attachmentId = '', leaseGeneration = 0) {
    try {
        const headers = createMutationHeaders(attachmentId, leaseGeneration);
        const response = await fetch(url, {
            method: 'PUT',
            headers: headers,
            body: jsonBody
        });
        return response.ok;
    } catch (err) {
        return false;
    }
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
 * Adjust context menu position to keep it within viewport
 */
export function adjustContextMenuPosition() {
    const menu = document.querySelector('.context-menu-overlay + .context-menu');
    if (!menu) return;

    const rect = menu.getBoundingClientRect();
    const viewportHeight = window.innerHeight;
    const viewportWidth = window.innerWidth;

    if (rect.bottom > viewportHeight) {
        menu.style.top = Math.max(0, viewportHeight - rect.height) + 'px';
    }
    if (rect.right > viewportWidth) {
        menu.style.left = Math.max(0, viewportWidth - rect.width) + 'px';
    }
}

/**
 * Scrolls the active panel's cursor row into view
 */
export function scrollCursorIntoView(cursorIndex = -1) {
    const activePanel = document.querySelector('.file-panel.active');
    const radzenPanel = document.querySelector('.radzen-file-panel.active');
    const scroller = radzenPanel?.querySelector('.rz-data-grid-data') || activePanel?.querySelector('.panel-filelist');
    if (!scroller) return;
    const focusedRow = radzenPanel?.querySelector('tr.bivium-focused') || activePanel?.querySelector('tr.cursor');
    const index = cursorIndex >= 0 ? cursorIndex : Number(focusedRow?.dataset.entryIndex ?? -1);
    scrollToFileListEntry(scroller, index, '', false);
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
    const virtualized = Boolean(scroller.closest('.radzen-file-grid-host'));
    const quietPeriod = 160;
    let stableSince = 0;
    let stableGeometry = null;
    let stableRows = '';
    let finished = false;
    let frame = 0;
    const inputs = ['wheel', 'touchstart', 'pointerdown', 'keydown'];
    function finish() {
        if (finished) return;
        finished = true;
        cancelAnimationFrame(frame);
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
            if (!virtualized) {
                finish();
                return;
            }

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
    frame = requestAnimationFrame(position);
}

const filePanelScrollTrackers = new Map();
const fileListColumnResizers = new Map();
const MIN_FILE_COLUMN_WIDTH = 48;

/**
 * Registers pointer-driven adjacent column resizing for one file table.
 * @param {string} tableId - Stable table DOM identifier.
 * @param {object} dotNetReference - FilePanel callback owner.
 */
export function registerFileListColumnResizer(tableId, dotNetReference) {
    unregisterFileListColumnResizer(tableId);
    const table = document.getElementById(tableId);
    if (!table || !dotNetReference) return;

    const columns = Array.from(table.querySelectorAll(':scope > colgroup > col'));
    const headers = Array.from(table.querySelectorAll(':scope > thead > tr > th'));
    const handles = Array.from(table.querySelectorAll('[data-column-resizer]'));
    if (columns.length !== 5 || headers.length !== 5 || handles.length !== 4) return;

    const eventController = new AbortController();
    const eventSignal = eventController.signal;
    let drag = null;

    function applyWidths(widths) {
        const total = widths.reduce(function (sum, width) { return sum + width; }, 0);
        if (!Number.isFinite(total) || total <= 0) return false;
        if (!widths.every(function (width) { return Number.isFinite(width) && width > 0; })) return false;

        table.classList.add('custom-columns');
        for (let i = 0; i < columns.length; i++) {
            columns[i].style.width = widths[i] / total * 100 + '%';
        }
        return true;
    }

    function finishDrag(event) {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const completed = drag;
        drag = null;
        if (!completed.changed) return;

        const total = completed.widths.reduce(function (sum, width) { return sum + width; }, 0);
        if (!Number.isFinite(total) || total <= 0) return;
        const ratios = completed.widths.map(function (width) { return width / total; });
        if (!ratios.every(function (ratio) { return Number.isFinite(ratio) && ratio > 0; })) return;
        invokeCircuitMethod(dotNetReference, 'OnFileListColumnRatiosChanged', ratios).catch(function () { });
    }

    for (const handle of handles) {
        handle.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
        }, { signal: eventSignal });
        handle.addEventListener('pointerdown', function (event) {
            if (event.button !== 0) return;
            const index = Number(handle.dataset.columnResizer);
            if (!Number.isInteger(index) || index < 0 || index >= headers.length - 1) return;

            const widths = headers.map(function (header) { return header.getBoundingClientRect().width; });
            const pairWidth = widths[index] + widths[index + 1];
            if (!widths.every(function (width) { return Number.isFinite(width) && width > 0; }) || pairWidth < MIN_FILE_COLUMN_WIDTH * 2) return;

            drag = {
                pointerId: event.pointerId,
                index: index,
                startX: event.clientX,
                startLeftWidth: widths[index],
                pairWidth: pairWidth,
                widths: widths,
                changed: false
            };
            handle.setPointerCapture?.(event.pointerId);
            event.preventDefault();
            event.stopPropagation();
        }, { signal: eventSignal });
    }

    document.addEventListener('pointermove', function (event) {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const leftWidth = Math.max(MIN_FILE_COLUMN_WIDTH, Math.min(drag.pairWidth - MIN_FILE_COLUMN_WIDTH, drag.startLeftWidth + event.clientX - drag.startX));
        const rightWidth = drag.pairWidth - leftWidth;
        if (Math.abs(leftWidth - drag.widths[drag.index]) < 0.01) return;

        drag.widths[drag.index] = leftWidth;
        drag.widths[drag.index + 1] = rightWidth;
        drag.changed = true;
        applyWidths(drag.widths);
        event.preventDefault();
    }, { signal: eventSignal });
    document.addEventListener('pointerup', finishDrag, { signal: eventSignal });
    document.addEventListener('pointercancel', finishDrag, { signal: eventSignal });

    const registration = {
        dispose: function () {
            drag = null;
            eventController.abort();
            if (fileListColumnResizers.get(tableId) === registration) fileListColumnResizers.delete(tableId);
        }
    };
    fileListColumnResizers.set(tableId, registration);
}

/**
 * Removes column-resize listeners owned by one file table.
 * @param {string} tableId - Stable table DOM identifier.
 */
export function unregisterFileListColumnResizer(tableId) {
    const registration = fileListColumnResizers.get(tableId);
    if (registration) registration.dispose();
}

/**
 * Tracks the first visible semantic file row for workspace persistence.
 * @param {string} panelId - Stable panel identifier.
 * @param {object} dotNetReference - FilePanel callback owner.
 */
export function registerFilePanelScroll(panelId, dotNetReference) {
    unregisterFilePanelScroll(panelId);
    const scroller = document.getElementById(panelId + '-filelist');
    if (!scroller) return;

    const tracker = { scroller, dotNetReference, timer: 0, restoring: false, pageSize: 0 };
    tracker.measurePageSize = function () {
        if (!isCircuitConnected()) return;
        const pageSize = computeFileListPageSize(scroller);
        if (pageSize < 1 || pageSize === tracker.pageSize) return;
        invokeCircuitMethod(dotNetReference, 'OnFileListPageSizeChanged', pageSize).then(function () {
            tracker.pageSize = pageSize;
        }).catch(function () { });
    };
    tracker.listener = function () {
        if (tracker.restoring) return;
        window.clearTimeout(tracker.timer);
        tracker.timer = window.setTimeout(function () {
            const top = scroller.getBoundingClientRect().top + (scroller.querySelector('thead')?.getBoundingClientRect().height || 0);
            const rows = scroller.querySelectorAll('tr[data-entry-path]');
            for (const row of rows) {
                if (row.getBoundingClientRect().bottom > top + 1) {
                    invokeCircuitMethod(dotNetReference, 'OnFileListScrollAnchorChanged', row.dataset.entryPath || '').catch(function () { });
                    break;
                }
            }
            tracker.measurePageSize();
        }, 120);
    };
    scroller.addEventListener('scroll', tracker.listener, { passive: true });

    // The splitter resizes the list without resizing the window, so the viewport is observed directly
    tracker.resizeObserver = new ResizeObserver(tracker.measurePageSize);
    tracker.resizeObserver.observe(scroller);

    const body = scroller.querySelector('tbody');
    if (body) tracker.resizeObserver.observe(body);

    filePanelScrollTrackers.set(panelId, tracker);
}

/**
 * Counts the whole file rows that fit in a file list viewport.
 * @param {HTMLElement} scroller - File list scroll container.
 * @returns {number} Number of rows a page jump should cover.
 */
function computeFileListPageSize(scroller) {
    if (!scroller) return 0;

    const header = scroller.querySelector('thead');
    const row = scroller.querySelector('tr[data-entry-path]');
    const headerHeight = header ? header.getBoundingClientRect().height : 0;

    // Before the first row exists the height used by scrollCursorIntoView is the best estimate
    const rowHeight = row && row.getBoundingClientRect().height >= 1 ? row.getBoundingClientRect().height : 20;

    return Math.max(1, Math.floor((scroller.clientHeight - headerHeight) / rowHeight));
}

/**
 * Stops semantic scroll tracking for a file panel.
 * @param {string} panelId - Stable panel identifier.
 */
export function unregisterFilePanelScroll(panelId) {
    const tracker = filePanelScrollTrackers.get(panelId);
    if (!tracker) return;
    fileListScrollRequests.get(tracker.scroller)?.();
    window.clearTimeout(tracker.timer);
    tracker.scroller.removeEventListener('scroll', tracker.listener);
    tracker.resizeObserver?.disconnect();
    filePanelScrollTrackers.delete(panelId);
}

/**
 * Restores a file list by semantic path, using its index only to materialize a virtual row.
 * @param {string} panelId - Stable panel identifier.
 * @param {string} anchorPath - Full path of the saved first visible row.
 * @param {number} anchorIndex - Current index of that path after sorting and validation.
 */
export function restoreFilePanelScrollAnchor(panelId, anchorPath, anchorIndex) {
    const tracker = filePanelScrollTrackers.get(panelId);
    const scroller = tracker?.scroller || document.getElementById(panelId + '-filelist');
    if (!scroller || anchorIndex < 0) return;

    fileListScrollRequests.get(scroller)?.();
    if (tracker) {
        window.clearTimeout(tracker.timer);
        tracker.restoring = true;
    }
    scrollToFileListEntry(scroller, anchorIndex, anchorPath, true, function () {
        if (tracker) tracker.restoring = false;
    });
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
 * Registra un resize immediato sui resizer Radzen senza attendere il roundtrip Blazor Server del mousedown.
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
        if (!drag) return;
        const completed = drag;
        drag = null;
        if (!completed.changed) return;
        invokeCircuitMethod(dotNetReference, 'OnRadzenColumnWidthsChanged', completed.ids, completed.widths).catch(function () { });
        event?.preventDefault();
    }
    handles.forEach(function (handle, handleIndex) {
        handle.addEventListener('mousedown', function (event) {
            if (event.button !== 0) return;
            const headers = Array.from(table.querySelectorAll('thead th')).filter(function (header) { return header.getClientRects().length > 0; });
            const columns = Array.from(table.querySelectorAll(':scope > colgroup > col'));
            if (headers.length !== columns.length || handleIndex >= headers.length) return;
            const ids = headers.map(function (header) {
                const semanticClass = Array.from(header.classList).find(value => value.startsWith('bivium-column-')) || '';
                return semanticClass.substring('bivium-column-'.length);
            });
            if (ids.some(id => !id)) return;
            drag = {
                startX: event.clientX,
                index: handleIndex,
                startWidth: headers[handleIndex].getBoundingClientRect().width,
                widths: headers.map(header => header.getBoundingClientRect().width),
                ids,
                columns,
                changed: false
            };
            event.preventDefault();
            event.stopImmediatePropagation();
        }, { capture: true, signal: controller.signal });
    });
    document.addEventListener('mousemove', function (event) {
        if (!drag) return;
        const index = drag.index;
        const width = Math.max(MIN_RADZEN_FILE_COLUMN_WIDTH, drag.startWidth + event.clientX - drag.startX);
        if (Math.abs(width - drag.widths[index]) < 0.01) return;
        drag.widths[index] = width;
        drag.columns[index].style.width = width + 'px';
        drag.changed = true;
        event.preventDefault();
    }, { signal: controller.signal });
    document.addEventListener('mouseup', finish, { signal: controller.signal });

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
 * Projects the Blazor-owned sort direction onto the generated Radzen column header.
 * @param {string} hostId - Stable grid wrapper identifier.
 */
export function projectRadzenAriaSort(hostId) {
    const host = document.getElementById(hostId);
    if (!host) return;
    for (const control of host.querySelectorAll('thead .radzen-file-sort[data-bivium-sort-direction]')) {
        const header = control.closest('th');
        const direction = control.getAttribute('data-bivium-sort-direction');
        if (header && ['none', 'ascending', 'descending'].includes(direction)) header.setAttribute('aria-sort', direction);
    }
}

/**
 * Rimuove l'adapter di resize colonne di una griglia Radzen.
 * @param {string} hostId - Identificatore del wrapper stabile della griglia.
 */
export function unregisterRadzenColumnResizer(hostId) {
    radzenColumnResizers.get(hostId)?.dispose();
}

/**
 * Installa la cattura Tab sull'input interno di RadzenAutoComplete.
 * @param {string} hostId - Identificatore del wrapper applicativo.
 * @param {object} dotNetReference - Proprietario del callback autocomplete.
 */
export function installRadzenPathAdapter(hostId, dotNetReference) {
    const host = document.getElementById(hostId);
    const input = host?.querySelector('input');
    if (!host || !input || !dotNetReference) return;
    const existing = radzenPathAdapters.get(hostId);
    if (existing?.input === input) return;
    existing?.dispose();

    const controller = new AbortController();
    let disposed = false;
    let inputGeneration = 0;
    let pending = Promise.resolve();
    input.addEventListener('input', function () {
        inputGeneration++;
    }, { signal: controller.signal });
    input.addEventListener('keydown', function (event) {
        if (event.key !== 'Tab') return;
        event.preventDefault();
        event.stopImmediatePropagation();
        const requestedGeneration = inputGeneration;
        pending = pending.then(function () {
            if (disposed) return null;
            return invokeCircuitMethod(dotNetReference, 'OnRadzenPathTab', input.value);
        }).then(function (value) {
            if (disposed || requestedGeneration !== inputGeneration || typeof value !== 'string') return;
            input.value = value;
        }).catch(function () { });
    }, { capture: true, signal: controller.signal });
    const registration = {
        input,
        dispose: function () {
            disposed = true;
            controller.abort();
            if (radzenPathAdapters.get(hostId) === registration) radzenPathAdapters.delete(hostId);
        }
    };
    radzenPathAdapters.set(hostId, registration);
    requestAnimationFrame(function () {
        if (input.isConnected) input.focus();
    });
}

/**
 * Rimuove l'adapter Tab di un editor percorso Radzen.
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
    const menuBar = document.querySelector('.bivium-radzen-menu-bar, .menu-bar');
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
export function initWindowDrag(windowId, titlebarId, resizeHandleId, dotNetReference = null) {
    const win = document.getElementById(windowId);
    const titlebar = document.getElementById(titlebarId);
    const resizeHandle = document.getElementById(resizeHandleId);
    if (!win || !titlebar) return;
    registerFloatingWindow(win);
    const previousRegistration = windowDragRegistrations.get(windowId);
    if (previousRegistration?.element === win) return;
    if (previousRegistration) previousRegistration.dispose();
    initializedWindowDragElements.add(win);
    const eventController = new AbortController();
    const eventSignal = eventController.signal;

    let isDragging = false;
    let isResizing = false;
    let dragOffsetX = 0;
    let dragOffsetY = 0;
    let geometryTimer = 0;
    let lastNotifiedGeometry = '';

    function getFocusTarget() {
        const active = document.activeElement;
        if (active && win.contains(active)) {
            if (active.classList.contains('terminal-virtual-viewport')) return 'terminal-input';
            if (active.classList.contains('terminal-tab-rename')) return 'terminal-tab-rename';
        }
        return win.dataset.focusTarget || 'terminal-input';
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

    function notifyGeometry() {
        if (!dotNetReference || !isFloatingWindowVisible(win)) return;
        const rect = win.getBoundingClientRect();
        if (![rect.left, rect.top, rect.width, rect.height].every(Number.isFinite) || rect.width <= 0 || rect.height <= 0) return;
        const update = {
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
        if (signature === lastNotifiedGeometry) return;
        lastNotifiedGeometry = signature;
        invokeCircuitMethod(dotNetReference, 'OnWindowGeometryChanged', update).catch(function () { });
    }

    function scheduleGeometryNotification() {
        window.clearTimeout(geometryTimer);
        geometryTimer = window.setTimeout(function () {
            geometryTimer = 0;
            notifyGeometry();
        }, 150);
    }

    if (clampToViewport(true)) scheduleGeometryNotification();

    win.addEventListener('pointerdown', function () {
        bringFloatingWindowToFront(win, false);
        scheduleGeometryNotification();
    }, { capture: true, signal: eventSignal });

    win.addEventListener('focusin', function (e) {
        if (e.target && typeof e.target.focus === 'function') {
            floatingWindowManager.lastFocusedElements.set(win, e.target);
        }
        bringFloatingWindowToFront(win, false);
        scheduleGeometryNotification();
    }, { signal: eventSignal });

    let wasVisible = isFloatingWindowVisible(win);
    const visibilityObserver = new MutationObserver(function () {
        const isVisible = isFloatingWindowVisible(win);
        if (isVisible && !wasVisible) {
            clampToViewport(false);
            bringFloatingWindowToFront(win, true);
            scheduleGeometryNotification();
        } else if (!isVisible && wasVisible) {
            activateTopVisibleFloatingWindow();
        }
        wasVisible = isVisible;
    });
    visibilityObserver.observe(win, { attributes: true, attributeFilter: ['class'] });

    // Drag via titlebar
    titlebar.addEventListener('mousedown', function (e) {
        if (e.button !== 0 || isBlockingModalOpen() || e.target.closest('button, input, select, textarea, a, [role="tab"], [role="button"], [contenteditable="true"], .rz-tabview-nav, .terminal-tab')) return;
        bringFloatingWindowToFront(win, true);
        isDragging = true;
        dragOffsetX = e.clientX - win.offsetLeft;
        dragOffsetY = e.clientY - win.offsetTop;
        e.preventDefault();
    }, { signal: eventSignal });

    // Resize via handle
    if (resizeHandle) {
        resizeHandle.addEventListener('mousedown', function (e) {
            isResizing = true;
            e.preventDefault();
            e.stopPropagation();
        }, { signal: eventSignal });
    }

    document.addEventListener('mousemove', function (e) {
        if (isDragging) {
            win.style.left = e.clientX - dragOffsetX + 'px';
            win.style.top = e.clientY - dragOffsetY + 'px';
            clampToViewport(false);
        }

        if (isResizing) {
            win.style.width = e.clientX - win.offsetLeft + 'px';
            win.style.height = e.clientY - win.offsetTop + 'px';
            clampToViewport(false);

            // Recalculate terminal dimensions after resize
            window.dispatchEvent(new Event('resize'));
        }
    }, { signal: eventSignal });

    document.addEventListener('mouseup', function () {
        const changed = isDragging || isResizing;
        isDragging = false;
        isResizing = false;
        if (changed) {
            clampToViewport(false);
            notifyGeometry();
        }
    }, { signal: eventSignal });

    window.addEventListener('resize', function () {
        clampToViewport(false);
        scheduleGeometryNotification();
    }, { signal: eventSignal });

    window.addEventListener('pagehide', notifyGeometry, { signal: eventSignal });
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'hidden') notifyGeometry();
    }, { signal: eventSignal });

    const registration = {
        element: win,
        dispose: function () {
            window.clearTimeout(geometryTimer);
            eventController.abort();
            visibilityObserver.disconnect();
            initializedWindowDragElements.delete(win);
            floatingWindowManager.windows = floatingWindowManager.windows.filter(function (item) { return item !== win; });
            if (windowDragRegistrations.get(windowId) === registration) windowDragRegistrations.delete(windowId);
        }
    };
    windowDragRegistrations.set(windowId, registration);
}

/**
 * Removes every global listener and callback owned by one floating window.
 * @param {string} windowId - DOM id used during initialization.
 */
export function disposeWindowDrag(windowId) {
    const registration = windowDragRegistrations.get(windowId);
    if (registration) registration.dispose();
}
