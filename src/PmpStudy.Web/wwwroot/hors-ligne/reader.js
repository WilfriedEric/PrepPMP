// Lecteur PDF hors ligne : PDF.js sur la copie du cours gardée dans le cache de l'appareil.
let lib = null;
let doc = null;
let loading = null;
let task = null;

export async function open(loadBytes) {
    if (doc) return doc;
    if (!loading) {
        loading = (async () => {
            lib ??= await import('/lib/pdfjs/pdf.min.mjs');
            lib.GlobalWorkerOptions.workerSrc = '/lib/pdfjs/pdf.worker.min.mjs';
            const data = await loadBytes();
            if (!data) throw new Error('Le PDF du cours n\'est pas sur cet appareil.');
            doc = await lib.getDocument({ data }).promise;
            return doc;
        })().catch((e) => { loading = null; throw e; });
    }
    return loading;
}

export function close() {
    task?.cancel();
    doc?.destroy();
    doc = null;
    loading = null;
}

export async function render(canvas, frame, pageNumber, zoom) {
    if (!doc) return;
    task?.cancel();
    const page = await doc.getPage(Math.min(Math.max(pageNumber, 1), doc.numPages));
    const base = page.getViewport({ scale: 1 });
    const width = Math.max(frame.clientWidth - 18, 260);
    const scale = (width / base.width) * zoom;
    const ratio = window.devicePixelRatio || 1;
    const viewport = page.getViewport({ scale: scale * ratio });
    canvas.width = Math.floor(viewport.width);
    canvas.height = Math.floor(viewport.height);
    canvas.style.width = Math.floor(viewport.width / ratio) + 'px';
    canvas.style.height = Math.floor(viewport.height / ratio) + 'px';
    task = page.render({ canvasContext: canvas.getContext('2d'), viewport });
    try { await task.promise; }
    catch (e) { if (e?.name !== 'RenderingCancelledException') throw e; }
    finally { task = null; }
}
