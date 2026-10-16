// Mode hors ligne de PMP Study : données locales (IndexedDB) et synchronisation avec le serveur.
// Partagé par l'application hors ligne et par la version en ligne (synchronisation automatique au retour du réseau).
//
// Principe : le serveur fournit un « paquet » (questions, carnet d'erreurs, notes, plan, réglages) que l'appareil
// garde en local. Tout ce qui est fait sans connexion est rangé dans une boîte d'envoi. La synchronisation
// envoie la boîte, puis retélécharge le paquet. Le serveur recorrige les réponses et arbitre les conflits.

const DB_NAME = 'pmpstudy-offline';
const DB_VERSION = 1;
export const DATA_CACHE = 'pmpstudy-data';
export const PDF_URL = '/hors-ligne/cours.pdf';
const PDFJS_FILES = ['/lib/pdfjs/pdf.min.mjs', '/lib/pdfjs/pdf.worker.min.mjs'];
const INTERVALS = [1, 3, 7, 14];
const MASTER_AFTER = 2;
const REFRESH_AFTER_MS = 6 * 3600 * 1000;

export class AuthError extends Error { constructor() { super('Connexion au compte requise.'); this.name = 'AuthError'; } }
export class NetworkError extends Error { constructor(m) { super(m || 'Serveur injoignable.'); this.name = 'NetworkError'; } }

// ---------- IndexedDB ----------

let dbPromise;
export function db() {
    if (!dbPromise) {
        dbPromise = new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                const d = req.result;
                d.createObjectStore('meta');
                d.createObjectStore('ecoTasks', { keyPath: 'code' });
                d.createObjectStore('chapters', { keyPath: 'id' });
                d.createObjectStore('plan', { keyPath: 'date' });
                d.createObjectStore('questions', { keyPath: 'id' });
                d.createObjectStore('errors', { keyPath: 'key' }).createIndex('questionId', 'questionId');
                d.createObjectStore('notes', { keyPath: 'clientId' });
                d.createObjectStore('sessions', { keyPath: 'clientId' });
                d.createObjectStore('outbox', { keyPath: 'key' });
                d.createObjectStore('journal', { keyPath: 'id', autoIncrement: true });
            };
            req.onsuccess = () => {
                const d = req.result;
                d.onversionchange = () => { d.close(); dbPromise = null; };
                resolve(d);
            };
            req.onerror = () => reject(req.error);
            req.onblocked = () => reject(new Error('Base locale bloquée par un autre onglet : ferme-le puis recharge.'));
        });
    }
    return dbPromise;
}

const done = (tx) => new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error || new Error('Transaction annulée.'));
});
const result = (req) => new Promise((resolve, reject) => { req.onsuccess = () => resolve(req.result); req.onerror = () => reject(req.error); });

async function tx(stores, mode, fn) {
    const d = await db();
    const t = d.transaction(stores, mode);
    const out = fn(t);
    await done(t);
    return out instanceof Promise ? await out : out;
}

export async function all(store) { return tx([store], 'readonly', (t) => result(t.objectStore(store).getAll())); }
export async function get(store, key) { return tx([store], 'readonly', (t) => result(t.objectStore(store).get(key))); }
export async function put(store, value, key) { await tx([store], 'readwrite', (t) => { t.objectStore(store).put(value, key); }); notify(); }
export async function count(store) { return tx([store], 'readonly', (t) => result(t.objectStore(store).count())); }
export const meta = (key) => get('meta', key);
export const setMeta = (key, value) => put('meta', value, key);

// ---------- Notifications entre onglets ----------

const channel = 'BroadcastChannel' in self ? new BroadcastChannel('pmp-offline') : null;
const listeners = new Set();
export function onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); }
function notify(local = true) {
    for (const fn of listeners) { try { fn(); } catch (e) { console.error(e); } }
    if (local && channel) channel.postMessage('change');
}
if (channel) channel.onmessage = () => notify(false);

