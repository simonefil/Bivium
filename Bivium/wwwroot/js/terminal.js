// Bivium terminal renderer - remote paged history with bounded DOM

import { invokeCircuitMethod, isCircuitConnected, registerCircuitParticipant } from './connection.js';
import { isTopVisibleFloatingWindow } from './interop.js';
import { createSurfacePublisher, disposeSurface } from './surface-adapters.js';

const terminals = new Map();
const publications = () => globalThis[Symbol.for('bivium.desktopPublications')];

// Gli anchor usano la timeline già posseduta dal runtime, non testo o indici del DOM virtualizzato.
function readTerminalView(state) {
    if (state.pendingView) return state.pendingView;
    const position = value => value ? { row: state.snapshot.screen?.alternateBuffer ? value.row : visualToTimelineRow(state, value.row), column: value.column } : null;
    const row = Math.floor(state.viewport.scrollTop / state.lineHeight);
    return { sessionId: state.sessionId, observedRevision: state.snapshot.revision,
        alternateBuffer: !!state.snapshot.screen?.alternateBuffer,
        topRow: state.snapshot.screen?.alternateBuffer ? row : visualToTimelineRow(state, row),
        rowFraction: Math.max(0, Math.min(1 - Number.EPSILON, state.viewport.scrollTop / state.lineHeight - row)),
        horizontalCells: state.viewport.scrollLeft / state.charWidth, following: state.following,
        promptRow: state.promptNavigationRow ?? -1,
        selectionAnchor: position(state.selectionAnchor), selectionFocus: position(state.selectionFocus) };
}

function applyTerminalView(state, view) {
    const alternate = !!state.snapshot.screen?.alternateBuffer;
    const sameBuffer = alternate === view.alternateBuffer;
    const minimum = alternate ? 0 : state.snapshot.historyStart;
    const maximum = alternate ? Math.max(0, state.snapshot.screen.lines.length - 1) : Math.max(minimum, state.snapshot.historyEnd + state.snapshot.screen.lines.length - 1);
    const clampRow = row => Math.max(minimum, Math.min(maximum, row));
    const visual = row => alternate ? clampRow(row) : timelineToVisualRow(state, clampRow(row));
    const position = value => value && sameBuffer ? { row: visual(value.row), column: Math.max(0, Math.min(Math.max(0, state.snapshot.cols - 1), value.column)) } : null;
    state.following = view.following;
    state.followTailPending = view.following;
    state.promptNavigationRow = sameBuffer && !alternate && view.promptRow >= 0 ? clampRow(view.promptRow) : null;
    state.selectionAnchor = position(view.selectionAnchor);
    state.selectionFocus = position(view.selectionFocus);
    scrollProgrammatically(state, view.following ? state.viewport.scrollHeight : (visual(sameBuffer ? view.topRow : minimum) + view.rowFraction) * state.lineHeight);
    state.userScroll = false;
    positionTerminalInput(state);
}

function installTerminalView(state, reference, generation, initial) {
    state.viewGeneration = generation;
    state.pendingView = initial.revision > 0 ? initial : null;
    if (state.pendingView) state.following = initial.following;
    state.viewAdapter = createSurfacePublisher(state.container, reference, 'OnTerminalViewChanged', generation, initial, () => readTerminalView(state));
    const ready = state.viewAdapter.registration.ready;
    state.viewAdapter.registration.ready = () => ready() && !!state.snapshot && !state.composing && !state.promptNavigationPending;
    const capture = state.viewAdapter.registration.capture;
    state.viewAdapter.registration.capture = () => { if (state.snapshot) capture(); };
}

export function flushTerminalViews() {
    return Array.from(terminals.values()).every(state => state.viewAdapter?.registration.ready());
}

/** Scarica la history tramite anchor same-origin: nessun popup e nessuna user activation richiesta. */
export function downloadTerminalHistory(url) {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = '';
    anchor.rel = 'noopener';
    anchor.hidden = true;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
}
let terminalVisibilityRegistration = null;
let terminalConnectionRegistration = null;
let terminalPageVisible = typeof document === 'undefined' || document.visibilityState !== 'hidden';
const PAGE_ROWS = 200;
const MAX_CACHED_PAGES = 12;
const OVERSCAN_ROWS = 12;
const TERMINAL_LINE_ATTRIBUTE = Object.freeze({
    normal: 0,
    doubleWidth: 1,
    doubleHeightTop: 2,
    doubleHeightBottom: 3
});
const TERMINAL_COLOR_MODE = Object.freeze({
    indexedOrDefault: 0,
    rgb: 1
});
const TERMINAL_PALETTE_SIZE = 256;
const TERMINAL_DEFAULT_FOREGROUND = '#ffffff';
const TERMINAL_DEFAULT_BACKGROUND = '#000000';
const TERMINAL_UNDERLINE_STYLE = Object.freeze({
    none: 0,
    single: 1,
    double: 2,
    curly: 3,
    dotted: 4,
    dashed: 5
});
const TERMINAL_CELL_ATTRIBUTE = Object.freeze({
    bold: 1,
    dim: 2,
    italic: 4,
    underline: 8,
    blink: 16,
    inverse: 32,
    invisible: 64,
    strikethrough: 128,
    overline: 256
});

function getKey(sessionId) {
    return String(sessionId);
}

function getState(sessionId) {
    return terminals.get(getKey(sessionId));
}

function cancelScheduledRender(state) {
    if (!state?.renderAnimationFrame) return;
    cancelAnimationFrame(state.renderAnimationFrame);
    state.renderAnimationFrame = 0;
}

function notifyTerminalPageVisibility(visible) {
    const notifiedReferences = new Set();
    for (const state of terminals.values()) {
        if (state.disposed || !state.dotNetRef || notifiedReferences.has(state.dotNetRef)) continue;
        notifiedReferences.add(state.dotNetRef);
        invokeCircuitMethod(state.dotNetRef, 'OnTerminalVisibilityChanged', visible).catch(function () { });
    }
}

function handleTerminalPageVisibilityChange() {
    const visible = document.visibilityState !== 'hidden';
    if (visible === terminalPageVisible) return;
    terminalPageVisible = visible;
    notifyTerminalPageVisibility(visible);
    for (const state of terminals.values()) {
        state.lifecycleGeneration++;
        state.resyncRequest = 0;
        state.pendingPages.clear();
        if (!visible) {
            state.followTailPending ||= state.following;
            cancelScheduledRender(state);
            stopSelectionAutoscroll(state);
            if (state.resizeTimer) {
                clearTimeout(state.resizeTimer);
                state.resizeTimer = null;
            }
            continue;
        }

        notifyResize(state);
        requestTerminalResync(state);
    }
}

