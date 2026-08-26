// Bivium Upload JS Module
// Handles hierarchical multi-entry selection, drag and drop, and chunked upload

const CHUNK_SIZE = 50 * 1024 * 1024;
const MAX_RETRIES = 3;

let _dotNetRef = null;
let _selectedFiles = new Map();
let _selectedDirectories = new Set();
let _dropZone = null;
let _dragDepth = 0;
let _uploadInProgress = false;

/**
 * Store the .NET callback reference and initialize the drop area.
 * @param {object} dotNetRef - DotNetObjectReference from Blazor.
 */
export function initUpload(dotNetRef) {
    _dotNetRef = dotNetRef;
    detachDropZone();
    _dropZone = document.getElementById('upload-drop-zone');
    if (_dropZone) {
        _dropZone.addEventListener('dragenter', handleDragEnter);
        _dropZone.addEventListener('dragover', handleDragOver);
        _dropZone.addEventListener('dragleave', handleDragLeave);
        _dropZone.addEventListener('drop', handleDrop);
    }
}

/**
 * Opens the native multi-file picker and appends its selection.
 */
export function selectFiles() {
    if (_uploadInProgress) return;
    const input = document.getElementById('upload-files-input');
    if (!input) return;

    input.value = '';
    input.onchange = async function () {
        addFilesFromList(input.files);
        await notifySelectionChanged();
    };
    input.click();
}

/**
 * Opens the native multi-directory picker and appends its selection.
 */
export function selectDirectories() {
    if (_uploadInProgress) return;
    const input = document.getElementById('upload-directories-input');
    if (!input) return;

    input.value = '';
    input.onchange = async function () {
        const entries = Array.from(input.webkitEntries || []);
        if (entries.length > 0) {
            for (const entry of entries) {
                await addLegacyEntry(entry, '');
            }
        } else {
            addFilesFromList(input.files);
        }
        await notifySelectionChanged();
    };
    input.click();
}

/**
 * Clears all selected files and directories.
 */
export async function clearUploadSelection() {
    if (_uploadInProgress) return;
    _selectedFiles.clear();
    _selectedDirectories.clear();
    await notifySelectionChanged();
}

/**
 * Uploads the complete current selection while preserving relative paths.
 * @param {string} destinationDir - Server destination directory.
 * @param {string} attachmentId - Authorized workspace attachment.
 * @param {number} leaseGeneration - Authorized lease generation.
 */
export function uploadSelection(destinationDir, attachmentId, leaseGeneration) {
    runUploadSelection(destinationDir, attachmentId, leaseGeneration).catch(async function (error) {
        _uploadInProgress = false;
        await reportComplete(false, error.message || 'Upload failed');
    });
}

async function runUploadSelection(destinationDir, attachmentId, leaseGeneration) {
    if (_uploadInProgress) return;
    const files = Array.from(_selectedFiles.values()).sort(function (left, right) {
        return left.relativePath.localeCompare(right.relativePath);
    });
    const directories = Array.from(_selectedDirectories).sort(comparePathsParentFirst);
    if (files.length === 0 && directories.length === 0) {
        await reportComplete(false, 'No files or folders selected');
        return;
    }

    _uploadInProgress = true;
    const createdDirectories = [];
    let uploadError = '';
    try {
        for (const relativePath of directories) {
            await reportProgress(0, 'Preparing ' + relativePath, 0, files.length);
            const directoryResult = await requestUploadDirectory(destinationDir, relativePath, false, attachmentId, leaseGeneration);
            if (directoryResult.created) createdDirectories.push(relativePath);
        }

        const totalBytes = files.reduce(function (total, item) { return total + item.file.size; }, 0);
        let completedBytes = 0;
        for (let fileIndex = 0; fileIndex < files.length; fileIndex++) {
            const item = files[fileIndex];
            await uploadOneFile(destinationDir, item, attachmentId, leaseGeneration, function (uploadedFileBytes) {
                let percent;
                if (totalBytes > 0) {
                    percent = Math.round(((completedBytes + uploadedFileBytes) / totalBytes) * 100);
                } else {
                    percent = Math.round(((fileIndex + 1) / files.length) * 100);
                }
                return reportProgress(percent, item.relativePath, fileIndex, files.length);
            });
            completedBytes += item.file.size;
            await reportProgress(totalBytes > 0 ? Math.round((completedBytes / totalBytes) * 100) : Math.round(((fileIndex + 1) / files.length) * 100), item.relativePath, fileIndex + 1, files.length);
        }
    } catch (error) {
        uploadError = error.message || 'Upload failed';
    }

    try {
        createdDirectories.sort(comparePathsChildFirst);
        for (const relativePath of createdDirectories) {
            await requestUploadDirectory(destinationDir, relativePath, true, attachmentId, leaseGeneration);
        }
    } catch (error) {
        if (!uploadError) uploadError = error.message || 'Could not finalize uploaded folders';
    }

    if (uploadError) {
        await reportComplete(false, uploadError);
    } else {
        await reportProgress(100, '', files.length, files.length);
        await reportComplete(true, '');
    }
    _uploadInProgress = false;
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

async function uploadOneFile(destinationDir, item, attachmentId, leaseGeneration, onProgress) {
    const fileSize = item.file.size;
    const totalChunks = Math.max(1, Math.ceil(fileSize / CHUNK_SIZE));
    const uploadId = createUploadId();

    for (let chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++) {
        const start = chunkIndex * CHUNK_SIZE;
        const end = Math.min(start + CHUNK_SIZE, fileSize);
        const chunk = item.file.slice(start, end);
        let success = false;
        let lastError = '';

        for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
            try {
                const response = await fetch('/api/FileTransfer/upload', {
                    method: 'POST',
                    headers: buildUploadHeaders(destinationDir, item.file.name, item.relativePath, chunkIndex, totalChunks, uploadId, attachmentId, leaseGeneration),
                    body: chunk
                });
                if (response.ok) {
                    success = true;
                    break;
                }
                lastError = 'HTTP ' + response.status + ': ' + await response.text();
            } catch (error) {
                lastError = error.message || 'Network error';
            }
        }

        if (!success) throw new Error('Chunk ' + chunkIndex + ' of ' + item.relativePath + ' failed: ' + lastError);
        await onProgress(end);
    }
}

