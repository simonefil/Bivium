// Bivium JS Interop
// Handles: resize drag, floating window stacking, keyboard capture, theme switching

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

/**
 * Starts browser-originated workspace presence heartbeats.
 * @param {object} dotNetReference - Current Commander callback owner.
 */
export function startWorkspacePresence(dotNetReference) {
    stopWorkspacePresence();
    if (!dotNetReference) return;

    const eventController = new AbortController();
    const eventSignal = eventController.signal;
    let disconnected = false;
    let pageActive = true;

    function heartbeat() {
        if (!pageActive) return;
        disconnected = false;
        dotNetReference.invokeMethodAsync('OnWorkspaceHeartbeat').catch(function () { });
    }

    function disconnect() {
        if (disconnected) return;
        pageActive = false;
        disconnected = true;
        dotNetReference.invokeMethodAsync('OnWorkspaceDisconnected').catch(function () { });
    }

    function reconnect() {
        pageActive = true;
        heartbeat();
    }

    const interval = window.setInterval(heartbeat, 20000);
    window.addEventListener('pagehide', disconnect, { signal: eventSignal });
    window.addEventListener('pageshow', reconnect, { signal: eventSignal });
    window.addEventListener('online', heartbeat, { signal: eventSignal });
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') heartbeat();
    }, { signal: eventSignal });

    workspacePresenceRegistration = {
        dispose: function () {
            window.clearInterval(interval);
            eventController.abort();
        }
    };
    heartbeat();
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
    let focusTarget = floatingWindowManager.lastFocusedElements.get(win);
    if (!focusTarget || !focusTarget.isConnected || !win.contains(focusTarget)) {
        const semanticTarget = win.dataset.focusTarget || '';
        if (semanticTarget === 'terminal-tab-rename') focusTarget = win.querySelector('.terminal-tab-rename');
        else if (semanticTarget === 'terminal-input') focusTarget = win.querySelector('.terminal-ime-input, .terminal-virtual-viewport');
        if (!focusTarget) focusTarget = win.querySelector(FLOATING_WINDOW_FOCUS_SELECTOR);
    }
    if (!focusTarget || !focusTarget.isConnected || !win.contains(focusTarget)) return;

    requestAnimationFrame(function () {
        if (!isFloatingWindowVisible(win) || getTopVisibleFloatingWindow() !== win || !focusTarget.isConnected) return;

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
    document.addEventListener('keydown', function (e) {
        const key = e.key;
        const ctrl = e.ctrlKey;
        const shift = e.shiftKey;
        const alt = e.altKey;

        // Check if focus is inside the terminal renderer
        const terminalWindow = document.getElementById('terminal-window');
        const activeEl = document.activeElement;
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

        // F12 toggles the terminal whenever the terminal itself is not focused
        if (key === 'F12') {
            e.preventDefault();
            e.stopPropagation();
            dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
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
        const hasDialog = document.querySelector('.context-menu-overlay');
        if (hasDialog) {
            return;
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
                if (key === 'Tab') {
                    e.preventDefault();
                }
                if (key === 'Escape') {
                    dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
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

        // Intercept Tab to prevent focus leaving
        if (key === 'Tab') {
            e.preventDefault();
        }

        // Send key event to .NET
        dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
    }, true);
}

/**
 * Initialize long-press touch handler for context menu
 * Fires a synthetic contextmenu event after 500ms hold
 */
export function initLongPress() {
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
    }, { passive: true });

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
    }, { passive: true });

    document.addEventListener('touchend', function () {
        if (timer !== null) {
            clearTimeout(timer);
            timer = null;
        }
    });
}

/**
 * Set the WebTUI theme attribute on the html element
 * @param {string} themeName - Theme name (dark, nord, catppuccin-mocha, etc.)
 */
export function setTheme(themeName) {
    document.documentElement.setAttribute('data-webtui-theme', themeName);
    localStorage.setItem('webtui-theme', themeName);
}

/**
 * Load the saved theme from localStorage
 * @returns {string} The saved theme name, or empty string
 */
export function loadSavedTheme() {
    const saved = localStorage.getItem('webtui-theme');
    if (saved) {
        document.documentElement.setAttribute('data-webtui-theme', saved);
    }
    return saved || '';
}

/**
 * Focus a DOM element by id
 * @param {string} elementId - DOM id
 */
export function focusElement(elementId) {
    const el = document.getElementById(elementId);
    if (el) el.focus();
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
    const row = activePanel?.querySelector('.file-list-table tbody tr.cursor');
    if (row) {
        row.scrollIntoView({ block: 'nearest' });
        return;
    }

    const scroller = activePanel?.querySelector('.panel-filelist');
    if (scroller && cursorIndex >= 0) {
        scroller.scrollTop = Math.max(0, cursorIndex * 20);
    }
}

const filePanelScrollTrackers = new Map();

/**
 * Tracks the first visible semantic file row for workspace persistence.
 * @param {string} panelId - Stable panel identifier.
 * @param {object} dotNetReference - FilePanel callback owner.
 */
export function registerFilePanelScroll(panelId, dotNetReference) {
    unregisterFilePanelScroll(panelId);
    const scroller = document.getElementById(panelId + '-filelist');
    if (!scroller) return;

    const tracker = { scroller, dotNetReference, timer: 0, restoring: false };
    tracker.listener = function () {
        if (tracker.restoring) return;
        window.clearTimeout(tracker.timer);
        tracker.timer = window.setTimeout(function () {
            const top = scroller.getBoundingClientRect().top;
            const rows = scroller.querySelectorAll('tr[data-entry-path]');
            for (const row of rows) {
                if (row.getBoundingClientRect().bottom > top + 1) {
                    dotNetReference.invokeMethodAsync('OnFileListScrollAnchorChanged', row.dataset.entryPath || '');
                    break;
                }
            }
        }, 120);
    };
    scroller.addEventListener('scroll', tracker.listener, { passive: true });
    filePanelScrollTrackers.set(panelId, tracker);
}

/**
 * Stops semantic scroll tracking for a file panel.
 * @param {string} panelId - Stable panel identifier.
 */
export function unregisterFilePanelScroll(panelId) {
    const tracker = filePanelScrollTrackers.get(panelId);
    if (!tracker) return;
    window.clearTimeout(tracker.timer);
    tracker.scroller.removeEventListener('scroll', tracker.listener);
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

    if (tracker) tracker.restoring = true;
    scroller.scrollTop = Math.max(0, anchorIndex * 20);
    window.requestAnimationFrame(function () {
        const rows = scroller.querySelectorAll('tr[data-entry-path]');
        for (const row of rows) {
            if (row.dataset.entryPath === anchorPath) {
                row.scrollIntoView({ block: 'start' });
                break;
            }
        }
        window.setTimeout(function () {
            if (tracker) tracker.restoring = false;
        }, 150);
    });
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
    const menuBar = document.querySelector('.menu-bar');
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
        dotNetReference.invokeMethodAsync('OnWindowGeometryChanged', update).catch(function () { });
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
