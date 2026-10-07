const RADZEN_MENU_HOVER_KEY = Symbol.for('bivium.radzenMenuHover');
const RADZEN_SELECTION_KEY = Symbol.for('bivium.radzenSelection');

if (!globalThis[RADZEN_SELECTION_KEY]) {
    // Solo la prima focalizzazione di ogni input applica la selezione iniziale: i ritorni di focus conservano quella dell'utente
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

/** Passa da un menu top-level aperto al successivo con l'hover, come nelle menu bar desktop. */
export function initializeRadzenMenuHover(host) {
    if (!host || host[RADZEN_MENU_HOVER_KEY]) return;

    const controller = new AbortController();
    host[RADZEN_MENU_HOVER_KEY] = controller;
    host.addEventListener('mouseover', function (event) {
        if (typeof globalThis.Radzen?.toggleMenuItem !== 'function') return;
        const targetItem = event.target.closest('.rz-navigation-item');
        if (!targetItem || !host.contains(targetItem)) return;

        const menuRoot = host.querySelector('.rz-menu');
        // Nel layout responsive le voci sono impilate: l'hover non deve aprire sottomenu
        if (!menuRoot || targetItem.parentElement !== menuRoot || menuRoot.classList.contains('rz-menu-open')) return;

        const activeItem = Array.from(menuRoot.children).find(function (item) {
            return item.classList.contains('rz-navigation-item-active');
        });
        if (!activeItem || activeItem === targetItem) return;
        // Solo un sottomenu realmente aperto autorizza il passaggio
        const openMenu = activeItem.querySelector(':scope > .rz-navigation-menu');
        if (!openMenu || openMenu.getClientRects().length === 0) return;

        const wrapper = targetItem.querySelector(':scope > .rz-navigation-item-wrapper');
        if (!wrapper) return;
        try {
            globalThis.Radzen.toggleMenuItem(wrapper, event, true, true);
        } catch {
            // API Radzen interna non disponibile: resta il comportamento click-to-open nativo
        }
    }, { signal: controller.signal });
}

/** Releases only the hover listener owned by this mounted menu. */
export function disposeRadzenMenuHover(host) {
    if (!host) return;
    host[RADZEN_MENU_HOVER_KEY]?.abort();
    delete host[RADZEN_MENU_HOVER_KEY];
}
