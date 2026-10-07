// Bivium Editor - Monaco Editor integration
import { isTopVisibleFloatingWindow } from './interop.js';

let editor = null;
let editorGeneration = 0;
let checkpointPublisher = null;
let pendingEditorViewState = null;
let editorJournal = null;
let editorTheme = 'vs-dark';

/** Journal applicativo: usa eventi/edit/viewstate pubblici, mai la history nativa Monaco. */
function createEditorJournal(current, hydrated, changed) {
    const units = hydrated.units.map(unit => ({
        GroupId: unit.groupId,
        Batches: unit.batches.map(batch => ({ Changes: batch.changes.map(change => ({ Offset: change.offset, Removed: change.removed, Inserted: change.inserted })) })),
        BeforeViewState: unit.beforeViewState, AfterViewState: unit.afterViewState,
        AfterSelections: unit.afterSelections, Typing: unit.typing, LastEditAt: unit.lastEditAt
    }));
    let cursor = hydrated.cursor;
    let text = current.getValue();
    let mutations = [];
    let transaction = null;
    let depth = 0;
    let composing = false;
    let silent = false;
    let failed = false;
    let typingKey = false;
    let previousView = JSON.stringify(current.saveViewState());
    let previousSelections = JSON.stringify(current.getSelections());
    // Il flush esplicito è un confine comando: un nuovo mount non estende gruppi dell'owner precedente
    let group = null;
    const disposables = [];
    const container = current.getDomNode();
    const view = () => JSON.stringify(current.saveViewState());
    const selections = () => JSON.stringify(current.getSelections());

    function groupId() {
        const bytes = crypto.getRandomValues(new Uint8Array(16));
        bytes[6] = (bytes[6] & 15) | 64;
        bytes[8] = (bytes[8] & 63) | 128;
        const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
        return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    }

    function finish() {
        if (!transaction || composing || depth || silent) return;
        const captured = transaction;
        transaction = null;
        const now = Date.now();
        const last = units[cursor - 1];
        const typing = captured.typing && captured.batches.length === 1 && captured.batches[0].Changes.length === 1 && captured.batches[0].Changes[0].Removed === '' && !/[\r\n]/.test(captured.batches[0].Changes[0].Inserted) && Array.from(captured.batches[0].Changes[0].Inserted).length === 1;
        const contiguous = typing && group && cursor === units.length && last?.Typing && last.GroupId === group && now - last.LastEditAt < 500 && captured.beforeSelections === last.AfterSelections;
        const unit = { GroupId: contiguous ? group : groupId(), Batches: captured.batches, BeforeViewState: captured.beforeView, AfterViewState: view(), AfterSelections: selections(), Typing: typing, LastEditAt: now };
        units.splice(cursor);
        if (contiguous) units[cursor - 1] = { ...unit, Batches: [...last.Batches, ...unit.Batches], BeforeViewState: last.BeforeViewState };
        else { units.push(unit); cursor++; }
        group = typing ? unit.GroupId : null;
        typingKey = false;
        previousView = unit.AfterViewState;
        previousSelections = unit.AfterSelections;
        mutations.push({ Kind: 'Append', Unit: unit });
        changed();
    }

    function boundary() {
        finish();
        group = null;
        typingKey = false;
    }

    function apply(batch, inverse) {
        let shift = 0;
        const edits = [...batch.Changes].sort((left, right) => left.Offset - right.Offset).map(change => {
            const offset = change.Offset + (inverse ? shift : 0);
            const removed = inverse ? change.Inserted : change.Removed;
            const inserted = inverse ? change.Removed : change.Inserted;
            if (text.slice(offset, offset + removed.length) !== removed) throw new Error('Editor journal content mismatch');
            shift += change.Inserted.length - change.Removed.length;
            const start = current.getModel().getPositionAt(offset);
            const end = current.getModel().getPositionAt(offset + removed.length);
            return { range: new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column), text: inserted };
        });
        // applyEdits non inserisce un'altra operazione nel journal o nella history nativa
        current.getModel().applyEdits(edits);
        text = current.getValue();
    }

    function move(redo) {
        if (failed || silent || composing || globalThis[Symbol.for('bivium.desktopPublications')]?.freeze) return;
        boundary();
        if (depth || (redo ? cursor >= units.length : cursor <= 0)) return;
        const unit = units[redo ? cursor : cursor - 1];
        silent = true;
        try {
            if (redo) for (const batch of unit.Batches) apply(batch, false);
            else for (let index = unit.Batches.length - 1; index >= 0; index--) apply(unit.Batches[index], true);
            cursor += redo ? 1 : -1;
            current.restoreViewState(JSON.parse(redo ? unit.AfterViewState : unit.BeforeViewState));
            previousView = view();
            previousSelections = selections();
            mutations.push({ Kind: redo ? 'Redo' : 'Undo' });
        } finally { silent = false; }
        changed();
    }

    disposables.push(current.onBeginUpdate(() => {
        if (!silent && depth === 0 && !transaction) { previousView = view(); previousSelections = selections(); }
        depth++;
    }));
    disposables.push(current.onEndUpdate(() => { depth--; if (!depth) finish(); }));
    disposables.push(current.onDidChangeModelContent(event => {
        if (silent) { text = current.getValue(); return; }
        if (event.isUndoing || event.isRedoing) {
            failed = true;
            console.error('Native Monaco history bypassed the application journal; checkpoints and handoff are blocked');
            return;
        }
        transaction ??= { beforeView: previousView, beforeSelections: previousSelections, batches: [], typing: typingKey && !composing };
        transaction.batches.push({ Changes: event.changes.map(change => ({ Offset: change.rangeOffset, Removed: text.slice(change.rangeOffset, change.rangeOffset + change.rangeLength), Inserted: change.text })) });
        text = current.getValue();
        queueMicrotask(finish);
    }));
    disposables.push(current.onDidCompositionStart(() => { boundary(); composing = true; }));
    disposables.push(current.onDidCompositionEnd(() => { composing = false; finish(); boundary(); }));
    disposables.push(current.onDidPaste(boundary));
    disposables.push(current.onKeyDown(event => {
        const key = event.browserEvent.key;
        typingKey = !event.ctrlKey && !event.metaKey && !event.altKey && Array.from(key || '').length === 1;
        if (!typingKey && !composing) boundary();
    }));
    disposables.push(current.onDidChangeCursorSelection(event => {
        if (silent || composing) return;
        if (event.reason === monaco.editor.CursorChangeReason.Explicit) boundary();
        if (!transaction) { previousView = view(); previousSelections = selections(); }
    }));
    // Comandi globali, palette/context e trigger/getAction convergono nello stesso journal
    for (const [id, redo] of [['undo', false], ['default:undo', false], ['redo', true], ['default:redo', true]]) {
        disposables.push(monaco.editor.registerCommand(id, () => move(redo)));
        disposables.push(current.addAction({ id, label: redo ? 'Redo' : 'Undo', run: () => move(redo) }));
    }
    function beforeInput(event) {
        if (event.inputType === 'historyUndo' || event.inputType === 'historyRedo') {
            event.preventDefault(); event.stopImmediatePropagation(); move(event.inputType === 'historyRedo');
        } else if (event.inputType !== 'insertText' && !composing) boundary();
    }
    function paste() { boundary(); }
    container.addEventListener('beforeinput', beforeInput, true);
    container.addEventListener('paste', paste, true);
    return {
        capture() { finish(); const delta = mutations; mutations = []; return { mutations: delta, cursor }; },
        ready() { return !failed && !composing && !depth && !transaction; },
        settle() { finish(); },
        boundary,
        isSilent() { return silent; },
        silence(action) { silent = true; try { action(); text = current.getValue(); previousView = view(); previousSelections = selections(); } finally { silent = false; } },
        stop() { for (const disposable of disposables) disposable.dispose(); container.removeEventListener('beforeinput', beforeInput, true); container.removeEventListener('paste', paste, true); }
    };
}

