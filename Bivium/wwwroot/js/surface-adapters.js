// App-owned surfaces: only semantic identities and visual state, never values or HTML.
import { createDesktopPublicationReference, requestVisualFrame } from './interop.js';
const key = Symbol.for('bivium.desktopPublications');
const registrationKey = Symbol.for('bivium.surfaceAdapter');
const formatKey = Symbol.for('bivium.formatPopupControl');

function registry() {
    const value = globalThis[key];
    value.surfaceTrackers ??= new Set();
    return value;
}

function visible(element) {
    return !!element?.isConnected && element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden';
}

function modal() {
    return Array.from(document.querySelectorAll('.rz-dialog-wrapper, dialog[open]')).filter(visible).at(-1);
}

function publisher(host, reference, method, generation, initial, read) {
    disposeSurface(host);
    const shared = registry();
    const transport = createDesktopPublicationReference(reference);
    let revision = initial.revision;
    let latest = null;
    let sending = null;
    let stopped = false;
    let failed = false;
    let signature = null;
    const controller = new AbortController();
    const registration = {
        restoring: true,
        capture(finalCapture = false) {
            if (stopped || failed || registration.restoring || (shared.freeze && finalCapture !== true)) return;
            const draft = read();
            const next = JSON.stringify(draft);
            if (signature === next) return;
            signature = next;
            latest = draft;
            startPump();
        },
        ready() { return !stopped && !failed && !registration.restoring && (host.isConnected || !!shared.freeze); },
        stop() { stopped = true; controller.abort(); registration.observer?.disconnect(); registration.resizeObserver?.disconnect(); shared.surfaceTrackers.delete(registration); }
    };
    function startPump() {
        if (sending || !latest || stopped || failed) return;
        sending = pump().finally(() => { sending = null; startPump(); });
        const pending = sending;
        shared.pending.add(pending);
        pending.then(() => shared.pending.delete(pending), () => shared.pending.delete(pending));
    }
    async function pump() {
        try {
            while (latest && !stopped && !failed) {
                const draft = latest;
                latest = null;
                const accepted = await transport.invokeMethodAsync(method, generation, { ...draft, revision });
                if (accepted < 0) { failed = true; shared.failures++; return; }
                revision = accepted;
            }
        } catch { failed = true; shared.failures++; }
    }
    host[registrationKey] = registration;
    shared.surfaceTrackers.add(registration);
    return { registration, signal: controller.signal };
}

export { publisher as createSurfacePublisher };

/** App keys: explicit id/name or accessible labels declared by the markup, never DOM indices. */
function focusKey(element, root) {
    const keyed = element?.closest('[data-ui-key]');
    if (keyed && root.contains(keyed)) return 'app:' + keyed.dataset.uiKey;
    const named = element?.closest('[name]');
    if (named && root.contains(named)) return 'name:' + named.getAttribute('name');
    const labelled = element?.closest('[aria-label]');
    if (labelled && root.contains(labelled)) return 'label:' + labelled.getAttribute('aria-label');
    // Closed labels declared by the app forms; does not serialize arbitrary text or DOM values
    const button = element?.closest('button');
    const text = button?.querySelector('.rz-button-text')?.textContent.trim();
    if (button && root.contains(button) && ['Cancel', 'OK', 'Save', 'Close', 'Delete', 'No', 'Yes', 'Yes to all', 'Calculate size', 'Cancel calculation', 'Add Files...', 'Add Folders...', 'Clear', 'Generate QR', 'Disable 2FA', 'Copy secret', 'Enable 2FA', 'Change password'].includes(text)) return 'button:' + text;
    return '';
}

function focusTarget(root, key) {
    const [kind, ...parts] = key.split(':');
    const value = parts.join(':');
    const attribute = kind === 'app' ? 'data-ui-key' : kind === 'name' ? 'name' : kind === 'label' ? 'aria-label' : null;
    const element = kind === 'button' ? Array.from(root.querySelectorAll('button')).find(item => focusKey(item, root) === key) : attribute ? Array.from(root.querySelectorAll('[' + attribute + ']')).find(item => item.getAttribute(attribute) === value) : null;
    return element?.matches('input, textarea, button, a, [tabindex]') ? element : element?.querySelector('input, textarea, button, a, [tabindex]');
}

// Standard API also for readonly; no reading or writing of the field value.
function fieldSelection(element, key) {
    if (!element?.matches('input:not([type=password]):not([type=file]), textarea') || typeof element.selectionStart !== 'number') return null;
    return { key, start: element.selectionStart, end: element.selectionEnd, direction: element.selectionDirection || 'none' };
}