// ---------- Aides ----------

export function uuid() {
    if (self.crypto?.randomUUID) return crypto.randomUUID();
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
        const r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}

const nowIso = () => new Date().toISOString();

/** Date du jour « AAAA-MM-JJ » dans le fuseau de l'utilisateur (celui réglé sur le serveur). */
export function todayIn(timeZone, at = new Date()) {
    try {
        return new Intl.DateTimeFormat('en-CA', { timeZone: timeZone || 'UTC', year: 'numeric', month: '2-digit', day: '2-digit' }).format(at);
    } catch {
        return at.toISOString().slice(0, 10);
    }
}

export function addDays(isoDay, days) {
    const d = new Date(isoDay + 'T00:00:00Z');
    d.setUTCDate(d.getUTCDate() + days);
    return d.toISOString().slice(0, 10);
}

export function daysBetween(fromIso, toIso) {
    return Math.round((new Date(toIso + 'T00:00:00Z') - new Date(fromIso + 'T00:00:00Z')) / 86400000);
}

export async function timeZone() { return (await meta('pack'))?.timeZone || 'Africa/Abidjan'; }
export async function today() { return todayIn(await timeZone()); }

export async function deviceId() {
    let id = await meta('deviceId');
    if (!id) { id = uuid(); await setMeta('deviceId', id); }
    return id;
}

export async function deviceName() {
    const name = await meta('deviceName');
    if (name) return name;
    const ua = navigator.userAgent;
    const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android'
        : /Windows/.test(ua) ? 'Windows' : /Mac OS X/.test(ua) ? 'Mac' : /Linux/.test(ua) ? 'Linux' : 'Appareil';
    const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'navigateur';
    return `${os} · ${browser}`;
}

// ---------- Réseau ----------

async function api(path, options = {}) {
    let res;
    try {
        res = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...options, headers: { 'Accept': 'application/json', ...(options.headers || {}) } });
    } catch {
        throw new NetworkError();
    }
    if (res.status === 401 || res.status === 403) throw new AuthError();
    if (!res.ok) {
        const text = await res.text().catch(() => '');
        throw new Error(text.replace(/^"|"$/g, '') || `Erreur serveur (${res.status}).`);
    }
    return res;
}

/** « ok », « auth » (connexion requise), « offline » ou « error ». */
export async function ping() {
    if (!navigator.onLine) return 'offline';
    try { await api('/api/offline/ping'); return 'ok'; }
    catch (e) { return e instanceof AuthError ? 'auth' : e instanceof NetworkError ? 'offline' : 'error'; }
}

// ---------- Paquet ----------

export async function hasPack() { return !!(await meta('pack')); }

export async function pull() {
    const id = await deviceId();
    const name = await deviceName();
    const res = await api(`/api/offline/pack?deviceId=${encodeURIComponent(id)}&name=${encodeURIComponent(name)}`);
    const text = await res.text();
    const pack = JSON.parse(text);
    if (await count('outbox') > 0) throw new Error('Des changements locaux attendent l\'envoi : synchronise d\'abord.');

    const errors = pack.errors.map(serverError);
    const notes = pack.notes.map((n) => ({
        clientId: n.clientId || `server-${n.id}`, id: n.id, page: n.page, content: n.content, tags: n.tags,
        chapterId: n.chapterId, ecoTask: n.ecoTask, createdUtc: n.createdUtc, updatedUtc: n.updatedUtc, serverUpdatedUtc: n.updatedUtc,
    }));
    const stores = ['ecoTasks', 'chapters', 'plan', 'questions', 'errors', 'notes', 'meta'];
    await tx(stores, 'readwrite', (t) => {
        const fill = (name, rows) => { const s = t.objectStore(name); s.clear(); rows.forEach((r) => s.put(r)); };
        fill('ecoTasks', pack.ecoTasks);
        fill('chapters', pack.chapters);
        fill('plan', pack.plan);
        fill('questions', pack.questions);
        fill('errors', errors);
        fill('notes', notes);
        const m = t.objectStore('meta');
        m.put({
            version: pack.version, generatedUtc: pack.generatedUtc, downloadedUtc: nowIso(), serverToday: pack.today, timeZone: pack.timeZone,
            examDate: pack.examDate, simulationDates: pack.simulationDates, pageOffset: pack.pageOffset, pdf: pack.pdf, bytes: text.length,
            counts: { questions: pack.questions.length, errors: errors.length, notes: notes.length, plan: pack.plan.length },
        }, 'pack');
        m.put(pack.seenQuestionIds, 'seen');
        if (pack.lastPage) m.put(pack.lastPage, 'serverLastPage');
    });
    await pruneSessions();
    notify();
    return pack;
}