function ensureTerminalVisibilityRegistration() {
    if (terminalVisibilityRegistration || typeof document === 'undefined') return;
    terminalPageVisible = document.visibilityState !== 'hidden';
    const eventController = new AbortController();
    terminalConnectionRegistration = registerCircuitParticipant({
        phase: 'terminal',
        suspend: function () {
            for (const state of terminals.values()) {
                state.lifecycleGeneration++;
                state.resyncRequest = 0;
                state.pendingPages.clear();
                state.pressedKeys.clear();
                state.followTailPending ||= state.following;
                state.lastCols = 0;
                state.lastRows = 0;
                cancelScheduledRender(state);
                stopSelectionAutoscroll(state);
                if (state.resizeTimer) clearTimeout(state.resizeTimer);
                state.resizeTimer = null;
            }
        },
        recover: async function (isCurrent) {
            const notifiedReferences = new Set();
            for (const state of terminals.values()) {
                if (!isCurrent()) return;
                if (state.disposed || !state.dotNetRef) continue;
                const reference = state.dotNetRef;
                if (!notifiedReferences.has(reference)) {
                    await reference.invokeMethodAsync('OnTerminalVisibilityChanged', terminalPageVisible);
                    notifiedReferences.add(reference);
                }
                if (!isCurrent() || state.disposed) continue;
                if (terminalPageVisible && isRendererVisible(state)) {
                    const size = calculateSize(state);
                    await reference.invokeMethodAsync('OnTerminalResize', state.sessionId, size[0], size[1]);
                    if (!isCurrent() || state.disposed) continue;
                    state.lastCols = size[0];
                    state.lastRows = size[1];
                }
                const attach = await reference.invokeMethodAsync('GetTerminalAttach', state.sessionId);
                if (!isCurrent() || state.disposed) continue;
                if (!attach?.session) throw new Error('Terminal attachment unavailable');
                applyTerminalAttach(state.sessionId, attach);
                await reference.invokeMethodAsync('OnTerminalFocus', state.sessionId,
                    terminalPageVisible && state.viewport.contains(document.activeElement));
            }
        }
    });
    document.addEventListener('visibilitychange', handleTerminalPageVisibilityChange, { signal: eventController.signal });
    terminalVisibilityRegistration = {
        dispose: function () {
            eventController.abort();
            terminalConnectionRegistration?.();
            terminalConnectionRegistration = null;
            terminalVisibilityRegistration = null;
        }
    };
}

function releaseTerminalVisibilityRegistration() {
    if (terminals.size > 0 || !terminalVisibilityRegistration) return;
    terminalVisibilityRegistration.dispose();
}

function isRendererVisible(state) {
    return !!state && !state.disposed && state.container?.clientWidth > 0 && state.container?.clientHeight > 0 &&
        state.viewport?.clientWidth > 0 && state.viewport?.clientHeight > 0;
}

function measureTerminalMetrics(state) {
    if (!isRendererVisible(state) || typeof getComputedStyle !== 'function') return;
    const characterProbe = document.createElement('span');
    characterProbe.textContent = '00000000000000000000000000000000000000000000000000';
    characterProbe.style.position = 'absolute';
    characterProbe.style.visibility = 'hidden';
    characterProbe.style.whiteSpace = 'pre';
    characterProbe.style.font = 'inherit';
    characterProbe.style.fontVariantLigatures = 'none';
    state.viewport.appendChild(characterProbe);
    const measuredCharacterWidth = characterProbe.getBoundingClientRect().width / characterProbe.textContent.length;
    characterProbe.remove();

    const viewportStyle = getComputedStyle(state.viewport);
    const measuredLineHeight = parseFloat(viewportStyle.lineHeight);
    const rowProbe = document.createElement('div');
    rowProbe.className = 'terminal-virtual-row';
    rowProbe.style.visibility = 'hidden';
    state.rowsLayer.appendChild(rowProbe);
    const rowStyle = getComputedStyle(rowProbe);
    const horizontalPadding = parseFloat(rowStyle.paddingLeft) + parseFloat(rowStyle.paddingRight);
    rowProbe.remove();

    if (Number.isFinite(measuredCharacterWidth) && measuredCharacterWidth > 0)
        state.charWidth = measuredCharacterWidth;
    if (Number.isFinite(measuredLineHeight) && measuredLineHeight > 0)
        state.lineHeight = measuredLineHeight;
    if (Number.isFinite(horizontalPadding) && horizontalPadding >= 0)
        state.horizontalPadding = horizontalPadding;
}

export function computeTerminalGridSize(width, height, characterWidth, lineHeight, horizontalPadding = 0) {
    const safeCharacterWidth = Math.max(1, characterWidth);
    const safeLineHeight = Math.max(1, lineHeight);
    const usableWidth = Math.max(1, width - Math.max(0, horizontalPadding));
    return [Math.max(20, Math.floor(usableWidth / safeCharacterWidth)), Math.max(5, Math.floor(Math.max(1, height) / safeLineHeight))];
}

function calculateSize(state) {
    if (!state || !state.container) return [120, 30];
    if (!isRendererVisible(state))
        return [state.lastCols || state.snapshot?.cols || 120, state.lastRows || state.snapshot?.rows || 30];
    measureTerminalMetrics(state);
    return computeTerminalGridSize(state.viewport.clientWidth, state.viewport.clientHeight, state.charWidth, state.lineHeight, state.horizontalPadding);
}

function notifyResize(state) {
    if (!isCircuitConnected() || !state || state.disposed || !terminalPageVisible || !state.dotNetRef || !isRendererVisible(state)) return;
    const size = calculateSize(state);
    if (size[0] === state.lastCols && size[1] === state.lastRows) return;
    const lifecycleGeneration = state.lifecycleGeneration;
    invokeCircuitMethod(state.dotNetRef, 'OnTerminalResize', state.sessionId, size[0], size[1]).then(function () {
        if (state.disposed || lifecycleGeneration !== state.lifecycleGeneration) return;
        state.lastCols = size[0];
        state.lastRows = size[1];
    }).catch(function () { });
}

function isAtLiveTail(state) {
    if (!state || !state.viewport) return true;
    return state.viewport.scrollHeight - state.viewport.scrollTop - state.viewport.clientHeight <= 1;
}

function disarmFollowTail(state) {
    if (!state) return;
    state.following = false;
    state.followTailPending = false;
    state.programmaticScrollTop = null;
}

function scrollProgrammatically(state, scrollTop) {
    state.viewport.scrollTop = scrollTop;
    state.programmaticScrollTop = state.viewport.scrollTop;
}

function getTotalRows(state) {
    if (!state.snapshot) return 0;
    const layout = getHistoryLayout(state);
    const screenRows = state.snapshot.screen?.lines?.length || 0;
    return layout.markerRows + layout.historyRows + screenRows;
}

function getHistoryLayout(state) {
    return computeHistoryLayout(state.snapshot);
}

export function computeHistoryLayout(snapshot) {
    if (!snapshot || snapshot.screen?.alternateBuffer) return { markerRows: 0, historyRows: 0 };
    return {
        markerRows: snapshot.historyTruncated ? 1 : 0,
        historyRows: Math.max(0, snapshot.historyEnd - snapshot.historyStart)
    };
}

export function computeVirtualRange(totalRows, scrollTop, viewportHeight, lineHeight) {
    const safeLineHeight = Math.max(1, lineHeight);
    const top = Math.max(0, Math.floor(scrollTop / safeLineHeight) - OVERSCAN_ROWS);
    const visibleCount = Math.ceil(viewportHeight / safeLineHeight) + OVERSCAN_ROWS * 2;
    return { start: top, end: Math.min(Math.max(0, totalRows), top + visibleCount) };
}

function visualToHistoryIndex(state, visualIndex) {
    return state.snapshot.historyStart + visualIndex - getHistoryLayout(state).markerRows;
}

