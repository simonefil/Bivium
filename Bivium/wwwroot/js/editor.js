// Bivium Editor - Monaco Editor integration

let editor = null;

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
export function initEditor(containerId, content, language) {
    const container = document.getElementById(containerId);
    if (!container) return;

    // Dispose existing editor if any
    if (editor) {
        editor.dispose();
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
            configureAndCreateEditor(container, content, language);
        };
        document.head.appendChild(script);
    } else {
        configureAndCreateEditor(container, content, language);
    }
}

/**
 * Configure the AMD loader and create the Monaco editor instance
 * @param {HTMLElement} container - Container element
 * @param {string} content - Initial text content
 * @param {string} language - Monaco language identifier
 */
function configureAndCreateEditor(container, content, language) {
    // Configure require paths for local Monaco
    require.config({
        paths: {
            'vs': './lib/monaco-editor/min/vs'
        }
    });

    // Configure worker URLs to use local path
    window.MonacoEnvironment = {
        getWorkerUrl: function (moduleId, label) {
            if (label === 'json') {
                return './lib/monaco-editor/min/vs/language/json/jsonWorker.js';
            }
            if (label === 'css' || label === 'scss' || label === 'less') {
                return './lib/monaco-editor/min/vs/language/css/cssWorker.js';
            }
            if (label === 'html' || label === 'handlebars' || label === 'razor') {
                return './lib/monaco-editor/min/vs/language/html/htmlWorker.js';
            }
            if (label === 'typescript' || label === 'javascript') {
                return './lib/monaco-editor/min/vs/language/typescript/tsWorker.js';
            }
            return './lib/monaco-editor/min/vs/editor/editor.worker.js';
        }
    };

    // Create Monaco editor
    require(['vs/editor/editor.main'], function () {
        editor = monaco.editor.create(container, {
            value: content,
            language: language,
            theme: 'vs-dark',
            minimap: { enabled: false },
            lineNumbers: 'on',
            wordWrap: 'on',
            scrollBeyondLastLine: false,
            automaticLayout: true,
            fontSize: 14,
            renderWhitespace: 'selection',
            tabSize: 4
        });
    });
}

/**
 * Get the current text content from the editor
 * @returns {string} Editor text content
 */
export function getEditorContent() {
    if (editor) {
        return editor.getValue();
    }
    return '';
}

/**
 * Set the editor text content
 * @param {string} content - Text content to set
 */
export function setEditorContent(content) {
    if (editor) {
        editor.setValue(content);
    }
}

/**
 * Dispose the Monaco editor instance and clean up
 */
export function disposeEditor() {
    if (editor) {
        editor.dispose();
        editor = null;
    }
}