function restoreFieldSelection(element, selection) {
    if (!selection || !fieldSelection(element, selection.key) || typeof element.setSelectionRange !== 'function') return;
    try { element.setSelectionRange(selection.start, selection.end, selection.direction); } catch { /* API not supported by the control type */ }
}

function restoreSurfaceScrolls(scrollers, positions) {
    for (const position of positions || []) {
        const element = scrollers().find(([key]) => key === position.key)?.[1];
        if (element) { element.scrollTop = position.top; element.scrollLeft = position.left; }
    }
}

function listenSurfaceFields(host, adapter, accepts = () => true) {
    for (const event of ['focusin', 'focusout', 'select', 'selectionchange', 'keyup', 'pointerup', 'input', 'scroll']) {
        (event === 'selectionchange' ? document : host).addEventListener(event, value => {
            if (accepts(value)) adapter.registration.capture();
        }, { capture: true, passive: true, signal: adapter.signal });
    }
}

/** Restore after the owner's autofocus; the tracker reads no value, password or token. */
export function installDialogSurface(root, reference, generation, initial) {
    const wrapper = root?.closest('.rz-dialog-wrapper');
    if (!wrapper) return false;
    const body = root.closest('.rz-dialog-content') || root;
    // app.css:47 and :172 assign overflow to the two stacks, in addition to the native body
    const scrollers = () => [['dialog-body', body], ...Array.from(root.querySelectorAll('.bivium-radzen-modal, .bivium-radzen-upload-dialog')).map(element => ['app-body', element]), ...Array.from(root.querySelectorAll('[data-ui-scroll-key]')).map(element => [element.dataset.uiScrollKey, element]), ...Array.from(root.querySelectorAll('textarea[name]')).map(element => ['field:' + element.name, element])];
    const identity = { ownerId: initial.ownerId, questionId: initial.questionId, phase: initial.phase, surface: initial.surface };
    let lastFocus = initial.focusKey || '';
    let selectionStart = initial.selectionStart;
    let selectionEnd = initial.selectionEnd;
    let selectionDirection = initial.selectionDirection;
    const textareas = () => Array.from(root.querySelectorAll('textarea[name]')).filter(element => ['vertical', 'horizontal', 'both'].includes(getComputedStyle(element).resize));
    const formatControl = root.querySelector('[data-workspace-format-surface]');
    const adapter = publisher(root, reference, 'OnDialogSurfaceChanged', generation, initial, () => {
        const active = document.activeElement;
        if (root.contains(active)) {
            lastFocus = focusKey(active, root);
            const selection = initial.surface !== 'auth' ? fieldSelection(active, lastFocus) : null;
            selectionStart = selection?.start ?? -1;
            selectionEnd = selection?.end ?? -1;
            selectionDirection = selection?.direction ?? 'none';
        } else if (active === wrapper.querySelector('.rz-dialog-titlebar-close')) lastFocus = 'dialog-close';
        return { ...identity, focusKey: lastFocus, selectionStart, selectionEnd, selectionDirection,
            scrolls: scrollers().map(([key, element]) => ({ key, top: element.scrollTop, left: element.scrollLeft })),
            formatPopup: formatControl?.[formatKey]?.read() || null,
            textareaGeometries: textareas().map(element => { const rect = element.getBoundingClientRect(); return { key: focusKey(element, root), width: rect.width, height: rect.height, resize: getComputedStyle(element).resize }; })
        };
    });
    adapter.registration.resizeObserver = new ResizeObserver(() => adapter.registration.capture());
    for (const element of textareas()) adapter.registration.resizeObserver.observe(element);
    listenSurfaceFields(wrapper, adapter, event => !formatControl?.[formatKey]?.owns(event.type === 'selectionchange' ? document.activeElement : event.target));
    wrapper.addEventListener('bivium:surface-change', () => adapter.registration.capture(), { signal: adapter.signal });
    requestVisualFrame(() => requestVisualFrame(async () => {
        if (root[registrationKey] !== adapter.registration) return;
        try {
          if (formatControl && !formatControl[formatKey]) {
              await new Promise(resolve => {
                  const ready = () => { if (formatControl[formatKey]) resolve(); };
                  root.addEventListener('bivium:format-ready', ready, { signal: adapter.signal });
                  adapter.signal.addEventListener('abort', resolve, { once: true });
                  ready();
              });
          }
          if (root[registrationKey] !== adapter.registration) return;
          if (modal() === wrapper && !registry().freeze) {
            // Typed measurements, not a style copy; preserves the responsive layout on the non-resizable axis
            for (const geometry of initial.textareaGeometries || []) {
                const element = focusTarget(root, geometry.key);
                if (!element?.matches('textarea')) continue;
                const style = getComputedStyle(element);
                const correction = axis => style.boxSizing === 'border-box' ? 0 : axis === 'height' ? ['paddingTop', 'paddingBottom', 'borderTopWidth', 'borderBottomWidth'].reduce((sum, property) => sum + (parseFloat(style[property]) || 0), 0) : ['paddingLeft', 'paddingRight', 'borderLeftWidth', 'borderRightWidth'].reduce((sum, property) => sum + (parseFloat(style[property]) || 0), 0);
                if (geometry.resize === 'vertical' || geometry.resize === 'both') element.style.height = Math.max(0, geometry.height - correction('height')) + 'px';
                if (geometry.resize === 'horizontal' || geometry.resize === 'both') element.style.width = Math.max(0, geometry.width - correction('width')) + 'px';
            }
            const target = initial.focusKey === 'dialog-close' ? wrapper.querySelector('.rz-dialog-titlebar-close') : focusTarget(root, initial.focusKey || '');
            if (visible(target) && !target.disabled) {
                target.focus({ preventScroll: true });
                if (initial.surface !== 'auth' && initial.selectionStart >= 0)
                    restoreFieldSelection(target, { key: initial.focusKey, start: initial.selectionStart, end: initial.selectionEnd, direction: initial.selectionDirection });
            }
            if (formatControl && initial.formatPopup && !await formatControl[formatKey].restore(initial.formatPopup)) throw new Error('Format popup restore was not authorized');
            if (root[registrationKey] !== adapter.registration) return;
            // The official highlight uses scrollIntoView: restores the dialog stacks after the popup
            restoreSurfaceScrolls(scrollers, initial.scrolls);
          }
          adapter.registration.restoring = false;
          adapter.registration.capture();
        } catch {
            if (adapter.signal.aborted) return;
            // Fail-closed: the mount stays not ready and the drain does not promise a successful hydration
            registry().failures++;
        }
    }));
    return true;
}

