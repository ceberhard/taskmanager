// Backlog page: remember which projects are collapsed, and keyboard movement in mass edit.
(function () {
    const storageKey = 'backlog.collapsedProjects';
    const groups = Array.from(document.querySelectorAll('details.project-group'));

    function readCollapsed() {
        try {
            const parsed = JSON.parse(localStorage.getItem(storageKey) || '[]');
            return Array.isArray(parsed) ? parsed : [];
        } catch (error) {
            return [];
        }
    }

    function saveCollapsed() {
        const collapsed = groups.filter(function (group) { return !group.open; })
            .map(function (group) { return group.dataset.project; });
        // Keep entries for projects hidden by the current filter, so their state is not forgotten.
        const shown = groups.map(function (group) { return group.dataset.project; });
        const hiddenByFilter = readCollapsed().filter(function (id) { return shown.indexOf(id) === -1; });
        try {
            localStorage.setItem(storageKey, JSON.stringify(collapsed.concat(hiddenByFilter)));
        } catch (error) {
            // Storage unavailable: collapsing still works for this page view.
        }
    }

    const collapsedAtLoad = readCollapsed();
    groups.forEach(function (group) {
        if (collapsedAtLoad.indexOf(group.dataset.project) !== -1) {
            group.open = false;
        }
        group.addEventListener('toggle', saveCollapsed);
    });

    document.querySelectorAll('[data-collapse-all]').forEach(function (button) {
        button.addEventListener('click', function () {
            const collapse = button.dataset.collapseAll === 'true';
            groups.forEach(function (group) { group.open = !collapse; });
            saveCollapsed();
        });
    });

    // Mass edit: Ctrl+Up / Ctrl+Down moves to the same column in the previous / next row,
    // opening a collapsed project on the way if needed.
    const massEdit = document.getElementById('mass-edit');
    if (massEdit) {
        massEdit.addEventListener('keydown', function (event) {
            if (!event.ctrlKey || (event.key !== 'ArrowDown' && event.key !== 'ArrowUp')) {
                return;
            }
            const column = event.target.dataset ? event.target.dataset.col : null;
            if (!column) {
                return;
            }
            const cells = Array.from(massEdit.querySelectorAll('[data-col="' + column + '"]'));
            const next = cells[cells.indexOf(event.target) + (event.key === 'ArrowDown' ? 1 : -1)];
            if (next) {
                event.preventDefault();
                const group = next.closest('details.project-group');
                if (group) {
                    group.open = true;
                }
                next.focus();
            }
        });
    }
})();
