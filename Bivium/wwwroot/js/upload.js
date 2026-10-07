// Bivium Upload JS Module
// Handles hierarchical multi-entry selection, drag and drop, and chunked upload

const CHUNK_SIZE = 50 * 1024 * 1024;
const MAX_RETRIES = 3;

let _selectedFiles = new Map();
let _selectedDirectories = new Set();
let _dropZone = null;
let _filesButton = null;
let _directoriesButton = null;
let _dragDepth = 0;
let _uploadInProgress = false;
let _uploadController = null;
let _context = null;
let _runPromise = null;
let _selectionPromise = Promise.resolve();
let _selectionInProgress = false;
const desktopPublications = globalThis[Symbol.for('bivium.desktopPublications')] ??= { pending: new Set(), panelTrackers: new Set(), windows: new Set(), freeze: null, failures: 0, composing: false };

const LEASE_REVOKED_MESSAGE = 'Workspace lease revoked';
const USER_CANCEL_REASON = 'Upload cancelled by user';
const PAUSE_REASON = 'Upload paused';

/**
 * Store the .NET callback reference and initialize the drop area.
 * @param {object} dotNetRef - DotNetObjectReference from Blazor.
 */
export function initUpload(dotNetRef, sessionId, generation, attachmentId) {
    if (_context?.sessionId !== sessionId || _context?.generation !== generation) {
        _uploadController?.abort();
        _selectedFiles.clear();
        _selectedDirectories.clear();
        _uploadInProgress = false;
    }
    _context = { reference: dotNetRef, sessionId, generation, attachmentId };
    detachDropZone();
    detachPickerButtons();
    _dropZone = document.getElementById('upload-drop-zone');
    _filesButton = document.getElementById('upload-add-files-button');
    _directoriesButton = document.getElementById('upload-add-folders-button');
    if (_dropZone) {
        _dropZone.addEventListener('dragenter', handleDragEnter);
        _dropZone.addEventListener('dragover', handleDragOver);
        _dropZone.addEventListener('dragleave', handleDragLeave);
        _dropZone.addEventListener('drop', handleDrop);
    }
    if (_filesButton) _filesButton.addEventListener('click', selectFiles);
    if (_directoriesButton) _directoriesButton.addEventListener('click', selectDirectories);
}

/**
 * Opens the native multi-file picker and appends its selection.
 */
function selectFiles() {
    if (_uploadInProgress || _selectionInProgress) return;
    const input = document.getElementById('upload-files-input');
    if (!input) return;
    const context = _context;

    input.value = '';
    input.onchange = async function () {
        if (_context !== context || desktopPublications.freeze) return;
        addFilesFromList(input.files);
        await notifySelectionChanged(context);
    };
    input.click();
}

/**
 * Opens the native multi-directory picker and appends its selection.
 */
function selectDirectories() {
    if (_uploadInProgress || _selectionInProgress) return;
    const input = document.getElementById('upload-directories-input');
    if (!input) return;
    const context = _context;

    input.value = '';
    input.onchange = async function () {
        if (_context !== context || desktopPublications.freeze) return;
        await trackSelection(async function () {
            const entries = Array.from(input.webkitEntries || []);
            if (entries.length > 0) {
                for (const entry of entries) await addLegacyEntry(entry, '', context);
            } else {
                addFilesFromList(input.files);
            }
            await notifySelectionChanged(context);
        });
    };
    input.click();
}

/**
 * Clears all selected files and directories.
 */
export async function clearUploadSelection() {
    if (_uploadInProgress || _selectionInProgress || desktopPublications.freeze) return;
    _selectedFiles.clear();
    _selectedDirectories.clear();
    await notifySelectionChanged();
}

/**
 * Uploads the complete current selection while preserving relative paths.
 * @param {string} sessionId - Workspace-owned upload session.
 * @param {string} attachmentId - Authorized workspace attachment.
 * @param {number} leaseGeneration - Authorized lease generation.
 */
export function uploadSelection(sessionId, attachmentId, leaseGeneration) {
    if (_uploadInProgress || _selectionInProgress || _context?.sessionId !== sessionId || _context?.generation !== leaseGeneration || desktopPublications.freeze) return false;
    _runPromise = runUploadSelection(sessionId, attachmentId, leaseGeneration, _context);
    return true;
}