/** Descriptor of the official popup; all checkpoints converge on the dialog's single publisher. */
export function installDropDownControl(control, popupId, listId, reference, generation, ownerId, questionId, phase, controlId) {
    disposeDropDownControl(control);
    const popup = document.getElementById(popupId);
    const list = document.getElementById(listId);
    const scroller = list?.parentElement; // RadzenDropDown.razor: official wrapper of the listbox
    if (!control || !popup || !list || !scroller) return false;
    const controller = new AbortController();
    const notify = () => control.dispatchEvent(new CustomEvent('bivium:surface-change', { bubbles: true }));
    const transport = createDesktopPublicationReference(reference);
    const descriptor = {
        read() { return { open: control.getAttribute('aria-expanded') === 'true' && !!globalThis.Radzen.popupOpened(popupId), highlightIndex: Array.from(list.querySelectorAll('[role="option"]')).findIndex(element => element.classList.contains('rz-state-highlight')), scrollTop: scroller.scrollTop, scrollLeft: scroller.scrollLeft, focused: control.contains(document.activeElement) || popup.contains(document.activeElement) }; },
        async restore(visual) {
            if (!descriptor.ready()) return false;
            const blocking = modal();
            const focused = visual.focused && (!blocking || blocking.contains(control));
            if (focused) control.focus({ preventScroll: true });
            const accepted = await transport.invokeMethodAsync('RestorePopupAsync', generation, ownerId, questionId, phase, controlId, { ...visual, focused });
            if (!accepted || !descriptor.ready()) return false;
            scroller.scrollTop = visual.scrollTop;
            scroller.scrollLeft = visual.scrollLeft;
            return true;
        },
        ready() { return control.isConnected && popup.isConnected && list.isConnected && control[formatKey] === descriptor && control.dataset.workspacePopupIdentity === `${ownerId}:${questionId}:${phase}:${generation}:${controlId}`; },
        owns(element) { return !!element && (control.contains(element) || popup.contains(element)); },
        stop() { controller.abort(); observer.disconnect(); }
    };
    const observer = new MutationObserver(notify);
    observer.observe(popup, { subtree: true, attributes: true, attributeFilter: ['class', 'style', 'aria-selected'] });
    observer.observe(control, { attributes: true, attributeFilter: ['aria-expanded', 'aria-activedescendant'] });
    scroller.addEventListener('scroll', notify, { passive: true, signal: controller.signal });
    control.addEventListener('focusin', notify, { signal: controller.signal });
    control.addEventListener('focusout', notify, { signal: controller.signal });
    popup.addEventListener('focusin', notify, { signal: controller.signal });
    popup.addEventListener('focusout', notify, { signal: controller.signal });
    control[formatKey] = descriptor;
    control[registrationKey] = descriptor;
    control.dispatchEvent(new CustomEvent('bivium:format-ready', { bubbles: true }));
    return true;
}

