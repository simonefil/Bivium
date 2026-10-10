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

const RADZEN_MENU_CLOSE_KEY = Symbol.for('bivium.radzenMenuClose');

if (!globalThis[RADZEN_MENU_CLOSE_KEY]) {
    globalThis[RADZEN_MENU_CLOSE_KEY] = true;
    const MENU_SELECTOR = '#workspace-application-menu';

    // Radzen closes the open dropdowns only on document click: a right click must not leave them open beside the context menu
    document.addEventListener('contextmenu', function () {
        const menu = document.querySelector(MENU_SELECTOR);
        if (!menu || !globalThis.Radzen) return;
        menu.querySelectorAll('.rz-navigation-item-active').forEach(function (item) {
            globalThis.Radzen.closeMenuItem(item);
        });
    }, true);

    // The responsive menu stays expanded after a command: its toggle collapses it before the command reaches Blazor,
    // so a window opened by the command is placed below the collapsed bar
    document.addEventListener('click', function (event) {
        const link = event.target instanceof Element ? event.target.closest(MENU_SELECTOR + ' .rz-navigation-item-link') : null;
        if (!link) return;
        const item = link.closest('.rz-navigation-item');
        if (!item || item.querySelector('.rz-navigation-menu') || item.classList.contains('rz-state-disabled')) return;
        const menu = document.querySelector(MENU_SELECTOR);
        const toggle = menu ? menu.querySelector('.rz-menu-toggle') : null;
        if (!toggle || !menu.classList.contains('rz-menu-open') || toggle.offsetParent === null) return;
        toggle.click();
    }, true);
}
