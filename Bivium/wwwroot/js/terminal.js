// Bivium Terminal - xterm.js integration

let terminal = null;
let fitAddon = null;
let dotNetRef = null;

/**
 * Initialize the xterm.js terminal in the given container
 * @param {string} containerId - DOM id of the container element
 * @param {object} dotNetReference - .NET DotNetObjectReference for callbacks
 */
export async function initTerminal(containerId, dotNetReference) {
    dotNetRef = dotNetReference;

    const container = document.getElementById(containerId);
    if (!container) return;

    // Import xterm and fit addon
    const { Terminal } = await import('/lib/xterm/lib/xterm.mjs');
    const { FitAddon } = await import('/lib/xterm-addon-fit/lib/addon-fit.mjs');

    // Create terminal
    terminal = new Terminal({
        cursorBlink: true,
        fontSize: 14,
        fontFamily: 'monospace',
        theme: {
            background: '#000000',
            foreground: '#ffffff',
            cursor: '#ffffff'
        },
        scrollback: 5000,
        convertEol: true
    });

    // Create and load fit addon
    fitAddon = new FitAddon();
    terminal.loadAddon(fitAddon);

    // Open terminal in container
    terminal.open(container);
    fitAddon.fit();

    // Handle user input - send to .NET
    terminal.onData(function (data) {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnTerminalInput', data);
        }
    });

    // Handle resize
    terminal.onResize(function (size) {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnTerminalResize', size.cols, size.rows);
        }
    });

    // Watch for container resize
    const resizeObserver = new ResizeObserver(function () {
        if (fitAddon) {
            fitAddon.fit();
        }
    });
    resizeObserver.observe(container);

    // Focus terminal
    terminal.focus();
}

/**
 * Write data to the terminal display
 * @param {string} data - Data to write
 */
export function writeTerminal(data) {
    if (terminal) {
        terminal.write(data);
    }
}

/**
 * Clear the terminal screen
 */
export function clearTerminal() {
    if (terminal) {
        terminal.clear();
    }
}

/**
 * Focus the terminal
 */
export function focusTerminal() {
    if (terminal) {
        terminal.focus();
    }
}

/**
 * Dispose the terminal
 */
export function disposeTerminal() {
    if (terminal) {
        terminal.dispose();
        terminal = null;
        fitAddon = null;
        dotNetRef = null;
    }
}