function visualToTimelineRow(state, visualIndex) {
    const layout = getHistoryLayout(state);
    if (visualIndex < layout.markerRows) return state.snapshot.historyStart;
    if (visualIndex < layout.markerRows + layout.historyRows)
        return visualToHistoryIndex(state, visualIndex);
    return state.snapshot.historyEnd + visualIndex - layout.markerRows - layout.historyRows;
}

function timelineToVisualRow(state, timelineRow) {
    const layout = getHistoryLayout(state);
    if (timelineRow < state.snapshot.historyEnd)
        return layout.markerRows + timelineRow - state.snapshot.historyStart;
    return layout.markerRows + layout.historyRows + timelineRow - state.snapshot.historyEnd;
}

function navigatePrompt(state, previous) {
    if (!isCircuitConnected() || !state.snapshot || state.snapshot.screen?.alternateBuffer || !state.snapshot.screen?.shellIntegrationAvailable) return;
    const lifecycleGeneration = state.lifecycleGeneration;
    const currentVisualRow = Math.floor(state.viewport.scrollTop / Math.max(1, state.lineHeight));
    const fromRow = state.promptNavigationRow ?? (isAtLiveTail(state)
        ? state.snapshot.historyEnd + (state.snapshot.screen?.lines?.length || 0)
        : visualToTimelineRow(state, currentVisualRow));
    state.promptNavigationPending = (state.promptNavigationPending || 0) + 1;
    invokeCircuitMethod(state.dotNetRef, 'FindTerminalPrompt', state.sessionId, fromRow, previous).then(function (target) {
        if (state.disposed || lifecycleGeneration !== state.lifecycleGeneration || !Number.isFinite(target) || target < 0) return;
        state.promptNavigationRow = Math.max(state.snapshot.historyStart, Math.min(state.snapshot.historyEnd + Math.max(0, (state.snapshot.screen?.lines?.length || 0) - 1), target));
        disarmFollowTail(state);
        scrollProgrammatically(state, Math.max(0, timelineToVisualRow(state, state.promptNavigationRow) * state.lineHeight));
        scheduleRender(state);
    }).catch(function () { }).finally(function () {
        state.promptNavigationPending--;
        if (!state.disposed) scheduleRender(state);
    });
}

function cachePage(state, page) {
    if (!page || !Array.isArray(page.lines)) return;
    state.pageCache.delete(page.start);
    state.pageCache.set(page.start, page);
    while (state.pageCache.size > MAX_CACHED_PAGES) {
        let evictionKey = state.pageCache.keys().next().value;
        if (state.snapshot) {
            const currentVisual = Math.floor(state.viewport.scrollTop / state.lineHeight);
            const currentHistory = visualToHistoryIndex(state, currentVisual);
            let greatestDistance = -1;
            for (const [start, candidate] of state.pageCache) {
                const midpoint = start + candidate.lines.length / 2;
                const distance = Math.abs(midpoint - currentHistory);
                if (distance > greatestDistance) {
                    greatestDistance = distance;
                    evictionKey = start;
                }
            }
        }
        state.pageCache.delete(evictionKey);
    }
}

function getHistoryLine(state, index) {
    for (const page of state.pageCache.values()) {
        if (index >= page.start && index < page.start + page.lines.length) {
            return page.lines[index - page.start];
        }
    }
    return null;
}

function requestHistoryPage(state, index) {
    if (!isCircuitConnected() || !terminalPageVisible || state.disposed || !state.dotNetRef || index < state.snapshot.historyStart || index >= state.snapshot.historyEnd) return;
    const pageRows = state.snapshot.historyPageRows || PAGE_ROWS;
    const start = Math.max(state.snapshot.historyStart, Math.floor(index / pageRows) * pageRows);
    if (state.pendingPages.has(start)) return;
    const lifecycleGeneration = state.lifecycleGeneration;
    state.pendingPages.add(start);
    invokeCircuitMethod(state.dotNetRef, 'GetTerminalHistoryPage', state.sessionId, start, pageRows).then(function (page) {
        if (lifecycleGeneration === state.lifecycleGeneration) state.pendingPages.delete(start);
        if (state.disposed || lifecycleGeneration !== state.lifecycleGeneration || !page || !state.snapshot || page.revision > state.snapshot.revision) return;
        cachePage(state, page);
        scheduleRender(state);
    }).catch(function () {
        if (lifecycleGeneration === state.lifecycleGeneration) state.pendingPages.delete(start);
    });
}

function requestTerminalResync(state) {
    if (!isCircuitConnected() || !terminalPageVisible || state.disposed || !state.dotNetRef || state.resyncRequest) return;
    const requestId = ++state.nextResyncRequest;
    const lifecycleGeneration = state.lifecycleGeneration;
    state.resyncRequest = requestId;
    invokeCircuitMethod(state.dotNetRef, 'GetTerminalAttach', state.sessionId).then(function (attach) {
        if (state.disposed || state.resyncRequest !== requestId || lifecycleGeneration !== state.lifecycleGeneration) return;
        if (!attach?.session) {
            scheduleRender(state, state.followTailPending);
            return;
        }
        if (state.snapshot && attach.session.revision < state.snapshot.revision) {
            scheduleRender(state, state.followTailPending);
            return;
        }
        applyTerminalAttach(state.sessionId, attach);
    }).catch(function () {
        if (!state.disposed && lifecycleGeneration === state.lifecycleGeneration)
            scheduleRender(state, state.followTailPending);
    }).finally(function () {
        if (state.resyncRequest === requestId) state.resyncRequest = 0;
    });
}

function rgbColor(color) {
    const red = (color >> 16) & 255;
    const green = (color >> 8) & 255;
    const blue = color & 255;
    return `rgb(${red},${green},${blue})`;
}

export function resolveTerminalColor(color, mode, isBackground, screen = null) {
    if (mode === TERMINAL_COLOR_MODE.rgb) {
        return rgbColor(color);
    }
    if (color < TERMINAL_PALETTE_SIZE) {
        const paletteValue = screen?.palette?.[color];
        return Number.isInteger(paletteValue) ? rgbColor(paletteValue) : paletteColor(color);
    }
    const defaultValue = isBackground ? screen?.defaultBackground : screen?.defaultForeground;
    if (Number.isInteger(defaultValue)) return rgbColor(defaultValue);
    return isBackground ? TERMINAL_DEFAULT_BACKGROUND : TERMINAL_DEFAULT_FOREGROUND;
}

export function resolveTerminalCellColors(cell, screen = null) {
    const attributes = cell.attributes || 0;
    let foreground = resolveTerminalColor(cell.foreground || 0, cell.foregroundMode || 0, false, screen);
    let background = resolveTerminalColor(cell.background || 0, cell.backgroundMode || 0, true, screen);
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.inverse) !== 0) {
        const swap = foreground;
        foreground = background;
        background = swap;
    }
    return { foreground, background };
}

export function resolveTerminalCellDecoration(cell, screen = null) {
    const attributes = cell.attributes || 0;
    const lines = [];
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.underline) !== 0) lines.push('underline');
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.strikethrough) !== 0) lines.push('line-through');
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.overline) !== 0) lines.push('overline');
    const styles = ['solid', 'solid', 'double', 'wavy', 'dotted', 'dashed'];
    return {
        line: lines.join(' '),
        style: styles[cell.underlineStyle || 0] || 'solid',
        color: cell.hasUnderlineColor ? resolveTerminalColor(cell.underlineColor || 0, cell.underlineColorMode || 0, false, screen) : ''
    };
}