function serverError(e) {
    return {
        key: 'e' + e.id, id: e.id, questionId: e.questionId, ecoTask: e.ecoTask, summary: e.summary, why: e.why, trap: e.trap,
        mindsetReflex: e.mindsetReflex, status: e.status, origin: e.origin, externalSource: e.externalSource, nextReview: e.nextReview,
        intervalStep: e.intervalStep, consecutiveCorrect: e.consecutiveCorrect, reviewCount: e.reviewCount, timesWrong: e.timesWrong,
        updatedUtc: e.updatedUtc,
    };
}

/** Garde l'historique local des 40 dernières sessions déjà envoyées. */
async function pruneSessions() {
    const sessions = await all('sessions');
    const synced = sessions.filter((s) => s.syncedUtc).sort((a, b) => (b.endUtc || '').localeCompare(a.endUtc || ''));
    const extra = synced.slice(40);
    if (extra.length === 0) return;
    await tx(['sessions'], 'readwrite', (t) => extra.forEach((s) => t.objectStore('sessions').delete(s.clientId)));
}

// ---------- Boîte d'envoi ----------

async function queue(op) {
    await tx(['outbox'], 'readwrite', (t) => {
        const s = t.objectStore('outbox');
        const req = s.get(op.key);
        req.onsuccess = () => {
            const prev = req.result;
            // Plusieurs modifications d'un même élément : une seule opération, qui garde l'état de départ (fusion côté serveur).
            s.put(prev ? { ...prev, ...op, base: prev.base ?? op.base, createdUtc: prev.createdUtc } : { ...op, createdUtc: nowIso() });
        };
    });
}

export async function pending() {
    const ops = await all('outbox');
    return ops.sort((a, b) => a.createdUtc.localeCompare(b.createdUtc));
}

/** Construit l'envoi à partir de la boîte et de l'état local courant. */
async function buildPush() {
    const ops = await pending();
    const push = { deviceId: await deviceId(), deviceName: await deviceName(), sessions: [], notes: [], errorEdits: [], errorReviews: [], readings: [], lastPage: null };
    for (const op of ops) {
        if (op.kind === 'session') {
            const s = await get('sessions', op.clientId);
            if (!s) continue;
            push.sessions.push({
                clientId: s.clientId, type: s.type, title: s.title, feedback: s.feedback, startUtc: s.startUtc, endUtc: s.endUtc,
                durationSeconds: s.durationSeconds,
                items: s.items.map((i) => ({ questionId: i.questionId, answer: i.answer, answeredUtc: i.answeredUtc, timeSeconds: Math.round(i.timeSeconds || 0), flagged: !!i.flagged, doubt: !!i.doubt })),
            });
        } else if (op.kind === 'note') {
            const n = await get('notes', op.clientId);
            if (!n) continue;
            push.notes.push({
                clientId: n.clientId, id: n.id ?? null, page: n.page ?? null, content: n.content, tags: n.tags, chapterId: n.chapterId ?? null,
                ecoTask: n.ecoTask ?? null, deleted: !!n.deleted, baseUpdatedUtc: n.serverUpdatedUtc ?? null, updatedUtc: n.updatedUtc,
            });
        } else if (op.kind === 'errorEdit') {
            const e = await get('errors', op.errorKey);
            if (!e || !e.id) continue;
            push.errorEdits.push({
                id: e.id, why: e.why, trap: e.trap, mindsetReflex: e.mindsetReflex,
                baseWhy: op.base?.why ?? null, baseTrap: op.base?.trap ?? null, baseMindsetReflex: op.base?.mindsetReflex ?? null, updatedUtc: op.updatedUtc,
            });
        } else if (op.kind === 'errorReview') {
            push.errorReviews.push({ clientId: op.clientId, id: op.errorId, knewIt: op.knewIt, atUtc: op.atUtc });
        } else if (op.kind === 'reading') {
            push.readings.push(op.reading);
        } else if (op.kind === 'lastPage') {
            push.lastPage = op.page;
        }
    }
    return { push, ops };
}