export function disposeDropDownControl(control) {
    control?.[formatKey]?.stop();
    if (control) { delete control[formatKey]; delete control[registrationKey]; }
}

/** Renamer session publisher; conditional controls must complete their own mount. */
export function installRenamerSurface(root, reference, generation, initial) {
    const controls = () => Array.from(root.querySelectorAll('[data-workspace-dropdown-surface]'));
    const popupOwns = element => controls().some(control => control[formatKey]?.owns(element));
    const fields = () => Array.from(root.querySelectorAll('input[name], textarea[name]')).filter(element => !popupOwns(element));
    const scrollers = () => {
        const items = Array.from(root.querySelectorAll('[data-ui-scroll-key]')).map(element => [element.dataset.uiScrollKey, element]);
        const grid = root.querySelector('.renamer-file-list .rz-data-grid-data');
        if (grid) items.push(['renamer-preview-grid', grid]);
        return items;
    };
    const ownerCurrent = () => root.isConnected && root.dataset.workspaceRenamerSession === initial.sessionId && Number(root.dataset.workspaceRenamerGeneration) === generation;
    let lastFocus = initial.focusKey || '';
    let retained = initial;
    let initialized = false;
    let finalCapture = false;
    const adapter = publisher(root, reference, 'OnRenamerViewChanged', generation, initial, () => {
        if (!initialized || !visible(root)) return retained;
        const active = document.activeElement;
        const focused = root.contains(active) && !popupOwns(active);
        if (focused && !registry().freeze) lastFocus = focusKey(active, root);
        const view = {
            sessionId: initial.sessionId,
            popups: finalCapture ? retained.popups : controls().map(control => ({ controlId: control.querySelector('input[name]')?.name, popup: control[formatKey].read() })),
            focusKey: finalCapture ? retained.focusKey : lastFocus,
            focused: finalCapture ? retained.focused : focused,
            scrolls: scrollers().map(([key, element]) => ({ key, top: element.scrollTop, left: element.scrollLeft })),
            selections: fields().map(element => fieldSelection(element, focusKey(element, root))).filter(Boolean)
        };
        if (!registry().freeze) retained = view;
        return view;
    });
    const ready = adapter.registration.ready;
    const mounted = () => ownerCurrent() && root[registrationKey] === adapter.registration && controls().every(control => control[formatKey]?.ready());
    adapter.registration.ready = () => ready() && mounted() && (initialized || !visible(root));
    const capture = adapter.registration.capture;
    adapter.registration.capture = () => { if (mounted()) capture(); };
    adapter.registration.captureFinal = () => {
        if (!registry().freeze || !adapter.registration.ready()) return;
        finalCapture = true;
        try { capture(true); } finally { finalCapture = false; }
    };
    let restoring = false;
    const restore = async () => {
        if (initialized || restoring || !mounted() || registry().freeze) return;
        // Minimized: keeps the draft without clamping to zero on a still hidden viewport.
        if (!visible(root)) { adapter.registration.restoring = false; adapter.registration.capture(); return; }
        restoring = true;
        adapter.registration.restoring = true;
        try {
            for (const control of controls()) {
                const id = control.querySelector('input[name]')?.name;
                const saved = initial.popups.find(item => item.controlId === id);
                if (saved && !await control[formatKey].restore(saved.popup)) throw new Error('Renamer popup owner changed');
                if (adapter.signal.aborted || !mounted()) return;
            }
            for (const selection of initial.selections || []) restoreFieldSelection(focusTarget(root, selection.key), selection);
            const blocking = modal();
            if (initial.focused && (!blocking || blocking.contains(root)) && !initial.popups.some(item => item.popup.focused)) {
                const target = focusTarget(root, initial.focusKey || '');
                if (visible(target) && !target.disabled && !target.closest('[inert]')) {
                    target.focus({ preventScroll: true });
                    restoreFieldSelection(target, (initial.selections || []).find(item => item.key === initial.focusKey));
                }
            }
            restoreSurfaceScrolls(scrollers, initial.scrolls);
            initialized = true;
            adapter.registration.restoring = false;
            adapter.registration.capture();
        } catch { if (!adapter.signal.aborted) registry().failures++; }
        finally { restoring = false; }
    };
    let cancelRestoreFrame = null;
    const scheduleRestore = () => {
        if (initialized || restoring || cancelRestoreFrame || adapter.signal.aborted) return;
        cancelRestoreFrame = requestVisualFrame(() => {
            cancelRestoreFrame = requestVisualFrame(() => { cancelRestoreFrame = null; restore(); });
        });
    };
    adapter.signal.addEventListener('abort', () => cancelRestoreFrame?.(), { once: true });
    root.addEventListener('bivium:format-ready', () => { scheduleRestore(); adapter.registration.capture(); }, { signal: adapter.signal });
    root.addEventListener('bivium:surface-change', () => adapter.registration.capture(), { signal: adapter.signal });
    listenSurfaceFields(root, adapter, event => event.type === 'selectionchange' ? root.contains(document.activeElement) && !popupOwns(document.activeElement) : !popupOwns(event.target));
    adapter.registration.observer = new MutationObserver(() => { scheduleRestore(); adapter.registration.capture(); });
    adapter.registration.observer.observe(root, { subtree: true, childList: true });
    adapter.registration.resizeObserver = new ResizeObserver(() => { scheduleRestore(); adapter.registration.capture(); });
    adapter.registration.resizeObserver.observe(root);
    scheduleRestore();
    return true;
}

