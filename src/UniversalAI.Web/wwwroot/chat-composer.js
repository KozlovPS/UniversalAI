const handlers = new WeakMap();

export function attach(textarea, callback) {
    if (!textarea || handlers.has(textarea)) return;
    let sending = false;
    const handler = event => {
        if (event.key !== 'Enter' || event.isComposing || event.keyCode === 229 || textarea.disabled) return;
        if (event.ctrlKey && !event.altKey && !event.metaKey) {
            event.preventDefault();
            textarea.setRangeText('\n', textarea.selectionStart, textarea.selectionEnd, 'end');
            textarea.dispatchEvent(new Event('input', { bubbles: true }));
            return;
        }
        if (event.shiftKey || event.altKey || event.metaKey) return;
        event.preventDefault();
        if (sending || event.repeat || !textarea.value.trim()) return;
        sending = true;
        callback.invokeMethodAsync('SendFromKeyboard', textarea.value)
            .catch(() => {})
            .finally(() => {
                sending = false;
                if (textarea.isConnected && !textarea.disabled) textarea.focus();
            });
    };
    textarea.addEventListener('keydown', handler);
    handlers.set(textarea, handler);
}

export function detach(textarea) {
    const handler = textarea && handlers.get(textarea);
    if (!handler) return;
    textarea.removeEventListener('keydown', handler);
    handlers.delete(textarea);
}