function paletteColor(index) {
    const base = [
        '#000000', '#cd0000', '#00cd00', '#cdcd00',
        '#0000ee', '#cd00cd', '#00cdcd', '#e5e5e5',
        '#7f7f7f', '#ff0000', '#00ff00', '#ffff00',
        '#5c5cff', '#ff00ff', '#00ffff', '#ffffff'
    ];
    if (index >= 0 && index < base.length) return base[index];
    if (index >= 16 && index <= 231) {
        const value = index - 16;
        const red = Math.floor(value / 36);
        const green = Math.floor((value % 36) / 6);
        const blue = value % 6;
        const level = function (component) { return component === 0 ? 0 : 55 + component * 40; };
        return `rgb(${level(red)},${level(green)},${level(blue)})`;
    }
    if (index >= 232 && index <= 255) {
        const level = 8 + (index - 232) * 10;
        return `rgb(${level},${level},${level})`;
    }
    return '#ffffff';
}

function createCellNode(cell, cursor, selected, screen) {
    const hyperlink = normalizeTerminalHyperlink(cell.hyperlink, window.location.href);
    const span = document.createElement(hyperlink ? 'a' : 'span');
    span.className = 'terminal-cell';
    if (hyperlink) {
        span.classList.add('terminal-hyperlink');
        span.href = hyperlink;
        span.target = '_blank';
        span.rel = 'noopener noreferrer';
    }
    span.textContent = cell.content || '';
    span.style.width = Math.max(1, cell.width || 1) + 'ch';
    if (cell.width === 0) span.classList.add('continuation');
    if (cursor) span.classList.add('cursor');
    if (selected) span.classList.add('selected');
    const attributes = cell.attributes || 0;
    const colors = resolveTerminalCellColors(cell, screen);
    // La selezione usa i colori --terminal-selection-* del CSS: nessun inline da sovrascrivere con !important
    if (!selected) {
        span.style.color = colors.foreground;
        span.style.backgroundColor = colors.background;
    }
    if (cursor && !selected) {
        span.style.color = resolveTerminalColor(257, TERMINAL_COLOR_MODE.indexedOrDefault, true, screen);
        span.style.backgroundColor = Number.isInteger(screen?.cursorColor) ? rgbColor(screen.cursorColor) : TERMINAL_DEFAULT_FOREGROUND;
    }
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.bold) !== 0) span.style.fontWeight = 'bold';
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.dim) !== 0) span.style.opacity = '0.65';
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.italic) !== 0) span.style.fontStyle = 'italic';
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.blink) !== 0) span.classList.add('blink');
    const decoration = resolveTerminalCellDecoration(cell, screen);
    if (decoration.line) span.style.textDecorationLine = decoration.line;
    if (cell.underlineStyle && cell.underlineStyle !== TERMINAL_UNDERLINE_STYLE.none) {
        span.style.textDecorationStyle = decoration.style;
        if (decoration.color) span.style.textDecorationColor = decoration.color;
    }
    if ((attributes & TERMINAL_CELL_ATTRIBUTE.invisible) !== 0) span.style.visibility = 'hidden';
    return span;
}

export function normalizeTerminalHyperlink(value, baseUrl = 'http://localhost/') {
    if (!value) return '';
    try {
        const url = new URL(value, baseUrl);
        return ['http:', 'https:', 'mailto:'].includes(url.protocol) ? url.href : '';
    } catch (error) {
        return '';
    }
}

function createRowNode(state, visualIndex, line) {
    const row = document.createElement('div');
    row.className = 'terminal-virtual-row';
    row.dataset.visualIndex = String(visualIndex);
    row.style.top = (visualIndex * state.lineHeight) + 'px';
    row.style.height = state.lineHeight + 'px';
    if (!line) {
        row.classList.add('loading');
        row.textContent = ' ';
        return row;
    }
    if (line.marker) {
        row.classList.add('terminal-truncation-marker');
        row.textContent = '[Earlier terminal output truncated]';
        return row;
    }
    const lineAttribute = line.lineAttribute || TERMINAL_LINE_ATTRIBUTE.normal;
    if (lineAttribute === TERMINAL_LINE_ATTRIBUTE.doubleWidth) row.classList.add('terminal-line-double-width');
    else if (lineAttribute === TERMINAL_LINE_ATTRIBUTE.doubleHeightTop) row.classList.add('terminal-line-double-height-top');
    else if (lineAttribute === TERMINAL_LINE_ATTRIBUTE.doubleHeightBottom) row.classList.add('terminal-line-double-height-bottom');
    const cells = line.cells || [];
    const screenIndex = getScreenIndex(state, visualIndex);
    const hasCursor = screenIndex >= 0 && state.snapshot.screen?.cursorVisible && state.snapshot.screen.cursorY === screenIndex;
    const cellCount = hasCursor ? Math.max(cells.length, state.snapshot.screen.cursorX + 1) : cells.length;
    for (let i = 0; i < cellCount; i++) {
        const cell = cells[i] || { content: ' ', width: 1, foreground: 0, background: 0, attributes: 0 };
        row.appendChild(createCellNode(cell, hasCursor && i === state.snapshot.screen.cursorX, isCellSelected(state, visualIndex, i), state.snapshot.screen));
    }
    if (cellCount === 0) row.appendChild(document.createTextNode(' '));
    return row;
}

function getScreenIndex(state, visualIndex) {
    if (!state.snapshot) return -1;
    const layout = getHistoryLayout(state);
    const screenIndex = visualIndex - layout.markerRows - layout.historyRows;
    const screenRows = state.snapshot.screen?.lines?.length || 0;
    return screenIndex >= 0 && screenIndex < screenRows ? screenIndex : -1;
}

function comparePositions(left, right) {
    return left.row === right.row ? left.column - right.column : left.row - right.row;
}

function getOrderedSelection(state) {
    if (state.selectionAnchor === null || state.selectionFocus === null) return false;
    return comparePositions(state.selectionAnchor, state.selectionFocus) <= 0
        ? [state.selectionAnchor, state.selectionFocus]
        : [state.selectionFocus, state.selectionAnchor];
}

function isCellSelected(state, visualIndex, column) {
    const selection = getOrderedSelection(state);
    if (!selection) return false;
    const position = { row: visualIndex, column };
    return comparePositions(position, selection[0]) >= 0 && comparePositions(position, selection[1]) <= 0;
}

function getLineForVisualIndex(state, visualIndex) {
    const layout = getHistoryLayout(state);
    if (layout.markerRows && visualIndex === 0) return { marker: true };
    if (visualIndex < layout.markerRows + layout.historyRows) {
        const historyIndex = visualToHistoryIndex(state, visualIndex);
        const line = getHistoryLine(state, historyIndex);
        if (!line) requestHistoryPage(state, historyIndex);
        return line;
    }
    const screenIndex = visualIndex - layout.markerRows - layout.historyRows;
    return state.snapshot.screen?.lines?.[screenIndex] || null;
}

function getScreenVisualSignature(screen) {
    return JSON.stringify([
        screen?.palette || null,
        screen?.defaultForeground ?? null,
        screen?.defaultBackground ?? null,
        screen?.cursorColor ?? null
    ]);
}