export function disposeSurface(host) {
    host?.[registrationKey]?.stop();
    if (host) delete host[registrationKey];
}

/** Portal context menu: native Radzen keyboard; the adapter captures position, active item and focus. */
export function installContextSurface(root, reference, generation, initial) {
    const portal = root?.closest('.rz-tooltip'); // Official RadzenContextMenu 11.4.2 markup
    if (!portal) return false;
    const menu = root.querySelector('.rz-menu');
    let active = initial.activeItem || '';
    const item = id => Array.from(menu?.querySelectorAll('.rz-navigation-item[id]') || []).find(element => element.id === id);
    const nativeActive = () => menu?.querySelector('.rz-navigation-item.rz-state-focused[id]:not([data-bivium-restored-focus])')?.id || '';
    const adapter = publisher(root, reference, 'OnContextSurfaceChanged', generation, initial, () => {
        active = nativeActive() || active;
        const rect = portal.getBoundingClientRect();
        return { ...initial, x: rect.left, y: rect.top, viewportWidth: document.documentElement.clientWidth, viewportHeight: document.documentElement.clientHeight, activeItem: active, focused: menu?.contains(document.activeElement) || false };
    });
    function clearRestoredFocus() {
        for (const element of menu?.querySelectorAll('[data-bivium-restored-focus]') || []) {
            element.classList.remove('rz-state-focused');
            element.removeAttribute('data-bivium-restored-focus');
        }
    }
    const capture = () => { if (!registry().freeze) adapter.registration.capture(); };
    menu?.addEventListener('keydown', clearRestoredFocus, { capture: true, signal: adapter.signal });
    menu?.addEventListener('pointerdown', clearRestoredFocus, { capture: true, signal: adapter.signal });
    menu?.addEventListener('focusin', capture, { signal: adapter.signal });
    menu?.addEventListener('focusout', capture, { signal: adapter.signal });
    adapter.registration.observer = new MutationObserver(capture);
    if (menu) adapter.registration.observer.observe(menu, { subtree: true, attributes: true, attributeFilter: ['class'] });
    window.addEventListener('resize', () => adapter.registration.capture(), { signal: adapter.signal });
    // openPopup has already positioned the portal and limited it to the viewport
    requestVisualFrame(() => {
        if (root[registrationKey] !== adapter.registration) return;
        const restored = item(active);
        if (restored && !nativeActive()) {
            restored.classList.add('rz-state-focused');
            restored.setAttribute('data-bivium-restored-focus', '');
        }
        if (initial.focused && !modal() && !registry().freeze) menu?.focus({ preventScroll: true });
        adapter.registration.restoring = false;
        adapter.registration.capture();
    });
    return true;
}
