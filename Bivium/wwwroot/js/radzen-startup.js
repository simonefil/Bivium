const RADZEN_MENU_HOVER_KEY = Symbol.for('bivium.radzenMenuHover');
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

/** Moves from an open top-level menu to the next one on hover, as in desktop menu bars. */
export function initializeRadzenMenuHover(host) {
    if (!host || host[RADZEN_MENU_HOVER_KEY]) return;

    const controller = new AbortController();
    host[RADZEN_MENU_HOVER_KEY] = controller;
    host.addEventListener('mouseover', function (event) {
        if (typeof globalThis.Radzen?.toggleMenuItem !== 'function') return;
        const targetItem = event.target.closest('.rz-navigation-item');
        if (!targetItem || !host.contains(targetItem)) return;

        const menuRoot = host.querySelector('.rz-menu');
        // In the responsive layout the items are stacked: hover must not open submenus
        if (!menuRoot || targetItem.parentElement !== menuRoot || menuRoot.classList.contains('rz-menu-open')) return;

        const activeItem = Array.from(menuRoot.children).find(function (item) {
            return item.classList.contains('rz-navigation-item-active');
        });
        if (!activeItem || activeItem === targetItem) return;
        // Only a submenu that is actually open allows the switch
        const openMenu = activeItem.querySelector(':scope > .rz-navigation-menu');
        if (!openMenu || openMenu.getClientRects().length === 0) return;

        const wrapper = targetItem.querySelector(':scope > .rz-navigation-item-wrapper');
        if (!wrapper) return;
        try {
            globalThis.Radzen.toggleMenuItem(wrapper, event, true, true);
        } catch {
            // Internal Radzen API unavailable: the native click-to-open behavior remains
        }
    }, { signal: controller.signal });
}

/** Releases only the hover listener owned by this mounted menu. */
export function disposeRadzenMenuHover(host) {
    if (!host) return;
    host[RADZEN_MENU_HOVER_KEY]?.abort();
    delete host[RADZEN_MENU_HOVER_KEY];
}