function getRowVisualSignature(state, visualIndex, line, screenSignature) {
    if (!line) return `${state.lineHeight}|${screenSignature}|loading`;
    if (line.marker) return `${state.lineHeight}|${screenSignature}|marker`;
    const cells = line.cells || [];
    const screenIndex = getScreenIndex(state, visualIndex);
    const hasCursor = screenIndex >= 0 && state.snapshot.screen?.cursorVisible && state.snapshot.screen.cursorY === screenIndex;
    const cellCount = hasCursor ? Math.max(cells.length, state.snapshot.screen.cursorX + 1) : cells.length;
    const visualCells = [];
    for (let index = 0; index < cellCount; index++) {
        const cell = cells[index] || { content: ' ', width: 1, foreground: 0, background: 0, attributes: 0 };
        visualCells.push([
            cell.content || '', cell.width ?? 1,
            cell.foreground || 0, cell.foregroundMode || 0,
            cell.background || 0, cell.backgroundMode || 0,
            cell.attributes || 0,
            cell.underlineStyle || 0,
            !!cell.hasUnderlineColor,
            cell.underlineColor || 0, cell.underlineColorMode || 0,
            normalizeTerminalHyperlink(cell.hyperlink, window.location.href),
            hasCursor && index === state.snapshot.screen.cursorX,
            isCellSelected(state, visualIndex, index)
        ]);
    }
    return JSON.stringify([
        state.lineHeight,
        screenSignature,
        line.wrapped === true,
        line.lineAttribute || TERMINAL_LINE_ATTRIBUTE.normal,
        visualCells
    ]);
}

function reconcileVisibleRows(state, entries) {
    const desiredNodes = entries.map(entry => entry.node);
    const desiredSet = new Set(desiredNodes);
    for (let index = 0; index < desiredNodes.length; index++) {
        const desired = desiredNodes[index];
        const current = state.rowsLayer.children[index];
        if (current === desired) continue;
        if (current && !desiredSet.has(current)) state.rowsLayer.replaceChild(desired, current);
        else state.rowsLayer.insertBefore(desired, current || null);
    }
    while (state.rowsLayer.children.length > desiredNodes.length)
        state.rowsLayer.removeChild(state.rowsLayer.lastChild);
}

function renderVisibleRows(state, followTail) {
    if (!terminalPageVisible || state.disposed || !state.snapshot || !state.viewport || !state.rowsLayer) return;
    const totalRows = getTotalRows(state);
    state.spacer.style.height = Math.max(state.viewport.clientHeight, totalRows * state.lineHeight) + 'px';
    let restoredView = null;
    if (state.pendingView && isRendererVisible(state)) {
        const saved = state.pendingView;
        restoredView = saved;
        state.pendingView = null;
        measureTerminalMetrics(state);
        state.spacer.style.height = Math.max(state.viewport.clientHeight, totalRows * state.lineHeight) + 'px';
        applyTerminalView(state, saved);
        followTail = saved.following;
    }
    if (followTail) {
        scrollProgrammatically(state, state.viewport.scrollHeight);
    }
    const range = computeVirtualRange(totalRows, state.viewport.scrollTop, state.viewport.clientHeight, state.lineHeight);
    const top = range.start;
    const bottom = range.end;
    const screenSignature = getScreenVisualSignature(state.snapshot.screen);
    const entries = [];
    const nextRowCache = new Map();
    for (let index = top; index < bottom; index++) {
        const line = getLineForVisualIndex(state, index);
        const signature = getRowVisualSignature(state, index, line, screenSignature);
        const cached = state.renderedRows.get(index);
        const entry = cached?.signature === signature
            ? cached
            : { signature, node: createRowNode(state, index, line) };
        entries.push(entry);
        nextRowCache.set(index, entry);
    }
    reconcileVisibleRows(state, entries);
    if (restoredView) {
        state.viewport.scrollLeft = Math.max(0, Math.min(Math.max(0, state.viewport.scrollWidth - state.viewport.clientWidth), restoredView.horizontalCells * state.charWidth));
        // Lo scroll del layout non riattiva follow-tail né cancella l'anchor del prompt.
        state.programmaticScrollTop = state.viewport.scrollTop;
    }
    state.renderedRows = nextRowCache;
    state.renderedStart = top;
    state.renderedEnd = bottom;
    if (state.viewAdapter && !state.selecting) {
        state.viewAdapter.registration.restoring = false;
        state.viewAdapter.registration.capture();
    }
}

function scheduleRender(state, followTail = false) {
    if (!state || state.disposed) return;
    if (followTail) state.following = true;
    state.followTailPending ||= followTail && state.following;
    if (!terminalPageVisible || state.renderAnimationFrame) return;
    state.renderAnimationFrame = requestAnimationFrame(function () {
        state.renderAnimationFrame = 0;
        if (state.disposed || !terminalPageVisible) return;
        const renderAtTail = state.followTailPending && state.following;
        state.followTailPending = false;
        renderVisibleRows(state, renderAtTail);
    });
}

function sendInput(state, data) {
    if (!data || !state.dotNetRef) return;
    // Typed text returns to the live tail like a real terminal
    state.promptNavigationRow = null;
    scheduleRender(state, true);
    invokeCircuitMethod(state.dotNetRef, 'OnTerminalInput', state.sessionId, data).catch(function () { });
}

function positionTerminalInput(state) {
    if (!state?.input || !state.viewport) return;
    state.input.style.top = Math.max(0, state.viewport.scrollTop) + 'px';
}

function getPositionFromPointer(state, event) {
    const bounds = state.viewport.getBoundingClientRect();
    const offset = event.clientY - bounds.top + state.viewport.scrollTop;
    const row = Math.max(0, Math.min(getTotalRows(state) - 1, Math.floor(offset / state.lineHeight)));
    const column = Math.max(0, Math.floor((event.clientX - bounds.left + state.viewport.scrollLeft) / state.charWidth));
    return { row, column };
}

function sendMouse(state, event, eventType, button) {
    if (!state.snapshot?.screen?.mouseTracking || !state.dotNetRef) return false;
    const visualIndex = getPositionFromPointer(state, event).row;
    const screenIndex = getScreenIndex(state, visualIndex);
    if (screenIndex < 0) return false;
    const bounds = state.viewport.getBoundingClientRect();
    const x = Math.max(0, Math.floor((event.clientX - bounds.left + state.viewport.scrollLeft) / state.charWidth));
    invokeCircuitMethod(state.dotNetRef, 'OnTerminalMouse', state.sessionId, button, x, screenIndex, eventType, event.shiftKey, event.ctrlKey, event.altKey).catch(function () { });
    return true;
}

function lineTextRange(line, startColumn, endColumn) {
    const cells = line?.cells || [];
    const result = [];
    const start = Math.max(0, startColumn || 0);
    const end = Math.min(cells.length, endColumn === undefined ? cells.length : endColumn);
    for (let index = start; index < end; index++) {
        if ((cells[index].width || 0) > 0) result.push(cells[index].content || '');
    }
    return result.join('');
}

function appendPlainLine(result, line, hasPrevious, startColumn = 0, endColumn = undefined) {
    if (hasPrevious && !line.wrapped) result.push('\n');
    result.push(lineTextRange(line, startColumn, endColumn));
}

