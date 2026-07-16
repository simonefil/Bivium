// Bivium JS Interop
// Handles: resize drag, floating window stacking, keyboard capture, theme switching

const FLOATING_WINDOW_BASE_Z_INDEX = 2000;
const FLOATING_WINDOW_FOCUS_SELECTOR = '.xterm-helper-textarea, .monaco-editor textarea, .renamer-body input:not([disabled]), .renamer-body select:not([disabled]), .renamer-body button:not([disabled])';
const FLOATING_WINDOW_MANAGER_KEY = Symbol.for('bivium.floatingWindowManager');

let floatingWindowManager = globalThis[FLOATING_WINDOW_MANAGER_KEY];
if (!floatingWindowManager) {
    floatingWindowManager = {
        windows: [],
        initializedDragElements: new WeakSet(),
        lastFocusedElements: new WeakMap()
    };
    globalThis[FLOATING_WINDOW_MANAGER_KEY] = floatingWindowManager;
}

const initializedWindowDragElements = floatingWindowManager.initializedDragElements;

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
        focusTarget = win.querySelector(FLOATING_WINDOW_FOCUS_SELECTOR);
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
    normalizeFloatingWindowStack();

    if (restoreFocus) restoreFloatingWindowFocus(win);
}

function activateTopVisibleFloatingWindow() {
    normalizeFloatingWindowStack();
    const topWindow = getTopVisibleFloatingWindow();
    if (topWindow) restoreFloatingWindowFocus(topWindow);
}

function registerFloatingWindow(win) {
    if (!floatingWindowManager.windows.includes(win)) {
        floatingWindowManager.windows.push(win);
    }

    const activeElement = document.activeElement;
    if (activeElement && win.contains(activeElement)) {
        floatingWindowManager.lastFocusedElements.set(win, activeElement);
    }

    normalizeFloatingWindowStack();
    if (isFloatingWindowVisible(win)) bringFloatingWindowToFront(win, true);
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

        // Check if focus is inside the terminal - let xterm.js handle input
        const terminalWindow = document.getElementById('terminal-window');
        const activeEl = document.activeElement;
        const inTerminal = terminalWindow && terminalWindow.classList.contains('visible') && (terminalWindow.contains(activeEl) || (activeEl && activeEl.closest && activeEl.closest('.terminal-body')));

        // Check if focus is inside the Monaco editor - let Monaco handle input
        const editorWindow = document.getElementById('editor-window');
        const inEditor = editorWindow && editorWindow.classList.contains('visible') && (editorWindow.contains(activeEl) || (activeEl && activeEl.closest && activeEl.closest('#monaco-container')));

        // F12 always goes to .NET (toggle terminal)
        if (key === 'F12') {
            e.preventDefault();
            e.stopPropagation();
            dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
            return;
        }

        // If terminal has focus, don't intercept anything else
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
 * Send a PUT request with JSON body and return success status
 * @param {string} url - Request URL
 * @param {string} jsonBody - JSON string to send as body
 * @returns {Promise<boolean>} True if response is OK
 */
export async function putJson(url, jsonBody) {
    try {
        const response = await fetch(url, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
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
export async function postJsonResult(url, jsonBody) {
    return await sendJsonResult(url, 'POST', jsonBody || '{}');
}

/**
 * Send a PUT request and return parsed JSON payload
 * @param {string} url - Request URL
 * @param {string} jsonBody - JSON string to send as a body
 * @returns {Promise<object>} Response envelope
 */
export async function putJsonResult(url, jsonBody) {
    return await sendJsonResult(url, 'PUT', jsonBody || '{}');
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

async function sendJsonResult(url, method, jsonBody) {
    try {
        const response = await fetch(url, {
            method: method,
            headers: { 'Content-Type': 'application/json' },
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

/**
 * Initialize drag and resize for a floating window element
 * @param {string} windowId - DOM id of the window container
 * @param {string} titlebarId - DOM id of the draggable titlebar
 * @param {string} resizeHandleId - DOM id of the resize handle
 */
export function initWindowDrag(windowId, titlebarId, resizeHandleId) {
    const win = document.getElementById(windowId);
    const titlebar = document.getElementById(titlebarId);
    const resizeHandle = document.getElementById(resizeHandleId);
    if (!win || !titlebar) return;
    registerFloatingWindow(win);
    if (initializedWindowDragElements.has(win)) return;
    initializedWindowDragElements.add(win);

    let isDragging = false;
    let isResizing = false;
    let dragOffsetX = 0;
    let dragOffsetY = 0;

    win.addEventListener('pointerdown', function () {
        bringFloatingWindowToFront(win, false);
    }, true);

    win.addEventListener('focusin', function (e) {
        if (e.target && typeof e.target.focus === 'function') {
            floatingWindowManager.lastFocusedElements.set(win, e.target);
        }
        bringFloatingWindowToFront(win, false);
    });

    let wasVisible = isFloatingWindowVisible(win);
    const visibilityObserver = new MutationObserver(function () {
        const isVisible = isFloatingWindowVisible(win);
        if (isVisible && !wasVisible) {
            bringFloatingWindowToFront(win, true);
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
    });

    // Resize via handle
    if (resizeHandle) {
        resizeHandle.addEventListener('mousedown', function (e) {
            isResizing = true;
            e.preventDefault();
            e.stopPropagation();
        });
    }

    document.addEventListener('mousemove', function (e) {
        if (isDragging) {
            let newX = e.clientX - dragOffsetX;
            let newY = e.clientY - dragOffsetY;

            // Keep window within viewport
            newX = Math.max(0, Math.min(newX, window.innerWidth - 50));
            newY = Math.max(0, Math.min(newY, window.innerHeight - 50));

            win.style.left = newX + 'px';
            win.style.top = newY + 'px';
        }

        if (isResizing) {
            let newWidth = e.clientX - win.offsetLeft;
            let newHeight = e.clientY - win.offsetTop;

            // Minimum size
            newWidth = Math.max(300, newWidth);
            newHeight = Math.max(150, newHeight);

            win.style.width = newWidth + 'px';
            win.style.height = newHeight + 'px';

            // Trigger xterm fit after resize
            window.dispatchEvent(new Event('resize'));
        }
    });

    document.addEventListener('mouseup', function () {
        isDragging = false;
        isResizing = false;
    });
}
