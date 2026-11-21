export function followOutput(element) {
    if (element) element.scrollTop = element.scrollHeight;
}

const recordingHandlers = new WeakMap();
export function setRecording(element, enabled, callback) {
    if (!element) return;
    const previous = recordingHandlers.get(element);
    if (previous) document.removeEventListener('keydown', previous, true);
    recordingHandlers.delete(element);
    if (!enabled) return;
    const handler = event => {
        if (event.key === 'Escape') {
            event.preventDefault();
            callback.invokeMethodAsync('ExitRecording').catch(() => {});
        }
    };
    recordingHandlers.set(element, handler);
    document.addEventListener('keydown', handler, true);
}