async function getHistoryLineAsync(state, historyIndex) {
    if (!isCircuitConnected() || state.disposed) throw new Error('Terminal disconnected');
    const lifecycleGeneration = state.lifecycleGeneration;
    let line = getHistoryLine(state, historyIndex);
    if (line) return line;
    const pageRows = state.snapshot.historyPageRows || PAGE_ROWS;
    const start = Math.max(state.snapshot.historyStart, Math.floor(historyIndex / pageRows) * pageRows);
    const page = await invokeCircuitMethod(state.dotNetRef, 'GetTerminalHistoryPage', state.sessionId, start, pageRows);
    if (state.disposed || lifecycleGeneration !== state.lifecycleGeneration) throw new Error('Terminal attachment changed');
    cachePage(state, page);
    return getHistoryLine(state, historyIndex);
}

async function copyRemoteSelection(state) {
    if (!state.snapshot || state.selectionAnchor === null || state.selectionFocus === null) return;
    const selection = getOrderedSelection(state);
    if (!selection) return;
    const first = selection[0];
    const last = selection[1];
    const result = [];
    let hasPrevious = false;
    for (let visualIndex = first.row; visualIndex <= last.row; visualIndex++) {
        let line = null;
        const layout = getHistoryLayout(state);
        if (visualIndex >= layout.markerRows && visualIndex < layout.markerRows + layout.historyRows)
            line = await getHistoryLineAsync(state, visualToHistoryIndex(state, visualIndex));
        else
            line = getLineForVisualIndex(state, visualIndex);
        if (!line || line.marker) continue;
        const startColumn = visualIndex === first.row ? first.column : 0;
        const endColumn = visualIndex === last.row ? last.column + 1 : undefined;
        appendPlainLine(result, line, hasPrevious, startColumn, endColumn);
        hasPrevious = true;
    }

    if (result.length > 0) await navigator.clipboard.writeText(result.join(''));
}

function stopSelectionAutoscroll(state) {
    if (state.selectionAnimationFrame) cancelAnimationFrame(state.selectionAnimationFrame);
    state.selectionAnimationFrame = 0;
    state.selectionPointerY = null;
}

export function getTerminalClipboardAction(key, control, shift, meta, alt) {
    if (alt || !(meta || (control && shift))) return '';
    const normalizedKey = (key || '').toLowerCase();
    if (normalizedKey === 'c') return 'copy';
    if (normalizedKey === 'v') return 'paste';
    return '';
}

function runSelectionAutoscroll(state) {
    if (!state.selecting || state.selectionPointerY === null) {
        stopSelectionAutoscroll(state);
        return;
    }
    const bounds = state.viewport.getBoundingClientRect();
    const edge = Math.min(48, bounds.height / 4);
    let delta = 0;
    if (state.selectionPointerY < bounds.top + edge)
        delta = -Math.max(2, (bounds.top + edge - state.selectionPointerY) / 3);
    else if (state.selectionPointerY > bounds.bottom - edge)
        delta = Math.max(2, (state.selectionPointerY - (bounds.bottom - edge)) / 3);
    if (delta !== 0) {
        state.viewport.scrollTop += delta;
        state.selectionFocus = getPositionFromPointer(state, { clientX: state.selectionPointerX, clientY: state.selectionPointerY });
        scheduleRender(state);
    }
    state.selectionAnimationFrame = requestAnimationFrame(function () { runSelectionAutoscroll(state); });
}