async function runUploadSelection(sessionId, attachmentId, leaseGeneration, context) {
    if (_uploadInProgress) return;
    const files = Array.from(_selectedFiles.values()).sort(function (left, right) {
        return left.relativePath.localeCompare(right.relativePath);
    });
    const directories = Array.from(_selectedDirectories).sort(comparePathsParentFirst);
    if (files.length === 0 && directories.length === 0) {
        await reportLocalError('No files or folders selected', context);
        return;
    }

    _uploadInProgress = true;
    const uploadController = new AbortController();
    _uploadController = uploadController;
    let admitted = false;
    try {
        await _selectionPromise;
        uploadController.signal.throwIfAborted();
        let state = await uploadRequest('/api/FileTransfer/upload-state', 'GET', attachmentId, leaseGeneration, uploadController.signal);
        if (state.id !== sessionId) throw new Error('Upload session changed. Reopen the dialog.');
        await verifySources(state, files, directories, attachmentId, leaseGeneration, uploadController.signal);
        state = await uploadRequest('/api/FileTransfer/upload-start?id=' + sessionId + '&revision=' + state.revision, 'POST', attachmentId, leaseGeneration, uploadController.signal);
        admitted = true;
        const destinationDir = state.destination;
        for (const directory of state.directories) {
            if (!directory.prepared) await requestUploadDirectory(destinationDir, directory.relativePath, false, sessionId, attachmentId, leaseGeneration, uploadController.signal);
        }
        for (const receipt of state.files) {
            if (receipt.completed) continue;
            const item = files.find(file => file.relativePath === receipt.source.relativePath);
            await uploadOneFile(destinationDir, item, receipt, sessionId, attachmentId, leaseGeneration, uploadController.signal);
        }
        // Created directories are stored on the server, even when the HTTP response was lost
        state = await uploadRequest('/api/FileTransfer/upload-state', 'GET', attachmentId, leaseGeneration, uploadController.signal);
        for (const directory of [...state.directories].sort((left, right) => comparePathsChildFirst(left.relativePath, right.relativePath))) {
            if (directory.created && !directory.finalized) await requestUploadDirectory(destinationDir, directory.relativePath, true, sessionId, attachmentId, leaseGeneration, uploadController.signal);
        }
        await uploadRequest('/api/FileTransfer/upload-complete?id=' + sessionId, 'POST', attachmentId, leaseGeneration, uploadController.signal);
        await reportComplete(true, '', context);
    } catch (error) {
        if (!uploadController.signal.aborted && !(error instanceof UploadLeaseRevokedError)) {
            if (admitted) await reportComplete(false, error.message || 'Upload failed', context);
            else await reportLocalError(error.message || 'Source verification failed', context);
        }
    } finally {
        if (_uploadController === uploadController) {
            _uploadInProgress = false;
            _uploadController = null;
        }
    }
}

/** Verifies metadata of the entire manifest and the SHA-256 of every already acknowledged chunk. */
async function verifySources(state, files, directories, attachmentId, generation, signal) {
    if (files.length !== state.files.length || directories.length !== state.directories.length || state.directories.some(directory => !directories.includes(directory.relativePath))) {
        throw new Error('Select the same original files and folders. The selection does not match the saved manifest.');
    }
    for (const receipt of state.files) {
        const item = files.find(file => file.relativePath === receipt.source.relativePath);
        if (!item || item.file.name !== receipt.source.name || item.file.size !== receipt.source.size || item.file.lastModified !== receipt.source.lastModified) {
            throw new Error('Source metadata does not match: ' + receipt.source.relativePath);
        }
        for (let index = 0; index < receipt.chunkHashes.length; index++) {
            signal.throwIfAborted();
            const chunk = item.file.slice(index * CHUNK_SIZE, Math.min((index + 1) * CHUNK_SIZE, item.file.size));
            if (globalThis.crypto?.subtle) {
                const digest = await crypto.subtle.digest('SHA-256', await chunk.arrayBuffer());
                const hash = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, '0')).join('');
                if (hash !== receipt.chunkHashes[index]) throw new Error('Received source chunk does not match: ' + receipt.source.relativePath);
            } else {
                // The default listener is HTTP: the server verifies the same chunk without rewriting it
                const headers = buildUploadHeaders(state.destination, receipt.source.name, receipt.source.relativePath, index, Math.max(1, Math.ceil(item.file.size / CHUNK_SIZE)), receipt.id, state.id, attachmentId, generation);
                headers['X-Verify-Only'] = 'true';
                const response = await fetch('/api/FileTransfer/upload', { method: 'POST', headers, body: chunk, signal });
                if (!response.ok) {
                    const message = await response.text();
                    if (isLeaseRevokedResponse(response.status, message)) throw new UploadLeaseRevokedError(message);
                    throw new Error(message || 'Received source chunk does not match: ' + receipt.source.relativePath);
                }
            }
        }
    }
}

