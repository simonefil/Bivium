// Geometry adapter only. Blazor owns every close button and its callback.
const strips = new Map();

export function ownsFocus(wrapper) {
    return !!wrapper?.contains(document.activeElement);
}

export function refresh(wrapper) {
    if (!wrapper?.isConnected) return;
    // A replaced element must not retain observers for its detached predecessor.
    for (const element of strips.keys()) {
        if (!element.isConnected || (element !== wrapper && element.id === wrapper.id)) dispose(element);
    }
    let state = strips.get(wrapper);
    if (!state) {
        state = createStrip(wrapper);
        strips.set(wrapper, state);
    }
    state.schedule();
}

function createStrip(wrapper) {
    let frame = 0;
    let disposed = false;
    let nav = null;
    let observed = new Set();
    const schedule = () => {
        if (!disposed && !frame) frame = requestAnimationFrame(layout);
    };
    const resize = new ResizeObserver(schedule);
    const mutations = new MutationObserver(schedule);

    function layout() {
        frame = 0;
        if (disposed) return;
        if (!wrapper.isConnected) {
            dispose(wrapper);
            return;
        }
        const currentNav = wrapper.querySelector('.rz-tabview-nav');
        if (currentNav !== nav) {
            nav?.removeEventListener('scroll', schedule);
            mutations.disconnect();
            nav = currentNav;
            if (nav) {
                nav.addEventListener('scroll', schedule, { passive: true });
                mutations.observe(nav, { childList: true, subtree: true, characterData: true,
                    attributes: true, attributeFilter: ['class', 'style', 'aria-selected', 'data-terminal-tab-id'] });
            }
        }
        const buttons = [...wrapper.querySelectorAll('[data-terminal-close-id]')];
        const headers = [...(nav?.querySelectorAll('[role="tab"][data-terminal-tab-id]') || [])];
        const nextObserved = new Set([wrapper, ...(nav ? [nav] : []), ...buttons,
            ...headers.map(header => header.closest('li'))].filter(Boolean));
        for (const element of observed) if (!nextObserved.has(element)) resize.unobserve(element);
        for (const element of nextObserved) if (!observed.has(element)) resize.observe(element);
        observed = nextObserved;

        const buttonRects = new Map(buttons.map(button => [button, button.getBoundingClientRect()]));
        const width = Math.max(0, ...[...buttonRects.values()].map(rect => rect.width));
        // Reserve the actual native button width; the existing header gap supplies spacing.
        const space = `${width}px`;
        if (width && wrapper.style.getPropertyValue('--terminal-tab-close-space') !== space) {
            wrapper.style.setProperty('--terminal-tab-close-space', space);
        }
        const layer = wrapper.querySelector('.terminal-tab-close-layer');
        const origin = (layer || wrapper).getBoundingClientRect();
        const viewport = nav?.getBoundingClientRect();
        const byId = new Map(headers.map(header => [header.dataset.terminalTabId, header]));
        // Read all rectangles before positioning: no Blazor-owned nodes are moved.
        const positions = buttons.map(button => {
            const header = byId.get(button.dataset.terminalCloseId);
            const tab = header?.getBoundingClientRect();
            const slot = header?.querySelector('.terminal-tab-close-slot')?.getBoundingClientRect();
            const rect = buttonRects.get(button);
            if (!tab || !slot || !viewport || !rect.width || !origin.width || !origin.height) return { button, visible: false };
            const left = slot.left + (slot.width - rect.width) / 2;
            const top = tab.top + (tab.height - rect.height) / 2;
            const clipLeft = Math.max(origin.left, viewport.left);
            const clipRight = Math.min(origin.right, viewport.left + nav.clientWidth);
            const clipTop = Math.max(origin.top, viewport.top);
            const clipBottom = Math.min(origin.bottom, viewport.top + nav.clientHeight);
            // Hide partial targets too: offscreen tabs cannot leave an invisible hit area.
            const visible = left >= clipLeft && left + rect.width <= clipRight &&
                top >= clipTop && top + rect.height <= clipBottom;
            return { button, visible, left: left - origin.left, top: top - origin.top };
        });
        for (const position of positions) {
            position.button.style.visibility = position.visible ? 'visible' : 'hidden';
            if (position.visible) {
                position.button.style.left = `${position.left}px`;
                position.button.style.top = `${position.top}px`;
            }
        }
    }

    document.fonts?.addEventListener('loadingdone', schedule);
    document.fonts?.ready.then(schedule);
    return { schedule, dispose() {
        if (disposed) return;
        disposed = true;
        if (frame) cancelAnimationFrame(frame);
        frame = 0;
        resize.disconnect();
        mutations.disconnect();
        nav?.removeEventListener('scroll', schedule);
        document.fonts?.removeEventListener('loadingdone', schedule);
        observed.clear();
    } };
}

export function dispose(wrapper) {
    const state = strips.get(wrapper);
    if (!state) return;
    state.dispose();
    strips.delete(wrapper);
}