/** Publishes coalesced full checkpoints in order; revisions advance only on server acknowledgement. */
function createCheckpointPublisher(current, dotNetRef, sessionId, initialRevision, leaseGeneration) {
    let revision = initialRevision;
    let pending = null;
    let sending = null;
    let timer = 0;
    let stopped = false;
    let rejected = false;

    function capture() {
        if (stopped || rejected || !editorJournal.ready()) return;
        const history = editorJournal.capture();
        pending = { content: current.getValue(), viewState: JSON.stringify(pendingEditorViewState ?? current.saveViewState()), mutations: [...(pending?.mutations ?? []), ...history.mutations], cursor: history.cursor };
        if (!sending) {
            sending = pump().finally(function () { sending = null; });
        }
    }

    async function pump() {
        try {
            while (pending && !stopped && !rejected) {
                const checkpoint = pending;
                pending = null;
                const data = new TextEncoder().encode(JSON.stringify({ Content: checkpoint.content, ViewState: checkpoint.viewState, HistoryMutations: checkpoint.mutations, HistoryCursor: checkpoint.cursor }));
                const stream = DotNet.createJSStreamReference(data);
                const acceptedRevision = await dotNetRef.invokeMethodAsync('OnEditorCheckpoint', sessionId, revision, leaseGeneration, stream);
                if (acceptedRevision < 0) {
                    rejected = true;
                    return;
                }
                revision = acceptedRevision;
            }
        } catch {
            // Never overwrite a new owner by retrying an unacknowledged checkpoint.
            rejected = true;
        }
    }

    function scheduleViewState() {
        if (editorJournal?.isSilent()) return;
        if (!timer && !stopped && !rejected) {
            timer = window.setTimeout(function () { timer = 0; capture(); }, 150);
        }
    }

    // Il journal pubblica soltanto dopo la fine dell'unità composita, mai metà IME
    current.onDidChangeCursorSelection(scheduleViewState);
    current.onDidScrollChange(scheduleViewState);

    return {
        async flush() {
            window.clearTimeout(timer);
            timer = 0;
            editorJournal.settle();
            if (!editorJournal.ready()) return false;
            capture();
            while (sending) await sending;
            return !stopped && !rejected && editorJournal.ready();
        },
        changed() { window.clearTimeout(timer); timer = 0; capture(); },
        stop() {
            stopped = true;
            pending = null;
            window.clearTimeout(timer);
        }
    };
}

