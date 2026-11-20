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
