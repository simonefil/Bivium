// Stateless adapter: changes only the tree scroll, never the focus or the page
export function revealDirectory(host, path) {
    const container = host?.closest(".radzen-panel-tree");
    if (!container || container.clientHeight === 0 || container.clientWidth === 0) return false;

    const label = Array.from(host.querySelectorAll("[data-directory-path]"))
        .find(element => element.dataset.directoryPath === path &&
            element.closest(".rz-treenode-content")?.getAttribute("aria-selected") === "true");
    const row = label?.closest(".rz-treenode-content");
    if (!row || row.closest(".rz-state-collapsed")) return false;

    const bounds = container.getBoundingClientRect();
    const target = row.getBoundingClientRect();
    if (target.top < bounds.top) container.scrollTop += target.top - bounds.top;
    else if (target.bottom > bounds.bottom) container.scrollTop += target.bottom - bounds.bottom;
    if (target.left < bounds.left) container.scrollLeft += target.left - bounds.left;
    else if (target.right > bounds.right) container.scrollLeft += target.right - bounds.right;
    return true;
}