/** Flushes the acknowledged draft before an explicit save or close; not an unload guarantee. */
export async function flushEditorCheckpoint() {
    editorJournal?.boundary();
    return checkpointPublisher ? await checkpointPublisher.flush() : false;
}

/**
 * Map file extension to Monaco language identifier
 * @param {string} ext - File extension including dot (e.g., ".cs")
 * @returns {string} Monaco language identifier
 */
export function getLanguageFromExtension(ext) {
    const map = {
        '.cs': 'csharp',
        '.csx': 'csharp',
        '.js': 'javascript',
        '.mjs': 'javascript',
        '.jsx': 'javascript',
        '.ts': 'typescript',
        '.tsx': 'typescript',
        '.py': 'python',
        '.json': 'json',
        '.jsonc': 'json',
        '.xml': 'xml',
        '.xsl': 'xml',
        '.xslt': 'xml',
        '.html': 'html',
        '.htm': 'html',
        '.css': 'css',
        '.scss': 'scss',
        '.less': 'less',
        '.md': 'markdown',
        '.yaml': 'yaml',
        '.yml': 'yaml',
        '.sql': 'sql',
        '.sh': 'shell',
        '.bash': 'shell',
        '.ps1': 'powershell',
        '.psm1': 'powershell',
        '.bat': 'bat',
        '.cmd': 'bat',
        '.cpp': 'cpp',
        '.cc': 'cpp',
        '.cxx': 'cpp',
        '.c': 'c',
        '.h': 'c',
        '.hpp': 'cpp',
        '.java': 'java',
        '.go': 'go',
        '.rs': 'rust',
        '.rb': 'ruby',
        '.php': 'php',
        '.lua': 'lua',
        '.r': 'r',
        '.swift': 'swift',
        '.kt': 'kotlin',
        '.ini': 'ini',
        '.toml': 'ini',
        '.cfg': 'ini',
        '.conf': 'ini',
        '.dockerfile': 'dockerfile',
        '.razor': 'razor',
        '.cshtml': 'razor',
        '.csproj': 'xml',
        '.sln': 'plaintext',
        '.log': 'plaintext',
        '.txt': 'plaintext',
        '.csv': 'plaintext',
        '.env': 'plaintext',
        '.gitignore': 'plaintext'
    };

    return map[ext] || 'plaintext';
}

