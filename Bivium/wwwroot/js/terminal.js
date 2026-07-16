// Bivium Terminal - xterm.js multi-session integration

const terminals = new Map();

let terminalConstructor = null;
let fitAddonConstructor = null;

async function loadXterm() {
    if (terminalConstructor && fitAddonConstructor) return;
    const terminalModule = await import('/lib/xterm/lib/xterm.mjs');
    const fitAddonModule = await import('/lib/xterm-addon-fit/lib/addon-fit.mjs');
    terminalConstructor = terminalModule.Terminal;
    fitAddonConstructor = fitAddonModule.FitAddon;
}

function getKey(sessionId) {
    return String(sessionId);
}

function getTerminalState(sessionId) {
    return terminals.get(getKey(sessionId));
}

function getDefaultSize(state) {
    if (state && state.terminal) return [state.terminal.cols || 120, state.terminal.rows || 30];
    return [120, 30];
}

function notifyResize(state) {
    if (!state || !state.dotNetRef || !state.terminal) return;
    if (state.terminal.cols === state.lastCols && state.terminal.rows === state.lastRows) return;
    state.lastCols = state.terminal.cols;
    state.lastRows = state.terminal.rows;
    state.dotNetRef.invokeMethodAsync('OnTerminalResize', state.numericSessionId, state.lastCols, state.lastRows).catch(function () { });
}

/**
 * Initialize an xterm.js terminal in the given container.
 * @param {number|string} sessionId - Terminal session id.
 * @param {string} containerId - DOM id of the container element.
 * @param {object} dotNetReference - .NET DotNetObjectReference for callbacks.
 */
export async function initTerminal(sessionId, containerId, dotNetReference) {
    disposeTerminal(sessionId);

    const terminalContainer = document.getElementById(containerId);
    if (!terminalContainer) return [120, 30];

    await loadXterm();

    const terminal = new terminalConstructor({
        cursorBlink: true,
        fontSize: 14,
        fontFamily: 'monospace',
        theme: {
            background: '#000000',
            foreground: '#ffffff',
            cursor: '#ffffff'
        },
        scrollback: 5000,
        termName: 'xterm-256color'
    });

    terminal.attachCustomKeyEventHandler(function (event) {
        if (event.type === 'keydown' && event.key === 'F12') {
            event.preventDefault();
            return false;
        }

        return true;
    });

    const fitAddon = new fitAddonConstructor();
    terminal.loadAddon(fitAddon);

    const state = {
        numericSessionId: Number(sessionId),
        terminal: terminal,
        fitAddon: fitAddon,
        dotNetRef: dotNetReference,
        terminalContainer: terminalContainer,
        resizeObserver: null,
        dataDisposable: null,
        resizeDisposable: null,
        resizeTimer: null,
        lastCols: 0,
        lastRows: 0
    };
    terminals.set(getKey(sessionId), state);

    terminal.open(terminalContainer);
    fitTerminal(sessionId);
    state.lastCols = terminal.cols;
    state.lastRows = terminal.rows;

    state.dataDisposable = terminal.onData(function (data) {
        if (state.dotNetRef) state.dotNetRef.invokeMethodAsync('OnTerminalInput', state.numericSessionId, data).catch(function () { });
    });

    state.resizeDisposable = terminal.onResize(function () {
        notifyResize(state);
    });

    state.resizeObserver = new ResizeObserver(function () {
        if (state.resizeTimer) clearTimeout(state.resizeTimer);
        state.resizeTimer = setTimeout(function () {
            state.resizeTimer = null;
            fitTerminal(sessionId);
        }, 25);
    });
    state.resizeObserver.observe(terminalContainer);

    requestAnimationFrame(function () {
        requestAnimationFrame(function () {
            fitTerminal(sessionId);
            const currentState = getTerminalState(sessionId);
            if (currentState && currentState.terminal === terminal && terminalContainer.classList.contains('active')) terminal.focus();
        });
    });

    terminal.focus();
    return [terminal.cols, terminal.rows];
}

/**
 * Fit terminal to its container.
 * @param {number|string} sessionId - Terminal session id.
 * @returns {[number, number]} Current terminal columns and rows.
 */
export function fitTerminal(sessionId) {
    const state = getTerminalState(sessionId);
    if (!state || !state.terminal || !state.fitAddon) return [120, 30];
    if (!state.terminalContainer || state.terminalContainer.offsetWidth <= 0 || state.terminalContainer.offsetHeight <= 0) return getDefaultSize(state);

    try {
        state.fitAddon.fit();
    } catch {
        return getDefaultSize(state);
    }

    notifyResize(state);
    return [state.terminal.cols, state.terminal.rows];
}

/**
 * Write data to a terminal display.
 * @param {number|string} sessionId - Terminal session id.
 * @param {string} data - Data to write.
 */
export function writeTerminal(sessionId, data) {
    const state = getTerminalState(sessionId);
    if (state && state.terminal) state.terminal.write(data);
}

/**
 * Clear a terminal screen.
 * @param {number|string} sessionId - Terminal session id.
 */
export function clearTerminal(sessionId) {
    const state = getTerminalState(sessionId);
    if (state && state.terminal) state.terminal.clear();
}

/**
 * Reset terminal parser, modes, screen, and scrollback.
 * @param {number|string} sessionId - Terminal session id.
 */
export function resetTerminal(sessionId) {
    const state = getTerminalState(sessionId);
    if (!state || !state.terminal) return;
    state.terminal.reset();
    state.terminal.clear();
}

/**
 * Focus a terminal.
 * @param {number|string} sessionId - Terminal session id.
 */
export function focusTerminal(sessionId) {
    const state = getTerminalState(sessionId);
    if (state && state.terminal) state.terminal.focus();
}

/**
 * Dispose one terminal.
 * @param {number|string} sessionId - Terminal session id.
 */
export function disposeTerminal(sessionId) {
    const key = getKey(sessionId);
    const state = terminals.get(key);
    if (!state) return;

    if (state.resizeTimer) {
        clearTimeout(state.resizeTimer);
        state.resizeTimer = null;
    }
    if (state.resizeObserver) {
        state.resizeObserver.disconnect();
        state.resizeObserver = null;
    }
    if (state.dataDisposable) {
        state.dataDisposable.dispose();
        state.dataDisposable = null;
    }
    if (state.resizeDisposable) {
        state.resizeDisposable.dispose();
        state.resizeDisposable = null;
    }
    if (state.terminal) {
        state.terminal.dispose();
        state.terminal = null;
    }

    state.fitAddon = null;
    state.dotNetRef = null;
    state.terminalContainer = null;
    terminals.delete(key);
}

/**
 * Dispose every terminal owned by this module instance.
 */
export function disposeAllTerminals() {
    for (const key of Array.from(terminals.keys())) {
        disposeTerminal(key);
    }
}