async function uploadRequest(url, method, attachmentId, generation, signal, body = null) {
    const headers = { 'X-Bivium-Attachment': attachmentId, 'X-Bivium-Lease-Generation': String(generation) };
    if (body !== null) headers['Content-Type'] = 'application/json';
    const response = await fetch(url, { method, headers, signal, body: body === null ? null : JSON.stringify(body) });
    if (response.ok) return await response.json();
    const message = await response.text();
    if (isLeaseRevokedResponse(response.status, message)) throw new UploadLeaseRevokedError(message);
    throw new Error(message || 'HTTP ' + response.status);
}

/** Explicit pause or handoff pause: stops the transport and waits for the current chunk rollback, preserving the selection. */
export async function pauseUpload(sessionId, generation) {
    if (_context?.sessionId !== sessionId || _context?.generation !== generation) throw new Error('Upload adapter changed');
    _uploadController?.abort(PAUSE_REASON);
    if (_runPromise) await _runPromise;
}

/**
 * Cancels the active browser request without changing already committed server entries.
 * @returns {boolean} True when an active upload was cancelled.
 */
export function cancelUpload() {
    const uploadController = _uploadController;
    if (!uploadController || uploadController.signal.aborted) return false;

    uploadController.abort(USER_CANCEL_REASON);
    if (_uploadController === uploadController) {
        _uploadController = null;
        _uploadInProgress = false;
    }
    return true;
}

/**
 * Normalizes a browser-relative path to slash separators.
 * @param {string} path - Relative path.
 * @returns {string} Normalized path, or an empty string for an invalid path.
 */
export function normalizeRelativePath(path) {
    const parts = String(path || '').replaceAll('\\', '/').split('/');
    const normalizedParts = [];
    for (const part of parts) {
        if (!part || part === '.') continue;
        if (part === '..') return '';
        normalizedParts.push(part);
    }
    return normalizedParts.join('/');
}

/**
 * Returns all parent directories of one relative file or directory path.
 * @param {string} relativePath - Normalized relative path.
 * @returns {string[]} Parent paths from root to leaf.
 */
export function getParentDirectories(relativePath) {
    const normalized = normalizeRelativePath(relativePath);
    if (!normalized) return [];

    const parts = normalized.split('/');
    const result = [];
    for (let i = 1; i < parts.length; i++) {
        result.push(parts.slice(0, i).join('/'));
    }
    return result;
}

async function uploadOneFile(destinationDir, item, receipt, sessionId, attachmentId, leaseGeneration, signal) {
    const fileSize = item.file.size;
    const totalChunks = Math.max(1, Math.ceil(fileSize / CHUNK_SIZE));
    const uploadId = receipt.id;

    for (let chunkIndex = receipt.receivedChunks; chunkIndex < totalChunks; chunkIndex++) {
        const start = chunkIndex * CHUNK_SIZE;
        const end = Math.min(start + CHUNK_SIZE, fileSize);
        const chunk = item.file.slice(start, end);
        let success = false;
        let lastError = '';

        for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
            try {
                const response = await fetch('/api/FileTransfer/upload', {
                    method: 'POST',
                    headers: buildUploadHeaders(destinationDir, item.file.name, item.relativePath, chunkIndex, totalChunks, uploadId, sessionId, attachmentId, leaseGeneration),
                    body: chunk,
                    signal: signal
                });
                if (response.ok) {
                    success = true;
                    break;
                }
                const responseText = await response.text();
                lastError = 'HTTP ' + response.status + ': ' + responseText;
                if (isLeaseRevokedResponse(response.status, responseText)) throw new UploadLeaseRevokedError(lastError);
            } catch (error) {
                if (error instanceof UploadLeaseRevokedError || signal.aborted) throw error;
                lastError = error.message || 'Network error';
            }
        }

        if (!success) throw new Error('Chunk ' + chunkIndex + ' of ' + item.relativePath + ' failed: ' + lastError);
    }
}

function isLeaseRevokedResponse(status, responseText) {
    return status === 409 && String(responseText || '').trim() === LEASE_REVOKED_MESSAGE;
}

class UploadLeaseRevokedError extends Error {
}

function buildUploadHeaders(destinationDir, fileName, relativePath, chunkIndex, totalChunks, uploadId, sessionId, attachmentId, leaseGeneration) {
    return {
        'X-Destination-Dir': encodeURIComponent(destinationDir),
        'X-File-Name': encodeURIComponent(fileName),
        'X-Relative-Path': encodeURIComponent(relativePath),
        'X-Chunk-Index': String(chunkIndex),
        'X-Total-Chunks': String(totalChunks),
        'X-Upload-Id': uploadId,
        'X-Upload-Session': sessionId,
        'X-Bivium-Attachment': attachmentId,
        'X-Bivium-Lease-Generation': String(leaseGeneration)
    };
}