function attachInputHandlers(state) {
    state.viewport.addEventListener('keydown', function (event) {
        positionTerminalInput(state);
        if (state.composing || event.isComposing || event.key === 'Process' || event.keyCode === 229) return;
        const promptNavigation = event.ctrlKey && !event.shiftKey && !event.altKey && !event.metaKey
            && (event.key === 'ArrowUp' || event.key === 'ArrowDown')
            && state.snapshot?.screen?.shellIntegrationAvailable === true && state.snapshot?.screen?.alternateBuffer !== true;
        if (promptNavigation) {
            event.preventDefault();
            navigatePrompt(state, event.key === 'ArrowUp');
            return;
        }
        const clipboardAction = getTerminalClipboardAction(event.key, event.ctrlKey, event.shiftKey, event.metaKey, event.altKey);
        if (clipboardAction === 'copy') {
            if (window.getSelection()?.toString()) return;
            event.preventDefault();
            if (state.selectionAnchor !== null) {
                copyRemoteSelection(state).catch(function () { });
            }
            return;
        }
        if (clipboardAction === 'paste') return;
        const specialKeys = [
            'Enter', 'Tab', 'Backspace', 'Escape',
            'ArrowUp', 'ArrowDown', 'ArrowRight', 'ArrowLeft',
            'Home', 'End', 'PageUp', 'PageDown', 'Insert', 'Delete',
            'F1', 'F2', 'F3', 'F4', 'F5', 'F6',
            'F7', 'F8', 'F9', 'F10', 'F11', 'F12'
        ];
        const kitty = state.snapshot?.screen?.kittyKeyboardActive === true;
        const special = specialKeys.includes(event.key);
        const printable = (!event.metaKey || kitty) && event.key.length === 1;
        const extended = kitty && (['Shift', 'Control', 'Alt', 'Meta', 'CapsLock', 'NumLock', 'ScrollLock', 'PrintScreen', 'Pause', 'ContextMenu'].includes(event.key) || /^F(1[3-9]|2[0-5])$/.test(event.key));
        if (special || printable || extended) {
            event.preventDefault();
            if (!isCircuitConnected()) return;
            // Like a real terminal, sending input drops the current selection and jumps back to the live tail
            state.selectionAnchor = null;
            state.selectionFocus = null;
            state.promptNavigationRow = null;
            scheduleRender(state, true);
            const input = [state.sessionId, event.key, printable ? event.key : '', event.shiftKey, event.ctrlKey, event.altKey, event.metaKey, event.code || '', event.repeat === true];
            state.pressedKeys.set(event.code || event.key, input);
            invokeCircuitMethod(state.dotNetRef, 'OnTerminalKey', ...input, false).catch(function () { });
        }
    });
    state.viewport.addEventListener('keyup', function (event) {
        const identity = event.code || event.key;
        if (!state.pressedKeys.has(identity)) return;
        state.pressedKeys.delete(identity);
        invokeCircuitMethod(state.dotNetRef, 'OnTerminalKey', state.sessionId, event.key, '', event.shiftKey, event.ctrlKey, event.altKey, event.metaKey, event.code || '', false, true).catch(function () { });
    });
    state.viewport.addEventListener('paste', function (event) {
        const text = event.clipboardData?.getData('text/plain') || '';
        if (text) {
            event.preventDefault();
            state.promptNavigationRow = null;
            scheduleRender(state, true);
            invokeCircuitMethod(state.dotNetRef, 'OnTerminalPaste', state.sessionId, text).catch(function () { });
        }
    });
    state.viewport.addEventListener('compositionstart', function () {
        state.composing = true;
    });
    state.viewport.addEventListener('compositionend', function (event) {
        state.composing = false;
        const data = event.data || state.input?.value || '';
        state.pendingCompositionCommit = data;
        if (data) sendInput(state, data);
        if (state.input) state.input.value = '';
        setTimeout(function () {
            if (state.pendingCompositionCommit === data) state.pendingCompositionCommit = '';
        }, 0);
    });
    state.viewport.addEventListener('focusin', function () {
        invokeCircuitMethod(state.dotNetRef, 'OnTerminalFocus', state.sessionId, true).catch(function () { });
    });
    state.viewport.addEventListener('focusout', function () {
        for (const input of state.pressedKeys.values())
            invokeCircuitMethod(state.dotNetRef, 'OnTerminalKey', ...input, true).catch(function () { });
        state.pressedKeys.clear();
        invokeCircuitMethod(state.dotNetRef, 'OnTerminalFocus', state.sessionId, false).catch(function () { });
    });
    state.viewport.addEventListener('mousedown', function (event) {
        const bounds = state.viewport.getBoundingClientRect();
        if (event.clientX >= bounds.left + state.viewport.clientWidth || event.clientY >= bounds.top + state.viewport.clientHeight) state.userScroll = true;
        if (state.userScroll) return; // La scrollbar nativa non inizia una selezione di testo
        const hyperlink = event.target.closest?.('.terminal-hyperlink');
        if (hyperlink && (!state.snapshot?.screen?.mouseTracking || event.ctrlKey || event.metaKey)) return;
        if (state.input) {
            positionTerminalInput(state);
            state.input.focus({ preventScroll: true });
        }
        const select = !state.snapshot?.screen?.mouseTracking || event.shiftKey;
        if (select && event.button === 0) {
            event.preventDefault();
            state.selecting = true;
            const position = getPositionFromPointer(state, event);
            if (!event.shiftKey || state.selectionAnchor === null) state.selectionAnchor = position;
            state.selectionFocus = position;
            state.selectionPointerX = event.clientX;
            state.selectionPointerY = event.clientY;
            runSelectionAutoscroll(state);
            scheduleRender(state);
            return;
        }
        if (sendMouse(state, event, 'down', event.button)) {
            state.mouseButton = event.button;
            event.preventDefault();
        }
    });
    state.viewport.addEventListener('mousemove', function (event) {
        if (state.selecting) {
            state.selectionPointerX = event.clientX;
            state.selectionPointerY = event.clientY;
            state.selectionFocus = getPositionFromPointer(state, event);
            scheduleRender(state);
            return;
        }
        if (state.mouseButton !== null) sendMouse(state, event, 'drag', state.mouseButton);
        else sendMouse(state, event, 'move', -1);
    });
    state.viewport.addEventListener('mouseup', function (event) {
        if (state.selecting) {
            state.selecting = false;
            stopSelectionAutoscroll(state);
            state.selectionFocus = getPositionFromPointer(state, event);
            if (comparePositions(state.selectionAnchor, state.selectionFocus) === 0) {
                state.selectionAnchor = null;
                state.selectionFocus = null;
            }
            scheduleRender(state);
            return;
        }
        if (state.mouseButton !== null) {
            sendMouse(state, event, 'up', state.mouseButton);
            state.mouseButton = null;
        }
    });
    state.viewport.addEventListener('wheel', function (event) {
        if (event.deltaY < 0) disarmFollowTail(state);
        state.userScroll = true;
        if (event.deltaY > 0 && isAtLiveTail(state)) state.following = true;
        const button = event.deltaY < 0 ? 64 : 65;
        const eventType = event.deltaY < 0 ? 'wheel-up' : 'wheel-down';
        if (!event.shiftKey && sendMouse(state, event, eventType, button)) event.preventDefault();
    }, { passive: false });
    state.viewport.addEventListener('contextmenu', function (event) {
        if (state.snapshot?.screen?.mouseTracking && !event.shiftKey) event.preventDefault();
    });
    state.windowSelectionMove = function (event) {
        if (!state.selecting || state.viewport.contains(event.target)) return;
        state.selectionPointerX = event.clientX;
        state.selectionPointerY = event.clientY;
        state.selectionFocus = getPositionFromPointer(state, event);
        scheduleRender(state);
    };
    state.windowSelectionUp = function (event) {
        if (!state.selecting) return;
        state.selecting = false;
        stopSelectionAutoscroll(state);
        if (publications().freeze) return; // Il checkpoint precede il mouseup sintetico del freeze
        state.selectionFocus = getPositionFromPointer(state, event);
        scheduleRender(state);
    };
    window.addEventListener('mousemove', state.windowSelectionMove);
    window.addEventListener('mouseup', state.windowSelectionUp);
}

function createRenderer(sessionId, container, dotNetReference) {
    const viewport = document.createElement('div');
    viewport.className = 'terminal-virtual-viewport';
    viewport.tabIndex = 0;
    viewport.setAttribute('role', 'textbox');
    viewport.setAttribute('aria-multiline', 'true');
    const spacer = document.createElement('div');
    spacer.className = 'terminal-virtual-spacer';
    const rowsLayer = document.createElement('div');
    rowsLayer.className = 'terminal-virtual-rows';
    const input = document.createElement('textarea');
    input.className = 'terminal-ime-input';
    input.setAttribute('aria-label', 'Terminal input');
    input.setAttribute('autocomplete', 'off');
    input.setAttribute('autocapitalize', 'off');
    input.setAttribute('spellcheck', 'false');
    input.inputMode = 'text';
    viewport.appendChild(spacer);
    viewport.appendChild(rowsLayer);
    viewport.appendChild(input);
    container.replaceChildren(viewport);

    const state = {
        sessionId: Number(sessionId),
        container: container,
        viewport: viewport,
        spacer: spacer,
        rowsLayer: rowsLayer,
        input: input,
        dotNetRef: dotNetReference,
        snapshot: null,
        pageCache: new Map(),
        pendingPages: new Set(),
        pressedKeys: new Map(),
        resizeObserver: null,
        resizeTimer: null,
        renderAnimationFrame: 0,
        followTailPending: false,
        following: true,
        programmaticScrollTop: null,
        userScroll: false,
        lifecycleGeneration: 0,
        nextResyncRequest: 0,
        resyncRequest: 0,
        disposed: false,
        lineHeight: 18,
        charWidth: 8.4,
        horizontalPadding: 0,
        lastCols: 0,
        lastRows: 0,
        renderedStart: 0,
        renderedEnd: 0,
        renderedRows: new Map(),
        promptNavigationRow: null,
        selectionAnchor: null,
        selectionFocus: null,
        selecting: false,
        composing: false,
        pendingCompositionCommit: '',
        selectionAnimationFrame: 0,
        selectionPointerX: 0,
        selectionPointerY: null,
        windowSelectionMove: null,
        windowSelectionUp: null,
        mouseButton: null
    };
    terminals.set(getKey(sessionId), state);
    invokeCircuitMethod(state.dotNetRef, 'OnTerminalVisibilityChanged', terminalPageVisible).catch(function () { });
    attachInputHandlers(state);
    positionTerminalInput(state);
    input.addEventListener('input', function () {
        if (state.composing) return;
        const data = input.value;
        input.value = '';
        if (!data) return;
        if (state.pendingCompositionCommit === data) {
            state.pendingCompositionCommit = '';
            return;
        }
        state.pendingCompositionCommit = '';
        sendInput(state, data);
    });
    viewport.addEventListener('scroll', function () {
        positionTerminalInput(state);
        if (state.programmaticScrollTop !== null && Math.abs(state.viewport.scrollTop - state.programmaticScrollTop) < 0.5) {
            state.programmaticScrollTop = null;
            return;
        }
        state.programmaticScrollTop = null;
        if (state.userScroll) state.promptNavigationRow = null;
        if (isAtLiveTail(state)) {
            if (state.userScroll) state.following = true;
        } else disarmFollowTail(state);
        state.userScroll = false;
        scheduleRender(state);
    }, { passive: true });
    state.resizeObserver = new ResizeObserver(function () {
        if (!terminalPageVisible || state.disposed || !isRendererVisible(state)) return;
        if (state.resizeTimer) clearTimeout(state.resizeTimer);
        state.resizeTimer = setTimeout(function () {
            state.resizeTimer = null;
            notifyResize(state);
            scheduleRender(state);
        }, 50);
    });
    state.resizeObserver.observe(container);
    return state;
}

