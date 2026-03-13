// Bivium JS Interop
// Handles: resize drag, keyboard capture, theme switching

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

        // F12 always goes to .NET (toggle terminal)
        if (key === 'F12') {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
            return;
        }

        // If terminal has focus, don't intercept anything else
        if (inTerminal) {
            return;
        }

        // If an input or textarea has focus, don't intercept (path bar, dialogs)
        const tagName = activeEl ? activeEl.tagName : '';
        if (tagName === 'INPUT' || tagName === 'TEXTAREA') {
            // Prevent Tab from moving focus (used for autocomplete)
            if (key === 'Tab') {
                e.preventDefault();
            }
            // Only forward Escape to .NET
            if (key === 'Escape') {
                dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
            }
            return;
        }

        // Intercept F5 to prevent browser refresh
        if (key === 'F5') {
            e.preventDefault();
        }

        // Intercept Ctrl+P to prevent browser print
        if (key === 'p' && e.ctrlKey) {
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
 * Scrolls the active panel's cursor row into view
 */
export function scrollCursorIntoView() {
    const row = document.querySelector('.file-panel.active .file-list-table tbody tr.cursor');
    if (row) {
        row.scrollIntoView({ block: 'nearest' });
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

    let isDragging = false;
    let isResizing = false;
    let dragOffsetX = 0;
    let dragOffsetY = 0;

    // Drag via titlebar
    titlebar.addEventListener('mousedown', function (e) {
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