async function requestUploadDirectory(destinationDir, relativePath, finalize, sessionId, attachmentId, leaseGeneration, signal) {
    let lastError = '';
    for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
        try {
            const response = await fetch('/api/FileTransfer/upload-directory', {
                method: 'POST',
                headers: {
                    'X-Destination-Dir': encodeURIComponent(destinationDir),
                    'X-Relative-Path': encodeURIComponent(relativePath),
                    'X-Finalize': String(finalize),
                    'X-Upload-Session': sessionId,
                    'X-Bivium-Attachment': attachmentId,
                    'X-Bivium-Lease-Generation': String(leaseGeneration)
                },
                signal: signal
            });
            if (response.ok) return await response.json();
            const responseText = await response.text();
            lastError = 'HTTP ' + response.status + ': ' + responseText;
            if (isLeaseRevokedResponse(response.status, responseText)) throw new UploadLeaseRevokedError(lastError);
        } catch (error) {
            if (error instanceof UploadLeaseRevokedError || signal.aborted) throw error;
            lastError = error.message || 'Network error';
        }
    }
    throw new Error((finalize ? 'Could not finalize ' : 'Could not create ') + relativePath + ': ' + lastError);
}

function addFilesFromList(fileList) {
    for (const file of Array.from(fileList || [])) {
        addSelectedFile(file, file.webkitRelativePath || file.name);
    }
}

function addSelectedFile(file, relativePath) {
    const normalized = normalizeRelativePath(relativePath || file.name);
    if (!normalized) return;

    _selectedFiles.set(normalized, { file: file, relativePath: normalized });
    for (const directory of getParentDirectories(normalized)) {
        _selectedDirectories.add(directory);
    }
}

function addSelectedDirectory(relativePath) {
    const normalized = normalizeRelativePath(relativePath);
    if (!normalized) return;

    _selectedDirectories.add(normalized);
    for (const parent of getParentDirectories(normalized)) {
        _selectedDirectories.add(parent);
    }
}

async function addFileSystemHandle(handle, parentPath, context) {
    if (_context !== context) return;
    const relativePath = normalizeRelativePath(parentPath ? parentPath + '/' + handle.name : handle.name);
    if (handle.kind === 'file') {
        const file = await handle.getFile();
        if (_context === context) addSelectedFile(file, relativePath);
        return;
    }

    addSelectedDirectory(relativePath);
    for await (const childHandle of handle.values()) {
        await addFileSystemHandle(childHandle, relativePath, context);
    }
}

async function addLegacyEntry(entry, parentPath, context) {
    if (_context !== context) return;
    const relativePath = normalizeRelativePath(parentPath ? parentPath + '/' + entry.name : entry.name);
    if (entry.isFile) {
        const file = await new Promise(function (resolve, reject) { entry.file(resolve, reject); });
        if (_context === context) addSelectedFile(file, relativePath);
        return;
    }

    if (!entry.isDirectory) return;
    addSelectedDirectory(relativePath);
    const reader = entry.createReader();
    let children = await readLegacyEntries(reader);
    while (children.length > 0) {
        for (const child of children) {
            await addLegacyEntry(child, relativePath, context);
        }
        children = await readLegacyEntries(reader);
    }
}

function readLegacyEntries(reader) {
    return new Promise(function (resolve, reject) { reader.readEntries(resolve, reject); });
}

async function notifySelectionChanged(context = _context) {
    if (!context || _context !== context) return;
    const files = Array.from(_selectedFiles.values(), item => ({ relativePath: item.relativePath, name: item.file.name, size: item.file.size, lastModified: item.file.lastModified }));
    const directories = Array.from(_selectedDirectories);
    const pending = _selectionPromise.catch(() => {}).then(async function () {
        if (_context !== context) return;
        const state = await uploadRequest('/api/FileTransfer/upload-state', 'GET', context.attachmentId, context.generation);
        if (state.id !== context.sessionId) throw new Error('Upload session changed. Select the source again.');
        if (state.canEditManifest) {
            await uploadRequest('/api/FileTransfer/upload-manifest?id=' + context.sessionId + '&revision=' + state.revision, 'POST', context.attachmentId, context.generation, undefined, { files, directories });
        }
        if (_context !== context) return;
        const acknowledged = await context.reference.invokeMethodAsync('OnUploadSelectionChanged', context.sessionId, context.generation, files.length > 0 || directories.length > 0);
        if (!acknowledged) desktopPublications.failures++;
    });
    _selectionPromise = pending;
    desktopPublications.pending.add(pending);
    try { await pending; }
    catch (error) {
        desktopPublications.failures++;
        if (!(error instanceof UploadLeaseRevokedError)) await reportComplete(false, error.message || 'Selection checkpoint failed', context);
        throw error;
    }
    finally { desktopPublications.pending.delete(pending); }
}