export function initTerminal(sessionId, containerId, dotNetReference, generation, initial) {
    const container = document.getElementById(containerId);
    if (!container) return [120, 30];
    let state = getState(sessionId);
    if (!state || state.container !== container || state.viewGeneration !== generation) {
        disposeTerminal(sessionId);
        state = createRenderer(sessionId, container, dotNetReference);
        installTerminalView(state, dotNetReference, generation, initial);
    } else {
        state.dotNetRef = dotNetReference;
    }
    ensureTerminalVisibilityRegistration();
    notifyResize(state);
    return [...calculateSize(state), state.snapshot ? 1 : 0];
}

export function applyTerminalSnapshot(sessionId, snapshot, followTailOverride = null) {
    const state = getState(sessionId);
    if (!state || state.disposed || !snapshot) return;
    if (state.snapshot && snapshot.revision < state.snapshot.revision) return;
    const followTail = typeof followTailOverride === 'boolean' ? followTailOverride : state.following;
    if (typeof followTailOverride === 'boolean') state.following = followTailOverride;
    if (followTail) state.promptNavigationRow = null;
    if (snapshot.screen && !snapshot.screen.colorsIncluded && state.snapshot?.screen) {
        snapshot.screen.palette = state.snapshot.screen.palette;
        snapshot.screen.defaultForeground = state.snapshot.screen.defaultForeground;
        snapshot.screen.defaultBackground = state.snapshot.screen.defaultBackground;
        snapshot.screen.cursorColor = state.snapshot.screen.cursorColor;
    }
    const oldStart = state.snapshot?.historyStart;
    const oldMarkerRows = getHistoryLayout(state).markerRows;
    const oldAlternateBuffer = state.snapshot?.screen?.alternateBuffer;
    const oldScrollTop = state.viewport.scrollTop;
    state.snapshot = snapshot;
    if (state.viewport && snapshot.screen) {
        state.viewport.style.color = resolveTerminalColor(256, TERMINAL_COLOR_MODE.indexedOrDefault, false, snapshot.screen);
        state.viewport.style.backgroundColor = resolveTerminalColor(257, TERMINAL_COLOR_MODE.indexedOrDefault, true, snapshot.screen);
    }
    if (oldAlternateBuffer !== undefined && oldAlternateBuffer !== snapshot.screen?.alternateBuffer) {
        state.selectionAnchor = null;
        state.selectionFocus = null;
    }
    if (oldStart !== undefined && oldStart !== snapshot.historyStart) {
        for (const [start, page] of state.pageCache) {
            if (start + page.lines.length <= snapshot.historyStart) state.pageCache.delete(start);
        }
        if (!followTail && !snapshot.screen?.alternateBuffer && snapshot.historyStart > oldStart)
            scrollProgrammatically(state, Math.max(0, oldScrollTop + (computeHistoryLayout(snapshot).markerRows - oldMarkerRows - (snapshot.historyStart - oldStart)) * state.lineHeight));
        if (!snapshot.screen?.alternateBuffer && snapshot.historyStart > oldStart && state.selectionAnchor && state.selectionFocus) {
            const visualShift = computeHistoryLayout(snapshot).markerRows - oldMarkerRows - (snapshot.historyStart - oldStart);
            const minimum = computeHistoryLayout(snapshot).markerRows;
            state.selectionAnchor = { row: Math.max(minimum, state.selectionAnchor.row + visualShift), column: state.selectionAnchor.column };
            state.selectionFocus = { row: Math.max(minimum, state.selectionFocus.row + visualShift), column: state.selectionFocus.column };
        }
    }
    if (state.promptNavigationRow !== null && !snapshot.screen?.alternateBuffer)
        state.promptNavigationRow = Math.max(snapshot.historyStart, Math.min(snapshot.historyEnd + Math.max(0, (snapshot.screen?.lines?.length || 0) - 1), state.promptNavigationRow));
    scheduleRender(state, followTail);
}

export function applyTerminalAttach(sessionId, attach) {
    const state = getState(sessionId);
    if (!state || state.disposed || !attach?.session) return;
    if (state.snapshot && attach.session.revision < state.snapshot.revision) {
        scheduleRender(state, state.followTailPending);
        return;
    }
    const followTail = state.following;
    state.pageCache.clear();
    state.pendingPages.clear();
    state.renderedRows.clear();
    if (attach.historyTail) cachePage(state, attach.historyTail);
    applyTerminalSnapshot(sessionId, attach.session, followTail);
}

export function applyTerminalPatch(sessionId, patch) {
    const state = getState(sessionId);
    if (!state || state.disposed || !patch) return;
    if (patch.requiresResync || !state.snapshot || state.snapshot.revision !== patch.fromRevision) {
        requestTerminalResync(state);
        return;
    }
    if (patch.session) applyTerminalSnapshot(sessionId, patch.session);
}

export function fitTerminal(sessionId) {
    const state = getState(sessionId);
    if (!state) return [120, 30];
    notifyResize(state);
    scheduleRender(state);
    return calculateSize(state);
}

export function focusTerminal(sessionId, sequence = null) {
    const state = getState(sessionId);
    if (!isTopVisibleFloatingWindow('terminal-window', sequence) || !terminalPageVisible || !state || state.disposed) return;
    requestAnimationFrame(function () {
        if (!isTopVisibleFloatingWindow('terminal-window', sequence) || !terminalPageVisible || state.disposed || !isRendererVisible(state)) return;
        notifyResize(state);
        scheduleRender(state);
        if (state.input) {
            positionTerminalInput(state);
            state.input.focus({ preventScroll: true });
        }
        else if (state.viewport) state.viewport.focus({ preventScroll: true });
    });
}

export function disposeTerminal(sessionId) {
    const key = getKey(sessionId);
    const state = terminals.get(key);
    if (!state) return;
    state.disposed = true;
    disposeSurface(state.container);
    state.lifecycleGeneration++;
    state.resyncRequest = 0;
    if (state.resizeTimer) clearTimeout(state.resizeTimer);
    cancelScheduledRender(state);
    stopSelectionAutoscroll(state);
    if (state.windowSelectionMove) window.removeEventListener('mousemove', state.windowSelectionMove);
    if (state.windowSelectionUp) window.removeEventListener('mouseup', state.windowSelectionUp);
    if (state.resizeObserver) state.resizeObserver.disconnect();
    if (state.container) state.container.replaceChildren();
    state.dotNetRef = null;
    state.pageCache.clear();
    state.pendingPages.clear();
    state.renderedRows.clear();
    terminals.delete(key);
    releaseTerminalVisibilityRegistration();
}

export function disposeAllTerminals() {
    for (const key of Array.from(terminals.keys())) disposeTerminal(key);
}
