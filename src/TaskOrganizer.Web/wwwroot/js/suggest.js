// Suggest page: keep the running total in step with the ticked tasks.
(function () {
    const proposal = document.getElementById('proposal');
    const total = document.getElementById('proposal-total');
    if (!proposal || !total) {
        return;
    }

    const available = parseFloat(proposal.dataset.available) || 0;
    const alreadyPlanned = parseFloat(proposal.dataset.already) || 0;

    function formatHours(hours) {
        // Two decimals at most, without trailing zeros: 7.5 h, 7.25 h, 7 h.
        return (Math.round(hours * 100) / 100).toString() + ' h';
    }

    function update() {
        let hours = alreadyPlanned;
        proposal.querySelectorAll('.proposal-pick:checked').forEach(function (box) {
            hours += parseFloat(box.dataset.hours) || 0;
        });
        const over = hours > available + 1e-9;
        total.textContent = formatHours(hours) + ' of ' + formatHours(available)
            + (over ? ' (over by ' + formatHours(hours - available) + ')' : '');
        total.classList.toggle('text-danger', over);
        total.classList.toggle('fw-semibold', over);
    }

    // Step 3 filter: show only rows whose title contains the text, open the projects that
    // have a match, and put the projects back as they were when the box is cleared.
    const filter = document.getElementById('swap-filter');
    const groups = Array.from(proposal.querySelectorAll('details.swap-group'));
    const noMatch = document.getElementById('swap-no-match');
    if (filter) {
        filter.addEventListener('input', function () {
            const text = filter.value.trim().toLowerCase();
            let anyMatch = false;
            groups.forEach(function (group) {
                let matches = 0;
                group.querySelectorAll('li.task-row').forEach(function (row) {
                    const title = row.querySelector('.task-title').textContent.toLowerCase();
                    const show = text === '' || title.indexOf(text) !== -1;
                    row.hidden = !show;
                    if (show) {
                        matches++;
                    }
                });
                group.hidden = matches === 0;
                group.open = text === '' ? group.dataset.initiallyOpen === 'true' : matches > 0;
                anyMatch = anyMatch || matches > 0;
            });
            if (noMatch) {
                noMatch.hidden = anyMatch || groups.length === 0;
            }
        });
    }

    proposal.addEventListener('change', function (event) {
        if (event.target.classList.contains('proposal-pick')) {
            update();
        }
    });
    update();
})();
