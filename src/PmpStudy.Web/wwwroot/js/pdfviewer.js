// Lecteur PDF : PDF.js charge le document par requêtes partielles (Range) et affiche une page à la fois.
let pdfjsLib;
const viewers = new Map();

async function lib() {
    if (!pdfjsLib) {
        pdfjsLib = await import('/lib/pdfjs/pdf.min.mjs');
        pdfjsLib.GlobalWorkerOptions.workerSrc = '/lib/pdfjs/pdf.worker.min.mjs';
    }
    return pdfjsLib;
}

export async function open(container, url, startPage, dotnet) {
    const pdf = await lib();
    const task = pdf.getDocument({
        url,
        withCredentials: true,
        disableAutoFetch: true,
        disableStream: true,
        rangeChunkSize: 262144,
    });
    const doc = await task.promise;
    const canvas = document.createElement('canvas');
    canvas.className = 'pdf-canvas';
    container.replaceChildren(canvas);
    const state = { doc, canvas, container, dotnet, page: 1, zoom: 1, renderTask: null, pending: null, busy: false };
    viewers.set(container, state);

    state.onResize = debounce(() => render(state, state.page), 200);
    window.addEventListener('resize', state.onResize);
    state.onKey = (e) => {
        if (e.target.closest('input, textarea, [contenteditable="true"]')) return;
        if (e.key === 'ArrowRight' || e.key === 'PageDown') { goTo(container, state.page + 1); e.preventDefault(); }
        if (e.key === 'ArrowLeft' || e.key === 'PageUp') { goTo(container, state.page - 1); e.preventDefault(); }
    };
    document.addEventListener('keydown', state.onKey);

    await render(state, clamp(startPage, 1, doc.numPages));
    return doc.numPages;
}

export async function goTo(container, page) {
    const state = viewers.get(container);
    if (!state) return;
    await render(state, clamp(page, 1, state.doc.numPages));
}

export async function zoom(container, factor) {
    const state = viewers.get(container);
    if (!state) return;
    state.zoom = factor === 0 ? 1 : clamp(state.zoom * factor, 0.5, 3);
    await render(state, state.page);
}

export function dispose(container) {
    const state = viewers.get(container);
    if (!state) return;
    window.removeEventListener('resize', state.onResize);
    document.removeEventListener('keydown', state.onKey);
    state.doc.destroy();
    viewers.delete(container);
}

async function render(state, pageNumber) {
    // Une seule page à la fois : si un rendu est en cours, on garde la dernière demande.
    if (state.busy) {
        state.pending = pageNumber;
        state.renderTask?.cancel();
        return;
    }
    state.busy = true;
    const changed = pageNumber !== state.page;
    state.page = pageNumber;
    try {
        const page = await state.doc.getPage(pageNumber);
        const base = page.getViewport({ scale: 1 });
        const width = Math.max(state.container.clientWidth - 2, 280);
        const scale = (width / base.width) * state.zoom;
        const ratio = window.devicePixelRatio || 1;
        const viewport = page.getViewport({ scale: scale * ratio });
        state.canvas.width = Math.floor(viewport.width);
        state.canvas.height = Math.floor(viewport.height);
        state.canvas.style.width = Math.floor(viewport.width / ratio) + 'px';
        state.canvas.style.height = Math.floor(viewport.height / ratio) + 'px';
        state.renderTask = page.render({ canvasContext: state.canvas.getContext('2d'), viewport });
        await state.renderTask.promise;
    } catch (e) {
        if (e?.name !== 'RenderingCancelledException') console.error(e);
    } finally {
        state.renderTask = null;
        state.busy = false;
    }
    if (state.pending !== null) {
        const next = state.pending;
        state.pending = null;
        await render(state, next);
        return;
    }
    if (changed) state.container.scrollTop = 0;
    await state.dotnet.invokeMethodAsync('OnPageRendered', state.page);
}

function clamp(v, min, max) { return Math.min(Math.max(v, min), max); }
function debounce(fn, ms) { let t; return () => { clearTimeout(t); t = setTimeout(fn, ms); }; }
