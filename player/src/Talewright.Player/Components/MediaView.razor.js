const images = new Map();
let next = 0;

export function observe(image, receiver) {
    const id = ++next;
    const state = { image, url: null, observer: null };
    const request = () => {
        state.observer?.disconnect();
        receiver.invokeMethodAsync("LoadAsync").catch(error => {
            if (images.has(id)) console.error("Image load request failed.", error);
        });
    };
    state.observer = new IntersectionObserver(entries => {
        if (entries.some(entry => entry.isIntersecting)) request();
    }, { rootMargin: "200px" });
    state.observer.observe(image);
    images.set(id, state);
    return id;
}

export async function load(id, stream, mime) {
    const bytes = await stream.arrayBuffer();
    const state = images.get(id);
    if (!state) return false;
    const url = URL.createObjectURL(new Blob([bytes], { type: mime }));
    state.url = url;
    const loaded = new Promise(resolve => {
        state.finish = resolve;
        state.image.onload = () => resolve(true);
        state.image.onerror = () => resolve(false);
    });
    state.image.src = url;
    const success = await loaded;
    state.image.onload = state.image.onerror = null;
    if (!success && images.has(id)) release(id);
    return success;
}

export function release(id) {
    const state = images.get(id);
    if (!state) return;
    images.delete(id);
    state.observer.disconnect();
    state.image.onload = state.image.onerror = null;
    state.image.removeAttribute("src");
    state.finish?.(false);
    if (state.url) URL.revokeObjectURL(state.url);
}