function buildUploadHeaders(destinationDir, fileName, relativePath, chunkIndex, totalChunks, uploadId, attachmentId, leaseGeneration) {
    return {
        'X-Destination-Dir': encodeURIComponent(destinationDir),
        'X-File-Name': encodeURIComponent(fileName),
        'X-Relative-Path': encodeURIComponent(relativePath),
        'X-Chunk-Index': String(chunkIndex),
        'X-Total-Chunks': String(totalChunks),
        'X-Upload-Id': uploadId,
        'X-Bivium-Attachment': attachmentId,
        'X-Bivium-Lease-Generation': String(leaseGeneration)
    };
}

async function requestUploadDirectory(destinationDir, relativePath, finalize, attachmentId, leaseGeneration) {
    let lastError = '';
    for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
        try {
            const response = await fetch('/api/FileTransfer/upload-directory', {
                method: 'POST',
                headers: {
                    'X-Destination-Dir': encodeURIComponent(destinationDir),
                    'X-Relative-Path': encodeURIComponent(relativePath),
                    'X-Finalize': String(finalize),
                    'X-Bivium-Attachment': attachmentId,
                    'X-Bivium-Lease-Generation': String(leaseGeneration)
                }
            });
            if (response.ok) return await response.json();
            lastError = 'HTTP ' + response.status + ': ' + await response.text();
        } catch (error) {
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

async function addFileSystemHandle(handle, parentPath) {
    const relativePath = normalizeRelativePath(parentPath ? parentPath + '/' + handle.name : handle.name);
    if (handle.kind === 'file') {
        addSelectedFile(await handle.getFile(), relativePath);
        return;
    }

    addSelectedDirectory(relativePath);
    for await (const childHandle of handle.values()) {
        await addFileSystemHandle(childHandle, relativePath);
    }
}

async function addLegacyEntry(entry, parentPath) {
    const relativePath = normalizeRelativePath(parentPath ? parentPath + '/' + entry.name : entry.name);
    if (entry.isFile) {
        const file = await new Promise(function (resolve, reject) { entry.file(resolve, reject); });
        addSelectedFile(file, relativePath);
        return;
    }

    if (!entry.isDirectory) return;
    addSelectedDirectory(relativePath);
    const reader = entry.createReader();
    let children = await readLegacyEntries(reader);
    while (children.length > 0) {
        for (const child of children) {
            await addLegacyEntry(child, relativePath);
        }
        children = await readLegacyEntries(reader);
    }
}

function readLegacyEntries(reader) {
    return new Promise(function (resolve, reject) { reader.readEntries(resolve, reject); });
}

async function notifySelectionChanged() {
    if (!_dotNetRef) return;
    const totalBytes = Array.from(_selectedFiles.values()).reduce(function (total, item) { return total + item.file.size; }, 0);
    await _dotNetRef.invokeMethodAsync('OnUploadSelectionChanged', _selectedFiles.size, _selectedDirectories.size, totalBytes);
}

async function reportProgress(percent, currentPath, processedFiles, totalFiles) {
    if (!_dotNetRef) return;
    await _dotNetRef.invokeMethodAsync('OnUploadProgress', Math.max(0, Math.min(100, percent)), currentPath, processedFiles, totalFiles);
}

async function reportComplete(success, message) {
    if (!_dotNetRef) return;
    await _dotNetRef.invokeMethodAsync('OnUploadComplete', success, message);
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
    if (_uploadInProgress) return;

    try {
        const items = Array.from(event.dataTransfer?.items || []).filter(function (item) { return item.kind === 'file'; });
        if (items.length > 0 && typeof items[0].getAsFileSystemHandle === 'function') {
            const handlePromises = items.map(function (item) { return item.getAsFileSystemHandle(); });
            for (const handlePromise of handlePromises) {
                const handle = await handlePromise;
                if (handle) await addFileSystemHandle(handle, '');
            }
        } else if (items.length > 0 && typeof items[0].webkitGetAsEntry === 'function') {
            for (const item of items) {
                const entry = item.webkitGetAsEntry();
                if (entry) await addLegacyEntry(entry, '');
            }
        } else {
            addFilesFromList(event.dataTransfer?.files);
        }
        await notifySelectionChanged();
    } catch (error) {
        await reportComplete(false, error.message || 'Could not read dropped files');
    }
}

function comparePathsParentFirst(left, right) {
    const depthDifference = left.split('/').length - right.split('/').length;
    return depthDifference !== 0 ? depthDifference : left.localeCompare(right);
}

function comparePathsChildFirst(left, right) {
    return -comparePathsParentFirst(left, right);
}

function createUploadId() {
    if (window.crypto && window.crypto.randomUUID) return window.crypto.randomUUID();

    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (char) {
        const random = Math.floor(Math.random() * 16);
        const value = char === 'x' ? random : (random & 0x3) | 0x8;
        return value.toString(16);
    });
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

/**
 * Disposes callbacks and DOM handlers.
 */
export function dispose() {
    detachDropZone();
    _dotNetRef = null;
    _selectedFiles.clear();
    _selectedDirectories.clear();
    _uploadInProgress = false;
}