/**
 * Initialize the Monaco editor in the given container
 * @param {string} containerId - DOM id of the container element
 * @param {string} content - Initial text content
 * @param {string} language - Monaco language identifier
 */
export function initEditor(containerId, content, language, sessionId, revision, viewState, leaseGeneration, history, modelEol) {
    const container = document.getElementById(containerId);
    if (!container) return Promise.resolve(false);

    const generation = ++editorGeneration;
    return new Promise(function (resolve) {
        // Dispose existing editor if any
        if (editor) {
            checkpointPublisher?.stop();
            editorJournal?.stop();
            editorJournal = null;
            checkpointPublisher = null;
            const model = editor.getModel();
            editor.dispose();
            model?.dispose();
            editor = null;
        }

        // Configure Monaco AMD loader
        const loaderScript = document.getElementById('monaco-loader');
        if (!loaderScript) {
            // Load the AMD loader script
            const script = document.createElement('script');
            script.id = 'monaco-loader';
            script.src = './lib/monaco-editor/min/vs/loader.js';
            script.onload = function () {
                configureAndCreateEditor(container, content, language, resolve, generation, sessionId, revision, viewState, leaseGeneration, history, modelEol);
            };
            document.head.appendChild(script);
        } else if (typeof globalThis.require?.config !== 'function') {
            loaderScript.addEventListener('load', function () {
                configureAndCreateEditor(container, content, language, resolve, generation, sessionId, revision, viewState, leaseGeneration, history, modelEol);
            }, { once: true });
        } else {
            configureAndCreateEditor(container, content, language, resolve, generation, sessionId, revision, viewState, leaseGeneration, history, modelEol);
        }
    });
}

/**
 * Configure the AMD loader and create the Monaco editor instance
 * @param {HTMLElement} container - Container element
 * @param {string} content - Initial text content
 * @param {string} language - Monaco language identifier
 */
