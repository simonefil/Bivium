// Bivium Upload JS Module
// Handles chunked file upload with progress callbacks

// Chunk size: 50 MB
const CHUNK_SIZE = 50 * 1024 * 1024;

// Max retry attempts per chunk
const MAX_RETRIES = 3;

// .NET object reference for callbacks
let _dotNetRef = null;

// Selected file reference
let _selectedFile = null;

/**
 * Store .NET reference for progress/completion callbacks
 * @param {object} dotNetRef - DotNetObjectReference from Blazor
 */
export function initUpload(dotNetRef) {
    _dotNetRef = dotNetRef;
}

/**
 * Trigger the hidden file input and return the selected file name
 * @returns {Promise<string>} Selected file name, or empty string if cancelled
 */
export function selectFile() {
    return new Promise(function (resolve) {
        const input = document.getElementById('upload-file-input');
        if (!input) {
            resolve('');
            return;
        }

        // Reset value so the same file can be re-selected
        input.value = '';

        // Listen for file selection
        input.onchange = function () {
            if (input.files && input.files.length > 0) {
                _selectedFile = input.files[0];
                resolve(_selectedFile.name);
            } else {
                _selectedFile = null;
                resolve('');
            }
        };

        input.click();
    });
}

/**
 * Upload the selected file in chunks to the server
 * @param {string} destinationDir - Server destination directory path
 * @param {string} fileName - File name for the upload
 */
export async function uploadFile(destinationDir, fileName) {
    if (!_selectedFile) {
        if (_dotNetRef) {
            await _dotNetRef.invokeMethodAsync('OnUploadComplete', false, 'No file selected');
        }
        return;
    }

    const fileSize = _selectedFile.size;
    const totalChunks = Math.max(1, Math.ceil(fileSize / CHUNK_SIZE));

    for (let i = 0; i < totalChunks; i++) {
        const start = i * CHUNK_SIZE;
        const end = Math.min(start + CHUNK_SIZE, fileSize);
        const chunk = _selectedFile.slice(start, end);

        // Retry loop for each chunk
        let success = false;
        let lastError = '';

        for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
            try {
                const response = await fetch('/api/FileTransfer/upload', {
                    method: 'POST',
                    headers: {
                        'X-Destination-Dir': destinationDir,
                        'X-File-Name': fileName,
                        'X-Chunk-Index': i.toString(),
                        'X-Total-Chunks': totalChunks.toString()
                    },
                    body: chunk
                });

                if (!response.ok) {
                    const errorText = await response.text();
                    lastError = 'HTTP ' + response.status + ': ' + errorText;
                } else {
                    success = true;
                    break;
                }
            } catch (err) {
                lastError = err.message || 'Network error';
            }
        }

        // Chunk failed after all retries
        if (!success) {
            if (_dotNetRef) {
                await _dotNetRef.invokeMethodAsync('OnUploadComplete', false, 'Chunk ' + i + ' failed: ' + lastError);
            }
            return;
        }

        // Report progress
        if (_dotNetRef) {
            const percent = Math.round(((i + 1) / totalChunks) * 100);
            await _dotNetRef.invokeMethodAsync('OnUploadProgress', percent);
        }
    }

    // Upload complete
    if (_dotNetRef) {
        await _dotNetRef.invokeMethodAsync('OnUploadComplete', true, '');
    }
}

/**
 * Dispose resources
 */
export function dispose() {
    _dotNetRef = null;
    _selectedFile = null;
}