async function reportComplete(success, message, context = _context) {
    if (!context || _context !== context) return;
    try { await context.reference.invokeMethodAsync('OnUploadComplete', context.sessionId, context.generation, success, message); }
    catch { /* Server receipts survive dispose or circuit loss. */ }
}

/** A preflight error does not publish phase, progress or checkpoint in the workspace. */
async function reportLocalError(message, context = _context) {
    if (!context || _context !== context) return;
    try { await context.reference.invokeMethodAsync('OnUploadVerificationFailed', context.sessionId, context.generation, message); }
    catch { /* The local error does not survive the caller's dispose. */ }
}

async function trackSelection(action) {
    _selectionInProgress = true;
    const pending = action();
    desktopPublications.pending.add(pending);
    try { await pending; }
    catch (error) { desktopPublications.failures++; throw error; }
    finally { desktopPublications.pending.delete(pending); _selectionInProgress = false; }
}

function handleDragEnter(event) {
    event.preventDefault();
    if (_uploadInProgress) return;
    _dragDepth++;
    if (_dropZone) _dropZone.classList.add('upload-drop-active');
}

function handleDragOver(event) {
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
}

function handleDragLeave(event) {
    event.preventDefault();
    if (_uploadInProgress) return;
    _dragDepth = Math.max(0, _dragDepth - 1);
    if (_dragDepth === 0 && _dropZone) _dropZone.classList.remove('upload-drop-active');
}

async function handleDrop(event) {
    event.preventDefault();
    _dragDepth = 0;
    if (_dropZone) _dropZone.classList.remove('upload-drop-active');
    if (_uploadInProgress || _selectionInProgress || desktopPublications.freeze) return;

    const context = _context;
    try {
        await trackSelection(async function () {
            const items = Array.from(event.dataTransfer?.items || []).filter(function (item) { return item.kind === 'file'; });
            if (items.length > 0 && typeof items[0].getAsFileSystemHandle === 'function') {
                const handlePromises = items.map(function (item) { return item.getAsFileSystemHandle(); });
                for (const handlePromise of handlePromises) {
                    const handle = await handlePromise;
                    if (handle) await addFileSystemHandle(handle, '', context);
                }
            } else if (items.length > 0 && typeof items[0].webkitGetAsEntry === 'function') {
                for (const item of items) {
                    const entry = item.webkitGetAsEntry();
                    if (entry) await addLegacyEntry(entry, '', context);
                }
            } else {
                addFilesFromList(event.dataTransfer?.files);
            }
            await notifySelectionChanged(context);
        });
    } catch (error) {
        await reportComplete(false, error.message || 'Could not read dropped files', context);
    }
}

function comparePathsParentFirst(left, right) {
    const depthDifference = left.split('/').length - right.split('/').length;
    return depthDifference !== 0 ? depthDifference : left.localeCompare(right);
}

function comparePathsChildFirst(left, right) {
    return -comparePathsParentFirst(left, right);
}

function detachDropZone() {
    if (!_dropZone) return;
    _dropZone.removeEventListener('dragenter', handleDragEnter);
    _dropZone.removeEventListener('dragover', handleDragOver);
    _dropZone.removeEventListener('dragleave', handleDragLeave);
    _dropZone.removeEventListener('drop', handleDrop);
    _dropZone.classList.remove('upload-drop-active');
    _dropZone = null;
    _dragDepth = 0;
}

function detachPickerButtons() {
    if (_filesButton) _filesButton.removeEventListener('click', selectFiles);
    if (_directoriesButton) _directoriesButton.removeEventListener('click', selectDirectories);
    _filesButton = null;
    _directoriesButton = null;
}

/**
 * Disposes callbacks and DOM handlers.
 */
export function dispose(sessionId, generation) {
    if (_context?.sessionId !== sessionId || _context?.generation !== generation) return;
    _context = null;
    _uploadController?.abort();
    _uploadController = null;
    detachDropZone();
    detachPickerButtons();
    _selectedFiles.clear();
    _selectedDirectories.clear();
    _uploadInProgress = false;
}