export async function push() {
    const { push: body, ops } = await buildPush();
    if (ops.length === 0) return null;
    const res = await api('/api/offline/sync', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const r = await res.json();
    const stamp = nowIso();
    const ids = new Map((r.noteIds || []).map((m) => [m.clientId, m]));
    await tx(['outbox', 'notes', 'sessions'], 'readwrite', (t) => {
        const outbox = t.objectStore('outbox');
        const notes = t.objectStore('notes');
        const sessions = t.objectStore('sessions');
        for (const op of ops) {
            // Une opération modifiée pendant l'envoi reste dans la boîte (sa date a changé).
            const req = outbox.get(op.key);
            req.onsuccess = () => { if (req.result && req.result.updatedUtc === op.updatedUtc) outbox.delete(op.key); };
            if (op.kind === 'session') {
                const sr = sessions.get(op.clientId);
                sr.onsuccess = () => { if (sr.result) sessions.put({ ...sr.result, syncedUtc: stamp }); };
            }
            if (op.kind === 'note') {
                const nr = notes.get(op.clientId);
                nr.onsuccess = () => {
                    const n = nr.result;
                    if (!n) return;
                    if (n.deleted) notes.delete(n.clientId);
                    else {
                        const m = ids.get(n.clientId);
                        notes.put(m ? { ...n, id: m.id, serverUpdatedUtc: m.updatedUtc } : n);
                    }
                };
            }
        }
    });
    notify();
    return r;
}

let running = null;

/**
 * Envoie les changements puis met à jour les données locales. Un seul passage à la fois, y compris entre onglets.
 * Retourne l'entrée de journal créée.
 */
export async function sync({ pushOnly = false, reason = 'manuel' } = {}) {
    if (running) return running;
    const work = async () => {
        const entry = { atUtc: nowIso(), reason, ok: false, sent: null, pulled: false, conflicts: [], warnings: [], error: null };
        try {
            const r = await push();
            if (r) {
                entry.sent = summarize(r);
                entry.conflicts = r.conflicts || [];
                entry.warnings = r.warnings || [];
            }
            if (!pushOnly) {
                if (await count('outbox') === 0) { await pull(); entry.pulled = true; }
                else entry.warnings.push('Des changements ont été faits pendant l\'envoi : relance la synchronisation pour les envoyer.');
            }
            entry.ok = true;
        } catch (e) {
            entry.error = e.message;
            entry.auth = e instanceof AuthError;
            entry.offline = e instanceof NetworkError;
        }
        await tx(['journal', 'meta'], 'readwrite', (t) => {
            const j = t.objectStore('journal');
            j.add(entry);
            // Journal limité aux 30 derniers passages.
            const keys = j.getAllKeys();
            keys.onsuccess = () => keys.result.slice(0, Math.max(0, keys.result.length - 30)).forEach((k) => j.delete(k));
            t.objectStore('meta').put(entry, 'lastSync');
        });
        notify();
        return entry;
    };
    running = (navigator.locks ? navigator.locks.request('pmp-offline-sync', work) : work()).finally(() => { running = null; });
    return running;
}

function summarize(r) {
    const parts = [];
    if (r.sessionsImported) parts.push(`${r.sessionsImported} session${r.sessionsImported > 1 ? 's' : ''}`);
    const notes = (r.notesCreated || 0) + (r.notesUpdated || 0) + (r.notesDeleted || 0);
    if (notes) parts.push(`${notes} note${notes > 1 ? 's' : ''}`);
    const errors = (r.errorsUpdated || 0) + (r.errorReviews || 0);
    if (errors) parts.push(`${errors} mise${errors > 1 ? 's' : ''} à jour du carnet`);
    if (r.readings) parts.push(`${r.readings} séance${r.readings > 1 ? 's' : ''} de lecture`);
    return parts.length ? parts.join(', ') : 'rien de nouveau (déjà reçu)';
}

export async function journal() {
    const rows = await all('journal');
    return rows.reverse();
}

/** Synchronisation automatique : envoie s'il y a des changements, rafraîchit un paquet ancien. Silencieux si rien à faire. */
export async function autoSync(reason = 'automatique') {
    if (!navigator.onLine || !(await hasPack())) return null;
    if ((await meta('autoSync')) === false) return null;
    const waiting = await count('outbox');
    const pack = await meta('pack');
    const stale = !pack || Date.now() - new Date(pack.downloadedUtc).getTime() > REFRESH_AFTER_MS;
    if (waiting === 0 && !stale) return null;
    return sync({ reason });
}

// ---------- PDF du cours ----------

export async function pdfInfo() { return meta('pdf'); }

export async function downloadPdf(onProgress) {
    const res = await api('/api/course/pdf', { headers: { Accept: 'application/pdf' } });
    const total = Number(res.headers.get('Content-Length')) || (await meta('pack'))?.pdf?.length || 0;
    const reader = res.body.getReader();
    const chunks = [];
    let received = 0;
    for (;;) {
        const { done: end, value } = await reader.read();
        if (end) break;
        chunks.push(value);
        received += value.length;
        onProgress?.(received, total);
    }
    const blob = new Blob(chunks, { type: 'application/pdf' });
    const cache = await caches.open(DATA_CACHE);
    await cache.put(PDF_URL, new Response(blob, { headers: { 'Content-Type': 'application/pdf', 'Content-Length': String(blob.size) } }));
    // Le lecteur hors ligne a aussi besoin de PDF.js.
    await cache.addAll(PDFJS_FILES);
    if (navigator.storage?.persist) await navigator.storage.persist().catch(() => false);
    const pack = await meta('pack');
    await setMeta('pdf', { bytes: blob.size, cachedUtc: nowIso(), uploadedUtc: pack?.pdf?.uploadedUtc ?? null, pageCount: pack?.pdf?.pageCount ?? null });
    return blob.size;
}

export async function removePdf() {
    const cache = await caches.open(DATA_CACHE);
    await cache.delete(PDF_URL);
    await tx(['meta'], 'readwrite', (t) => t.objectStore('meta').delete('pdf'));
    notify();
}

export async function loadPdfBytes() {
    const cache = await caches.open(DATA_CACHE);
    const res = await cache.match(PDF_URL);
    return res ? new Uint8Array(await res.arrayBuffer()) : null;
}

// ---------- Effacement ----------

export async function wipe() {
    const d = await db();
    d.close();
    dbPromise = null;
    await new Promise((resolve, reject) => {
        const req = indexedDB.deleteDatabase(DB_NAME);
        req.onsuccess = resolve;
        req.onerror = () => reject(req.error);
        req.onblocked = resolve;
    });
    await caches.delete(DATA_CACHE);
    notify();
}

// ---------- Notes ----------

export async function saveNote(note) {
    const now = nowIso();
    const n = { ...note, clientId: note.clientId || uuid(), updatedUtc: now, createdUtc: note.createdUtc || now, deleted: false };
    await put('notes', n);
    await queue({ key: 'note:' + n.clientId, kind: 'note', clientId: n.clientId, updatedUtc: now });
    return n;
}

export async function deleteNote(clientId) {
    const n = await get('notes', clientId);
    if (!n) return;
    const now = nowIso();
    if (!n.id) {
        // Jamais envoyée : on l'oublie simplement.
        await tx(['notes', 'outbox'], 'readwrite', (t) => { t.objectStore('notes').delete(clientId); t.objectStore('outbox').delete('note:' + clientId); });
        notify();
        return;
    }
    await put('notes', { ...n, deleted: true, updatedUtc: now });
    await queue({ key: 'note:' + clientId, kind: 'note', clientId, updatedUtc: now });
}

// ---------- Carnet d'erreurs ----------

export function recordMiss(e, day, isReview) {
    if (isReview) e.reviewCount++;
    e.timesWrong++;
    e.intervalStep = 0;
    e.consecutiveCorrect = 0;
    e.status = 'Active';
    e.nextReview = addDays(day, INTERVALS[0]);
}

export function recordSuccess(e, day) {
    e.reviewCount++;
    e.consecutiveCorrect++;
    if (e.consecutiveCorrect >= MASTER_AFTER) { e.status = 'Mastered'; return; }
    e.intervalStep = Math.min(e.intervalStep + 1, INTERVALS.length - 1);
    e.nextReview = addDays(day, INTERVALS[e.intervalStep]);
}

export async function saveErrorAnalysis(key, fields) {
    const e = await get('errors', key);
    if (!e) return;
    if (!e.id) throw new Error('Cette erreur vient d\'une session hors ligne : son analyse sera possible après la synchronisation.');
    const base = { why: e.why ?? null, trap: e.trap ?? null, mindsetReflex: e.mindsetReflex ?? null };
    const now = nowIso();
    await put('errors', { ...e, ...fields, localEditUtc: now });
    await queue({ key: 'errorEdit:' + e.id, kind: 'errorEdit', errorKey: key, base, updatedUtc: now });
}

/** Revue d'une erreur externe (sans question à rejouer) : « je savais » ou « je ne savais pas ». */
export async function reviewExternal(key, knewIt) {
    const e = await get('errors', key);
    if (!e || !e.id) return;
    const day = await today();
    if (knewIt) recordSuccess(e, day); else recordMiss(e, day, true);
    await put('errors', e);
    const clientId = uuid();
    await queue({ key: 'errorReview:' + clientId, kind: 'errorReview', clientId, errorId: e.id, knewIt, atUtc: nowIso(), updatedUtc: nowIso() });
}

// ---------- Sessions ----------

export function isCorrect(question, answer) {
    if (!answer || answer.length === 0) return false;
    const correct = question.options.filter((o) => o.correct).map((o) => o.order).sort().join(',');
    return [...new Set(answer)].sort().join(',') === correct;
}

export async function saveSession(session) { await put('sessions', session); }

/** Clôture une session : correction, carnet d'erreurs local, boîte d'envoi. */
export async function finishSession(clientId) {
    const s = await get('sessions', clientId);
    if (!s || s.endUtc) return s;
    const now = new Date();
    const deadline = new Date(s.deadlineUtc);
    s.endUtc = (now < deadline ? now : deadline).toISOString();
    const questions = new Map((await Promise.all(s.items.map((i) => get('questions', i.questionId)))).filter(Boolean).map((q) => [q.id, q]));
    for (const item of s.items) {
        const q = questions.get(item.questionId);
        item.isCorrect = q ? isCorrect(q, item.answer) : false;
    }
    s.correctCount = s.items.filter((i) => i.isCorrect).length;
    s.scorePercent = s.items.length ? Math.round(1000 * s.correctCount / s.items.length) / 10 : 0;

    const day = todayIn(await timeZone(), new Date(s.endUtc));
    const seen = new Set((await meta('seen')) || []);
    await tx(['sessions', 'errors', 'outbox', 'meta'], 'readwrite', (t) => {
        const errors = t.objectStore('errors');
        const byQuestion = errors.index('questionId');
        for (const item of s.items) {
            if (item.answer?.length) seen.add(item.questionId);
            const miss = !item.isCorrect || item.doubt;
            const q = questions.get(item.questionId);
            const req = byQuestion.get(item.questionId);
            req.onsuccess = () => {
                const e = req.result;
                if (e) {
                    if (miss) recordMiss(e, day, true);
                    else if (e.status === 'Active') recordSuccess(e, day);
                    else return;
                    errors.put(e);
                } else if (miss) {
                    errors.put({
                        key: 'q' + item.questionId, id: null, questionId: item.questionId, ecoTask: q?.ecoTask ?? null, status: 'Active', origin: 'App',
                        nextReview: addDays(day, INTERVALS[0]), intervalStep: 0, consecutiveCorrect: 0, reviewCount: 0, timesWrong: 1, local: true,
                    });
                }
            };
        }
        t.objectStore('sessions').put(s);
        t.objectStore('outbox').put({ key: 'session:' + s.clientId, kind: 'session', clientId: s.clientId, updatedUtc: s.endUtc, createdUtc: nowIso() });
        t.objectStore('meta').put([...seen], 'seen');
    });
    notify();
    return s;
}

// ---------- Lecture ----------

export async function trackReading(reading) {
    await queue({ key: 'reading:' + reading.clientId, kind: 'reading', reading, updatedUtc: reading.updatedUtc });
}

export async function setLastPage(page) {
    await setMeta('lastPage', page);
    await queue({ key: 'lastPage', kind: 'lastPage', page, updatedUtc: nowIso() });
}

// ---------- Description des changements en attente ----------

export async function describePending() {
    const ops = await pending();
    const out = [];
    for (const op of ops) {
        if (op.kind === 'session') {
            const s = await get('sessions', op.clientId);
            if (s) out.push({ kind: 'Session', text: `${s.title} · ${s.correctCount ?? 0}/${s.items.length} justes`, at: s.endUtc });
        } else if (op.kind === 'note') {
            const n = await get('notes', op.clientId);
            if (n) out.push({ kind: 'Note', text: `${n.deleted ? 'Suppression' : n.id ? 'Modification' : 'Nouvelle note'}${n.page ? ` · page ${n.page}` : ''} · ${short(n.content)}`, at: n.updatedUtc });
        } else if (op.kind === 'errorEdit') {
            const e = await get('errors', op.errorKey);
            out.push({ kind: 'Carnet', text: `Analyse d'erreur${e?.ecoTask ? ` · ${e.ecoTask}` : ''}`, at: op.updatedUtc });
        } else if (op.kind === 'errorReview') {
            out.push({ kind: 'Carnet', text: `Revue d'une erreur externe · ${op.knewIt ? 'je savais' : 'à revoir'}`, at: op.atUtc });
        } else if (op.kind === 'reading') {
            const r = op.reading;
            out.push({ kind: 'Lecture', text: `Pages ${r.pageFrom} à ${r.pageTo} · ${Math.round(r.minutes)} min`, at: r.updatedUtc });
        } else if (op.kind === 'lastPage') {
            out.push({ kind: 'Lecture', text: `Dernière page lue : ${op.page}`, at: op.updatedUtc });
        }
    }
    return out;
}

function short(text) {
    const t = (text || '').replace(/\s+/g, ' ').trim();
    return t.length > 60 ? t.slice(0, 57) + '…' : t || '(vide)';
}
