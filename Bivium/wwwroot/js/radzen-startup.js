const RADZEN_MENU_HOVER_KEY = Symbol.for('bivium.radzenMenuHover');
const RADZEN_SELECTION_KEY = Symbol.for('bivium.radzenSelection');

if (!globalThis[RADZEN_SELECTION_KEY]) {
    globalThis[RADZEN_SELECTION_KEY] = true;
    document.addEventListener('focusin', function (event) {
        const input = event.target;
        if (!input || !input.dataset || input.dataset.biviumSelectionLength === undefined || typeof input.setSelectionRange !== 'function') return;
        const selectionLength = Number.parseInt(input.dataset.biviumSelectionLength, 10);
        if (!Number.isFinite(selectionLength) || selectionLength < 0) return;
        input.setSelectionRange(0, Math.min(selectionLength, input.value.length));
    });
}

export function initializeRadzenMenuHover(host) {
    if (!host || host[RADZEN_MENU_HOVER_KEY]) return;

    const controller = new AbortController();
    host[RADZEN_MENU_HOVER_KEY] = controller;
    host.addEventListener('mouseover', function (event) {
        const targetItem = event.target.closest('.rz-navigation-item');
        if (!targetItem || !host.contains(targetItem)) return;

        const targetList = targetItem.parentElement;
        const menuRoot = host.querySelector('.rz-menu');
        if (!menuRoot || targetList !== menuRoot) return;

        const activeItem = Array.from(menuRoot.children).find(function (item) {
            return item.classList.contains('rz-navigation-item-active');
        });
        if (!activeItem || activeItem === targetItem) return;

        const wrapper = targetItem.querySelector(':scope > .rz-navigation-item-wrapper');
        if (wrapper && globalThis.Radzen) {
            globalThis.Radzen.toggleMenuItem(wrapper, event, true, true);
        }
    }, { signal: controller.signal });
}

/** Releases only the hover listener owned by this mounted menu. */
export function disposeRadzenMenuHover(host) {
    if (!host) return;
    host[RADZEN_MENU_HOVER_KEY]?.abort();
    delete host[RADZEN_MENU_HOVER_KEY];
}