function configureAndCreateEditor(container, content, language, resolve, generation, sessionId, revision, viewState, leaseGeneration, history, modelEol) {
    if (generation !== editorGeneration || !container.isConnected) {
        resolve(false);
        return;
    }
    // Configure require paths for local Monaco
    require.config({
        paths: {
            'vs': './lib/monaco-editor/min/vs'
        }
    });

    // Configure worker URLs to use local path
    window.MonacoEnvironment = {
        getWorkerUrl: function () {
            // The bundled AMD distribution loads each language through its shared worker bootstrap.
            return new URL('./lib/monaco-editor/min/vs/base/worker/workerMain.js', document.baseURI).href;
        }
    };

    // Create Monaco editor
    require(['vs/editor/editor.main'], async function () {
        if (generation !== editorGeneration || !container.isConnected) {
            resolve(false);
            return;
        }
        editor = monaco.editor.create(container, {
            value: content,
            language: language,
            theme: editorTheme,
            minimap: { enabled: false },
            lineNumbers: 'on',
            wordWrap: 'on',
            scrollBeyondLastLine: false,
            automaticLayout: true,
            fontSize: 14,
            renderWhitespace: 'selection',
            tabSize: 4,
            readOnly: true
        });

        const current = editor;
        if (modelEol) current.getModel().setEOL(modelEol === '\r\n' ? monaco.editor.EndOfLineSequence.CRLF : monaco.editor.EndOfLineSequence.LF);
        const dotNetRef = window._editorSaveDotNetRef;
        try {
            const stream = DotNet.createJSStreamReference(new TextEncoder().encode(JSON.stringify(current.getValue())));
            revision = await dotNetRef.invokeMethodAsync('OnEditorModelInitialized', sessionId, revision, leaseGeneration, current.getModel().getEOL(), stream);
        } catch {
            console.error('Editor model initialization failed; input and checkpoints remain disabled');
            revision = -1;
        }
        if (revision < 0 || generation !== editorGeneration || current !== editor || !container.isConnected) {
            if (revision < 0) console.error('Editor model base was not acknowledged; input and checkpoints remain disabled');
            resolve(false);
            return;
        }

        // Hydration must not emit edits or dirty notifications.
        pendingEditorViewState = viewState ? JSON.parse(viewState) : null;
        if (pendingEditorViewState && container.closest('.editor-window')?.classList.contains('visible')) {
            editor.restoreViewState(pendingEditorViewState);
            pendingEditorViewState = null;
        }
        if (dotNetRef) {
            editorJournal = createEditorJournal(editor, history, () => checkpointPublisher?.changed());
            editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, function () {
                if (!globalThis[Symbol.for('bivium.desktopPublications')]?.freeze) dotNetRef.invokeMethodAsync('OnEditorSave');
            });
            checkpointPublisher = createCheckpointPublisher(editor, dotNetRef, sessionId, revision, leaseGeneration);
        }

        current.updateOptions({ readOnly: false });

        resolve(true);
    });
}

/** Layout the retained Monaco instance only after its window is visible. */
export function layoutEditor(sequence = null) {
    const current = editor;
    requestAnimationFrame(function () {
        const win = document.getElementById('editor-window');
        if (!current || current !== editor || !win?.classList.contains('visible')) return;
        current.layout();
        if (pendingEditorViewState) {
            editorJournal.silence(() => current.restoreViewState(pendingEditorViewState));
            pendingEditorViewState = null;
        }
        if (isTopVisibleFloatingWindow('editor-window', sequence) && !win.dataset.focusTarget?.startsWith('control:')) current.focus();
    });
}

/**
 * Applies the Monaco theme derived from the current Radzen theme.
 * @param {string} theme - Monaco theme id (vs, vs-dark, hc-black, hc-light)
 */
export function setEditorTheme(theme) {
    editorTheme = theme || 'vs-dark';
    // Il tema Monaco è globale: si applica anche all'istanza già montata
    if (globalThis.monaco?.editor) monaco.editor.setTheme(editorTheme);
}

/**
 * Set the .NET object reference for Ctrl+S save callback
 * @param {object} dotNetRef - .NET DotNetObjectReference
 */
export function setSaveCallback(dotNetRef) {
    window._editorSaveDotNetRef = dotNetRef;
}

/**
 * Dispose the Monaco editor instance and clean up
 */
export function disposeEditor() {
    editorGeneration++;
    checkpointPublisher?.stop();
    editorJournal?.stop();
    editorJournal = null;
    checkpointPublisher = null;
    pendingEditorViewState = null;
    if (editor) {
        const model = editor.getModel();
        editor.dispose();
        model?.dispose();
        editor = null;
    }
    window._editorSaveDotNetRef = null;
}
