export function followOutput(element, smooth = false) {
    if (!element) return;
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    element.scrollTo({ top: element.scrollHeight, behavior: smooth && !reduced ? 'smooth' : 'instant' });
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
