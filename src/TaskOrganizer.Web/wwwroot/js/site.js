// Press "/" anywhere outside a form field to jump to the quick-add box.
document.addEventListener('keydown', function (event) {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey) {
        return;
    }
    const tag = event.target.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || event.target.isContentEditable) {
        return;
    }
    const quickTitle = document.getElementById('quick-title');
    if (quickTitle) {
        event.preventDefault();
        quickTitle.focus();
    }
});

// While a time entry is running, keep every time figure on the page current without reloading
// it, so nothing being typed is lost. The server says how often (0 means the clock is not running).
(function () {
    const bar = document.getElementById('capture-bar');
    const seconds = bar ? parseInt(bar.dataset.timeRefreshSeconds, 10) : 0;
    if (!seconds || seconds <= 0) {
        return;
    }

    const timer = setInterval(function () {
        fetch('/api/time/today', { headers: { 'Accept': 'application/json' } })
            .then(function (response) { return response.ok ? response.json() : null; })
            .then(function (time) {
                if (!time) {
                    return;
                }
                document.querySelectorAll('[data-live-time="today"]').forEach(function (el) { el.textContent = time.todayHours; });
                document.querySelectorAll('[data-live-time="week"]').forEach(function (el) { el.textContent = time.weekHours; });
                if (!time.running) {
                    clearInterval(timer);
                }
            })
            .catch(function () { /* The app may be restarting; try again next time. */ });
    }, seconds * 1000);
})();
