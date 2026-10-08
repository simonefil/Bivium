const RADZEN_SELECTION_KEY = Symbol.for('bivium.radzenSelection');

if (!globalThis[RADZEN_SELECTION_KEY]) {
    // Only the first focus of each input applies the initial selection: later focus returns keep the user's selection
    const selectedInputs = new WeakSet();
    globalThis[RADZEN_SELECTION_KEY] = true;
    document.addEventListener('focusin', function (event) {
        const input = event.target;
        if (!input || !input.dataset || input.dataset.biviumSelectionLength === undefined || typeof input.setSelectionRange !== 'function') return;
        if (selectedInputs.has(input)) return;
        selectedInputs.add(input);
        const selectionLength = Number.parseInt(input.dataset.biviumSelectionLength, 10);
        if (!Number.isFinite(selectionLength) || selectionLength < 0) return;
        input.setSelectionRange(0, Math.min(selectionLength, input.value.length));
    });
}
