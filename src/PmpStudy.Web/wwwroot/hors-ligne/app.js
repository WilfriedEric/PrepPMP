// Application hors ligne de PMP Study : fonctionne sans réseau à partir des données gardées sur l'appareil.
import * as S from '/hors-ligne/store.js';
import { icon } from '/hors-ligne/icons.js';

// ---------- Aides ----------

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const view = () => $('#view');
const LETTERS = 'ABCDEFGHIJ';
const DOMAINS = { People: 'Personnes', Process: 'Processus', BusinessEnvironment: "Environnement d'affaires" };
const APPROACHES = { Predictive: 'Prédictif', Agile: 'Agile', Hybrid: 'Hybride' };
const SECONDS_PER_QUESTION = 80;

/** Les dates du serveur arrivent parfois sans « Z » : ce sont toujours des dates UTC. */
const parseUtc = (s) => (s ? new Date(/[zZ]|[+-]\d\d:\d\d$/.test(s) ? s : s + 'Z') : null);

let tz = 'Africa/Abidjan';
const fmt = (opts) => new Intl.DateTimeFormat('fr-FR', { timeZone: tz, ...opts });
const fmtDateTime = (iso) => (iso ? fmt({ day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }).format(parseUtc(iso)).replace(' ', ' à ') : '');
const fmtDay = (isoDay, opts = { weekday: 'long', day: 'numeric', month: 'long' }) =>
    new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', ...opts }).format(new Date(isoDay + 'T00:00:00Z'));
const fmtDuration = (seconds) => {
    const s = Math.max(0, Math.round(seconds));
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
    return h ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
};
const fmtBytes = (b) => (b > 1048576 ? `${(b / 1048576).toFixed(1).replace('.', ',')} Mo` : `${Math.max(1, Math.round(b / 1024))} Ko`);
const ago = (iso) => {
    const d = parseUtc(iso);
    if (!d) return 'jamais';
    const min = Math.round((Date.now() - d.getTime()) / 60000);
    if (min < 1) return "à l'instant";
    if (min < 60) return `il y a ${min} min`;
    if (min < 1440) return `il y a ${Math.round(min / 60)} h`;
    return `le ${fmtDateTime(iso)}`;
};
const plural = (n, one, many) => `${n} ${n > 1 ? many : one}`;
const shuffle = (arr) => { for (let i = arr.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [arr[i], arr[j]] = [arr[j], arr[i]]; } return arr; };
const levelOf = (p) => (p >= 80 ? ['ok', 'Au-dessus de la cible'] : p >= 70 ? ['accent', 'Cible'] : p >= 60 ? ['warn', 'Sous la cible'] : ['bad', 'À renforcer']);

function toast(message, kind = '') {
    const el = document.createElement('div');
    el.className = `toast ${kind}`;
    el.textContent = message;
    $('#toasts').append(el);
    setTimeout(() => el.remove(), 4200);
}

function modal(html, bind) {
    const dlg = $('#modal');
    dlg.innerHTML = html;
    bind?.(dlg);
    dlg.showModal();
    return dlg;
}

function confirmBox(message, ok = 'Confirmer', danger = false) {
    return new Promise((resolve) => {
        const dlg = modal(`<h2>${esc(message)}</h2>
            <div class="row" style="justify-content:flex-end;margin-top:18px">
              <button class="btn ghost" data-r="0">Annuler</button>
              <button class="btn ${danger ? 'danger' : 'primary'}" data-r="1">${esc(ok)}</button>
            </div>`);
        dlg.onclick = (e) => {
            const r = e.target.closest('[data-r]')?.dataset.r;
            if (r !== undefined) { dlg.close(); resolve(r === '1'); }
        };
        dlg.oncancel = () => resolve(false);
    });
}

// ---------- État global ----------

const state = { net: navigator.onLine ? 'checking' : 'offline', pending: 0, due: 0 };
let cleanup = null; // nettoyage de la vue courante (minuteurs, écouteurs)

const NAV = [
    ['', "Aujourd'hui", 'today'],
    ['entrainement', "S'entraîner", 'school'],
    ['erreurs', 'Erreurs', 'errorOutline'],
    ['lecture', 'Lecture', 'menuBook'],
    ['notes', 'Notes', 'stickyNote2'],
    ['synchro', 'Synchro', 'sync'],
];

function renderNav(route) {
    const section = route.split('/')[0];
    const current = section === 'session' || section === 'resultats' ? 'entrainement' : section;
    const badge = (key) => (key === 'synchro' && state.pending ? `<span class="count">${state.pending}</span>`
        : key === 'erreurs' && state.due ? `<span class="count">${state.due}</span>` : '');
    const links = NAV.map(([key, label, ico]) =>
        `<a href="#/${key}" ${key === current ? 'aria-current="page"' : ''}>${icon(ico)}<span>${label}</span>${badge(key)}</a>`).join('');
    $('#nav').innerHTML = links;
    $('#tabbar').innerHTML = links;
}

function renderStatus() {
    const pill = $('#net-pill');
    const text = { ok: 'En ligne', auth: 'Connexion requise', offline: navigator.onLine ? 'Serveur injoignable' : 'Hors ligne', error: 'Serveur en erreur', checking: 'Vérification…' }[state.net];
    pill.className = `pill ${state.net === 'ok' ? 'on' : state.net === 'checking' ? '' : 'off'}`;
    $('#net-text').textContent = text;
    const pending = $('#pending-pill');
    pending.classList.toggle('hidden', state.pending === 0);
    pending.textContent = `${state.pending} en attente`;
}

async function refreshCounts() {
    state.pending = await S.count('outbox');
    const day = await S.today();
    state.due = (await S.all('errors')).filter((e) => e.status === 'Active' && e.nextReview <= day).length;
    renderStatus();
    renderNav(currentRoute());
}

async function checkNetwork() {
    const before = state.net;
    if (before !== 'ok') { state.net = navigator.onLine ? 'checking' : 'offline'; renderStatus(); }
    state.net = await S.ping();
    renderStatus();
    // Serveur de nouveau joignable (sans événement « online » : Wi-Fi sans Internet, serveur redémarré…).
    if (state.net === 'ok' && before !== 'ok' && before !== 'checking') {
        await autoSync('retour du réseau');
        if (currentRoute().startsWith('synchro')) route();
    }
    return state.net;
}

async function autoSync(reason) {
    if (state.net !== 'ok') return;
    const entry = await S.autoSync(reason);
    if (!entry) return;
    if (entry.ok && entry.sent) toast(`Synchronisé : ${entry.sent}.`);
    else if (!entry.ok && entry.auth) toast('Synchronisation en attente : reconnecte-toi à ton compte.', 'error');
    else if (!entry.ok) toast(`Synchronisation impossible : ${entry.error}`, 'error');
    if (entry.conflicts?.length) toast(`${plural(entry.conflicts.length, 'conflit résolu', 'conflits résolus')} : voir le journal dans Synchro.`);
}

// ---------- Routeur ----------

const currentRoute = () => decodeURIComponent(location.hash.replace(/^#\/?/, ''));

async function route() {
    cleanup?.();
    cleanup = null;
    $('#modal').open && $('#modal').close();
    const r = currentRoute();
    renderNav(r);
    const [section, arg] = r.split('/');
    const titles = { '': "Aujourd'hui", entrainement: 'Entraînement', session: 'Session', resultats: 'Résultats', erreurs: "Carnet d'erreurs", lecture: 'Lecture', notes: 'Notes', synchro: 'Synchronisation' };
    $('#title').textContent = titles[section] ?? 'PMP Study';
    document.title = `${titles[section] ?? 'PMP Study'} · PMP Study hors ligne`;
    const hasPack = await S.hasPack();
    tz = await S.timeZone();
    try {
        if (!hasPack && section !== 'synchro') await renderWelcome();
        else if (section === '') await renderToday();
        else if (section === 'entrainement') await renderTraining();
        else if (section === 'session') await renderSession(arg);
        else if (section === 'resultats') await renderResults(arg);
        else if (section === 'erreurs') await renderErrors(arg);
        else if (section === 'lecture') await renderReader(Number(arg) || null);
        else if (section === 'notes') await renderNotes();
        else if (section === 'synchro') await renderSync();
        else location.hash = '#/';
    } catch (e) {
        console.error(e);
        view().innerHTML = `<div class="notice error">Erreur d'affichage : ${esc(e.message)}</div>`;
    }
    view().classList.remove('fade-in');
    void view().offsetWidth;
    view().classList.add('fade-in');
}

// ---------- Accueil ----------

async function renderWelcome() {
    view().innerHTML = `
      <div class="eyebrow">Mode hors ligne</div>
      <h1>Réviser sans connexion</h1>
      <p class="lead">Cet appareil n'a pas encore de données hors ligne. Une fois préparé, tu pourras t'entraîner,
        revoir ton carnet d'erreurs, lire le cours et prendre des notes sans réseau. Tout sera envoyé à la version en ligne à la prochaine synchronisation.</p>
      <a class="btn primary" href="#/synchro">${icon('download')}Préparer cet appareil</a>`;
}

async function renderToday() {
    const pack = await S.meta('pack');
    const day = await S.today();
    const plan = await S.get('plan', day);
    const sessions = await S.all('sessions');
    const open = sessions.find((s) => !s.endUtc && new Date(s.deadlineUtc) > new Date());
    const todays = sessions.filter((s) => s.endUtc && S.todayIn(tz, new Date(s.endUtc)) === day);
    const answeredToday = todays.reduce((n, s) => n + s.items.filter((i) => i.answer?.length).length, 0);
    const errors = await S.all('errors');
    const due = errors.filter((e) => e.status === 'Active' && e.nextReview <= day);
    const daysLeft = S.daysBetween(day, pack.examDate);
    const offset = pack.pageOffset || 0;
    const nextSim = (pack.simulationDates || []).find((d) => d >= day);

    const planHtml = plan ? `
        <div class="stack">
          ${plan.label ? `<div><strong>${esc(plan.label)}</strong></div>` : ''}
          ${plan.pageFrom ? `<div class="row between"><span>Lire les pages <span class="mono">${plan.pageFrom}</span> à <span class="mono">${plan.pageTo ?? plan.pageFrom}</span></span>
              <a class="btn small" href="#/lecture/${plan.pageFrom + offset}">${icon('menuBook')}Lire</a></div>` : ''}
          ${plan.chapters?.length ? `<div class="row">${plan.chapters.map((c) => `<span class="tag">${esc(c)}</span>`).join('')}</div>` : ''}
          ${plan.questionGoal ? `<div>
              <div class="row between small"><span>Objectif : ${plural(plan.questionGoal, 'question', 'questions')}</span><span class="mono">${answeredToday} / ${plan.questionGoal}</span></div>
              <div class="progress" style="margin-top:6px"><span style="width:${Math.min(100, 100 * answeredToday / plan.questionGoal)}%"></span></div>
              <div class="help small muted" style="margin-top:6px">Compte uniquement les questions faites hors ligne sur cet appareil.</div></div>` : ''}
          ${plan.ecoTasks?.length ? `<div class="row">${plan.ecoTasks.map((c) => `<span class="tag accent">${esc(c)}</span>`).join('')}</div>` : ''}
        </div>` : `<div class="empty"><strong>Pas de plan pour aujourd'hui</strong>Le plan de révision se règle dans la version en ligne.</div>`;

    view().innerHTML = `
      <section class="hero">
        <div>
          <div class="eyebrow">${esc(fmtDay(day))}</div>
          <h1>Révision hors ligne</h1>
          <p class="lead" style="margin:0">Données téléchargées ${esc(ago(pack.downloadedUtc))}. ${state.pending ? `${plural(state.pending, 'changement attend', 'changements attendent')} la synchronisation.` : 'Tout est synchronisé.'}</p>
        </div>
        <div class="count-big">J-${Math.max(daysLeft, 0)}<small>examen le ${esc(fmtDay(pack.examDate, { day: 'numeric', month: 'long', year: 'numeric' }))}${nextSim ? ` · simulation le ${esc(fmtDay(nextSim, { day: 'numeric', month: 'long' }))}` : ''}</small></div>
      </section>
      ${open ? `<div class="notice accent row between" style="margin-bottom:16px"><span>Session en cours : <strong>${esc(open.title)}</strong></span><a class="btn small primary" href="#/session/${open.clientId}">Reprendre</a></div>` : ''}
      <div class="grid split">
        <div class="card"><h2>Plan du jour</h2>${planHtml}</div>
        <div class="card">
          <h2>Aujourd'hui sur cet appareil</h2>
          <div class="metrics" style="grid-template-columns:repeat(2,minmax(0,1fr))">
            <div class="metric"><span class="v">${answeredToday}</span><span class="l">questions répondues</span></div>
            <div class="metric"><span class="v">${due.length}</span><span class="l">erreurs à revoir</span></div>
          </div>
          <div class="stack" style="margin-top:18px">
            <button class="btn primary" id="quick">${icon('school')}20 questions chronométrées</button>
            <button class="btn" id="replay" ${due.some((e) => e.questionId) ? '' : 'disabled'}>${icon('replay')}Rejouer les erreurs dues</button>
          </div>
        </div>
      </div>`;
    $('#quick').onclick = () => startTraining({ count: 20, seconds: SECONDS_PER_QUESTION, feedback: 'immediate', preferUnseen: true });
    $('#replay').onclick = () => startReplay();
}

// ---------- Entraînement ----------

async function renderTraining() {
    const ecoTasks = await S.all('ecoTasks');
    const questions = await S.all('questions');
    const sources = [...new Set(questions.map((q) => q.source).filter(Boolean))].sort();
    const sessions = (await S.all('sessions')).sort((a, b) => (b.startUtc || '').localeCompare(a.startUtc || ''));
    const saved = (await S.meta('trainingForm')) || { count: 20, seconds: SECONDS_PER_QUESTION, feedback: 'immediate', preferUnseen: true };

    view().innerHTML = `
      <div class="eyebrow">Hors ligne</div>
      <h1>Entraînement chronométré</h1>
      <p class="lead">${plural(questions.length, 'question disponible', 'questions disponibles')} sur cet appareil. Les réponses sont recorrigées par le serveur à la synchronisation et alimentent ton carnet d'erreurs.</p>
      <div class="grid split">
        <form class="card stack" id="form">
          <h2>Nouvelle session</h2>
          <div class="form-grid">
            <div class="field"><label for="domain">Domaine</label>
              <select class="input" id="domain"><option value="">Tous</option>${Object.entries(DOMAINS).map(([k, v]) => `<option value="${k}" ${saved.domain === k ? 'selected' : ''}>${v}</option>`).join('')}</select></div>
            <div class="field"><label for="task">Tâche ECO</label><select class="input" id="task"></select></div>
            <div class="field"><label for="approach">Approche</label>
              <select class="input" id="approach"><option value="">Toutes</option>${Object.entries(APPROACHES).map(([k, v]) => `<option value="${k}" ${saved.approach === k ? 'selected' : ''}>${v}</option>`).join('')}</select></div>
            <div class="field"><label for="source">Source</label>
              <select class="input" id="source"><option value="">Toutes</option>${sources.map((s) => `<option ${saved.source === s ? 'selected' : ''}>${esc(s)}</option>`).join('')}</select></div>
            <div class="field"><label for="count">Nombre de questions</label><input class="input mono" id="count" type="number" min="1" max="180" value="${saved.count}"></div>
            <div class="field"><label for="seconds">Secondes par question</label><input class="input mono" id="seconds" type="number" min="20" max="600" value="${saved.seconds}"><span class="help">80 s = rythme de l'examen</span></div>
          </div>
          <div class="field"><label>Correction</label>
            <div class="segmented" id="feedback">
              <button type="button" data-v="immediate" aria-pressed="${saved.feedback !== 'end'}">Immédiate</button>
              <button type="button" data-v="end" aria-pressed="${saved.feedback === 'end'}">En fin de session</button>
            </div></div>
          <label class="check"><input type="checkbox" id="unseen" ${saved.preferUnseen ? 'checked' : ''}> Questions jamais vues en priorité</label>
          <div class="row between">
            <span class="muted small" id="match"></span>
            <button class="btn primary" id="start">${icon('timer')}Commencer</button>
          </div>
        </form>
        <div class="card">
          <h2>Sessions sur cet appareil</h2>
          ${sessions.length ? `<ul class="list">${sessions.slice(0, 15).map((s) => {
              const done = !!s.endUtc;
              const href = done ? `#/resultats/${s.clientId}` : `#/session/${s.clientId}`;
              const [cls, label] = done ? levelOf(s.scorePercent) : ['accent', 'En cours'];
              return `<li><a href="${href}" class="row between" style="text-decoration:none;color:inherit">
                  <span><strong style="font-weight:560">${esc(s.title)}</strong><br><span class="small muted">${esc(fmtDateTime(s.startUtc))} · ${done ? (s.syncedUtc ? 'envoyée' : 'en attente d\'envoi') : 'à terminer'}</span></span>
                  <span class="row">${done ? `<span class="mono">${String(s.scorePercent).replace('.', ',')} %</span>` : ''}<span class="tag ${cls}">${label}</span></span></a></li>`;
          }).join('')}</ul>` : `<div class="empty"><strong>Aucune session hors ligne</strong>Lance une première session avec le formulaire.</div>`}
        </div>
      </div>`;

    const fillTasks = () => {
        const d = $('#domain').value;
        $('#task').innerHTML = `<option value="">Toutes</option>` + ecoTasks.filter((t) => !d || t.domain === d)
            .map((t) => `<option value="${t.code}" ${saved.task === t.code ? 'selected' : ''}>${esc(t.code)} · ${esc(t.title)}</option>`).join('');
    };
    const read = () => ({
        domain: $('#domain').value || null, task: $('#task').value || null, approach: $('#approach').value || null, source: $('#source').value || null,
        count: Math.min(Math.max(Number($('#count').value) || 20, 1), 180), seconds: Math.min(Math.max(Number($('#seconds').value) || 80, 20), 600),
        feedback: $('#feedback [aria-pressed="true"]').dataset.v, preferUnseen: $('#unseen').checked,
    });
    const recount = () => {
        const f = read();
        const n = filterQuestions(questions, f).length;
        $('#match').textContent = `${plural(n, 'question correspond', 'questions correspondent')} · durée ${fmtDuration(Math.min(f.count, n) * f.seconds)}`;
        $('#start').disabled = n === 0;
    };
    fillTasks();
    recount();
    $('#domain').onchange = () => { fillTasks(); recount(); };
    $('#form').oninput = recount;
    $('#feedback').onclick = (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        $$('#feedback button').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
        recount();
    };
    $('#form').onsubmit = async (e) => {
        e.preventDefault();
        const f = read();
        await S.setMeta('trainingForm', f);
        await startTraining(f, questions);
    };
}

function filterQuestions(questions, f) {
    return questions.filter((q) => (!f.domain || q.domain === f.domain) && (!f.task || q.ecoTask === f.task)
        && (!f.approach || q.approach === f.approach) && (!f.source || q.source === f.source));
}

async function startTraining(f, questions) {
    questions ??= await S.all('questions');
    const seen = new Set((await S.meta('seen')) || []);
    let pool = shuffle(filterQuestions(questions, f));
    if (f.preferUnseen) pool = [...pool.filter((q) => !seen.has(q.id)), ...pool.filter((q) => seen.has(q.id))];
    const picked = pool.slice(0, f.count);
    if (!picked.length) { toast('Aucune question ne correspond.', 'error'); return; }
    const session = newSession('training', `Entraînement · ${plural(picked.length, 'question', 'questions')}`, f.feedback, picked.map((q) => q.id), f.seconds);
    await S.saveSession(session);
    location.hash = `#/session/${session.clientId}`;
}

async function startReplay() {
    const day = await S.today();
    const questions = new Set((await S.all('questions')).map((q) => q.id));
    const due = (await S.all('errors')).filter((e) => e.status === 'Active' && e.nextReview <= day && e.questionId && questions.has(e.questionId))
        .sort((a, b) => a.nextReview.localeCompare(b.nextReview)).slice(0, 30);
    if (!due.length) { toast("Aucune erreur à rejouer aujourd'hui."); return; }
    const session = newSession('replay', `Rejeu d'erreurs · ${plural(due.length, 'question', 'questions')}`, 'immediate', shuffle(due.map((e) => e.questionId)), SECONDS_PER_QUESTION);
    await S.saveSession(session);
    location.hash = `#/session/${session.clientId}`;
}

function newSession(type, title, feedback, questionIds, seconds) {
    const start = new Date();
    const duration = questionIds.length * seconds;
    return {
        clientId: S.uuid(), type, title, feedback, startUtc: start.toISOString(), durationSeconds: duration,
        deadlineUtc: new Date(start.getTime() + duration * 1000).toISOString(), endUtc: null, current: 0,
        items: questionIds.map((id) => ({ questionId: id, answer: [], answeredUtc: null, timeSeconds: 0, flagged: false, doubt: false, checked: false })),
    };
}

// ---------- Session ----------

async function renderSession(clientId) {
    let s = await S.get('sessions', clientId);
    if (!s) { location.hash = '#/entrainement'; return; }
    if (s.endUtc) { location.hash = `#/resultats/${clientId}`; return; }
    const questions = new Map((await Promise.all(s.items.map((i) => S.get('questions', i.questionId)))).filter(Boolean).map((q) => [q.id, q]));
    s.items = s.items.filter((i) => questions.has(i.questionId));
    if (!s.items.length) { toast('Les questions de cette session ne sont plus sur l\'appareil.', 'error'); location.hash = '#/entrainement'; return; }
    s.current = Math.min(s.current || 0, s.items.length - 1);
    let enteredAt = Date.now();
    let finishing = false;

    const flushTime = () => {
        const item = s.items[s.current];
        item.timeSeconds = Math.min((item.timeSeconds || 0) + (Date.now() - enteredAt) / 1000, 3600);
        enteredAt = Date.now();
    };
    const save = () => S.saveSession(s);

    const finish = async (auto = false) => {
        if (finishing) return;
        finishing = true;
        flushTime();
        await save();
        await S.finishSession(s.clientId);
        await refreshCounts();
        if (auto) toast('Temps écoulé : la session est terminée.');
        location.hash = `#/resultats/${s.clientId}`;
        autoSync('fin de session');
    };

    const draw = () => {
        const item = s.items[s.current];
        const q = questions.get(item.questionId);
        const immediate = s.feedback === 'immediate';
        const locked = immediate && item.checked;
        const multi = q.type === 'Multiple';
        const nCorrect = q.options.filter((o) => o.correct).length;
        const answered = s.items.filter((i) => i.answer.length).length;
        const optionClass = (o) => {
            const sel = item.answer.includes(o.order);
            if (!locked) return sel ? 'selected' : '';
            if (o.correct) return 'correct';
            return sel ? 'wrong' : '';
        };
        view().innerHTML = `
          <div class="q-head">
            <span class="tag accent">${esc(s.type === 'replay' ? "Rejeu d'erreurs" : 'Entraînement')}</span>
            <span class="mono small muted">Question ${s.current + 1} / ${s.items.length} · ${answered} répondue${answered > 1 ? 's' : ''}</span>
            <span style="flex:1"></span>
            <span class="timer" id="timer">--:--</span>
          </div>
          <div class="progress" style="margin-bottom:18px"><span style="width:${100 * answered / s.items.length}%"></span></div>
          <div class="grid split">
            <div class="card">
              <div class="row" style="margin-bottom:12px">
                ${q.ecoTask ? `<span class="tag">${esc(q.ecoTask)}</span>` : ''}
                <span class="tag">${esc(APPROACHES[q.approach] || q.approach)}</span>
                ${multi ? `<span class="tag warn">${nCorrect} réponses</span>` : ''}
              </div>
              ${q.scenario ? `<div class="scenario">${esc(q.scenario)}</div>` : ''}
              <p class="stem">${esc(q.stem)}</p>
              <div class="options" role="${multi ? 'group' : 'radiogroup'}">
                ${q.options.map((o, i) => `<button class="option ${optionClass(o)}" data-order="${o.order}" ${locked ? 'disabled' : ''}
                    role="${multi ? 'checkbox' : 'radio'}" aria-checked="${item.answer.includes(o.order)}">
                    <span class="letter">${LETTERS[i]}</span><span>${esc(o.text)}</span></button>`).join('')}
              </div>
              ${locked ? `<div class="explanation"><strong class="${S.isCorrect(q, item.answer) ? '' : ''}">${S.isCorrect(q, item.answer) ? 'Bonne réponse.' : 'Réponse incorrecte.'}</strong>${q.explanation ? `\n\n${esc(q.explanation)}` : ''}</div>` : ''}
              <div class="q-actions">
                <button class="btn ghost small" id="prev" ${s.current === 0 ? 'disabled' : ''}>${icon('arrowBack')}Précédente</button>
                <button class="btn ghost small" id="flag" aria-pressed="${item.flagged}">${icon('flag')}${item.flagged ? 'Marquée' : 'À revoir'}</button>
                <label class="check small"><input type="checkbox" id="doubt" ${item.doubt ? 'checked' : ''} ${locked ? 'disabled' : ''}> Doute</label>
                <span class="spacer"></span>
                ${immediate && !locked ? `<button class="btn primary" id="check" ${item.answer.length ? '' : 'disabled'}>${icon('check')}Valider</button>`
                  : s.current < s.items.length - 1 ? `<button class="btn primary" id="next">Suivante${icon('arrowForward')}</button>`
                  : `<button class="btn primary" id="end">Terminer</button>`}
              </div>
            </div>
            <div class="card">
              <h3>Navigation</h3>
              <div class="qnav">${s.items.map((it, i) => {
                  const cls = [i === s.current ? 'current' : '', it.flagged ? 'flagged' : ''];
                  if (immediate && it.checked) cls.push(S.isCorrect(questions.get(it.questionId), it.answer) ? 'ok' : 'ko');
                  else if (it.answer.length) cls.push('answered');
                  return `<button data-go="${i}" class="${cls.join(' ')}">${i + 1}</button>`;
              }).join('')}</div>
              <button class="btn" id="finish" style="width:100%;margin-top:16px">Terminer la session</button>
            </div>
          </div>`;

        $$('.option', view()).forEach((b) => b.onclick = async () => {
            const order = Number(b.dataset.order);
            if (multi) item.answer = item.answer.includes(order) ? item.answer.filter((x) => x !== order) : [...item.answer, order];
            else item.answer = [order];
            item.answeredUtc = item.answer.length ? new Date().toISOString() : null;
            await save();
            draw();
        });
        $('#prev').onclick = () => go(s.current - 1);
        $('#next') && ($('#next').onclick = () => go(s.current + 1));
        $('#end') && ($('#end').onclick = () => askFinish());
        $('#finish').onclick = () => askFinish();
        $('#check') && ($('#check').onclick = async () => {
            if (multi && item.answer.length !== nCorrect) {
                if (!(await confirmBox(`Cette question attend ${nCorrect} réponses. Valider quand même ?`, 'Valider'))) return;
            }
            flushTime();
            item.checked = true;
            await save();
            draw();
        });
        $('#flag').onclick = async () => { item.flagged = !item.flagged; await save(); draw(); };
        $('#doubt').onchange = async (e) => { item.doubt = e.target.checked; await save(); };
        $$('[data-go]', view()).forEach((b) => b.onclick = () => go(Number(b.dataset.go)));
        tick();
    };

    const go = async (i) => {
        if (i < 0 || i >= s.items.length) return;
        flushTime();
        s.current = i;
        await save();
        draw();
        window.scrollTo({ top: 0, behavior: 'smooth' });
    };

    const askFinish = async () => {
        const missing = s.items.filter((i) => !i.answer.length).length;
        const msg = missing ? `${plural(missing, 'question reste', 'questions restent')} sans réponse (comptées fausses). Terminer ?` : 'Terminer la session ?';
        if (await confirmBox(msg, 'Terminer')) finish();
    };

    const tick = () => {
        const left = (new Date(s.deadlineUtc) - Date.now()) / 1000;
        const el = $('#timer');
        if (el) { el.textContent = fmtDuration(left); el.classList.toggle('low', left < 120); }
        if (left <= 0) finish(true);
    };

    const timer = setInterval(tick, 1000);
    const onHide = () => { if (document.visibilityState === 'hidden') { flushTime(); save(); } };
    document.addEventListener('visibilitychange', onHide);
    const onKey = (e) => {
        if (e.target.closest('input, textarea, select') || $('#modal').open) return;
        const q = questions.get(s.items[s.current].questionId);
        const idx = 'abcdefghij'.indexOf(e.key.toLowerCase());
        if (idx >= 0 && idx < q.options.length) $$('.option', view())[idx]?.click();
        if (e.key === 'ArrowRight') go(s.current + 1);
        if (e.key === 'ArrowLeft') go(s.current - 1);
        if (e.key === 'Enter') ($('#check') || $('#next'))?.click();
    };
    document.addEventListener('keydown', onKey);
    cleanup = () => {
        clearInterval(timer);
        document.removeEventListener('visibilitychange', onHide);
        document.removeEventListener('keydown', onKey);
        if (!finishing) { flushTime(); save(); }
    };
    draw();
}

// ---------- Résultats ----------

async function renderResults(clientId) {
    const s = await S.get('sessions', clientId);
    if (!s || !s.endUtc) { location.hash = s ? `#/session/${clientId}` : '#/entrainement'; return; }
    const questions = new Map((await Promise.all(s.items.map((i) => S.get('questions', i.questionId)))).filter(Boolean).map((q) => [q.id, q]));
    const byDomain = {};
    for (const it of s.items) {
        const d = questions.get(it.questionId)?.domain || 'Autre';
        byDomain[d] ??= { ok: 0, total: 0 };
        byDomain[d].total++;
        if (it.isCorrect) byDomain[d].ok++;
    }
    const time = s.items.reduce((n, i) => n + (i.timeSeconds || 0), 0);
    const [cls, label] = levelOf(s.scorePercent);
    const waiting = !s.syncedUtc;

    view().innerHTML = `
      <section class="hero">
        <div>
          <div class="eyebrow">${esc(fmtDateTime(s.endUtc))}</div>
          <h1>${esc(s.title)}</h1>
          <div class="row"><span class="tag ${cls}">${label}</span>
            <span class="tag ${waiting ? 'warn' : 'ok'}">${waiting ? 'En attente de synchronisation' : `Envoyée ${esc(ago(s.syncedUtc))}`}</span></div>
        </div>
        <div class="count-big">${String(s.scorePercent).replace('.', ',')}<span class="unit">%</span><small>${s.correctCount} / ${s.items.length} justes · ${fmtDuration(time)} · ${s.items.length ? Math.round(time / s.items.length) : 0} s par question</small></div>
      </section>
      <div class="grid split">
        <div class="card">
          <h2>Correction</h2>
          <ul class="list">${s.items.map((it, n) => {
              const q = questions.get(it.questionId);
              if (!q) return `<li class="muted">Question ${n + 1} : plus disponible sur l'appareil.</li>`;
              const mark = (o) => (o.correct ? 'correct' : it.answer.includes(o.order) ? 'wrong' : '');
              return `<li><details>
                  <summary class="row" style="cursor:pointer;list-style:none">
                    <span class="tag ${it.isCorrect ? 'ok' : 'bad'}">${n + 1}</span>
                    <span style="flex:1;min-width:0">${esc(q.stem.length > 110 ? q.stem.slice(0, 107) + '…' : q.stem)}</span>
                    ${it.doubt ? '<span class="tag warn">doute</span>' : ''}${it.flagged ? '<span class="tag">marquée</span>' : ''}
                  </summary>
                  <div style="margin-top:12px">
                    ${q.scenario ? `<div class="scenario">${esc(q.scenario)}</div>` : ''}
                    <p class="stem">${esc(q.stem)}</p>
                    <div class="options">${q.options.map((o, i) => `<div class="option ${mark(o)}" style="cursor:default"><span class="letter">${LETTERS[i]}</span><span>${esc(o.text)}</span></div>`).join('')}</div>
                    ${q.explanation ? `<div class="explanation">${esc(q.explanation)}</div>` : ''}
                  </div></details></li>`;
          }).join('')}</ul>
        </div>
        <div class="stack">
          <div class="card">
            <h2>Par domaine</h2>
            <ul class="list">${Object.entries(byDomain).map(([d, v]) => `<li class="row between"><span>${esc(DOMAINS[d] || d)}</span>
                <span class="mono">${v.ok}/${v.total} · ${Math.round(100 * v.ok / v.total)} %</span></li>`).join('')}</ul>
          </div>
          <div class="card stack">
            <p class="small muted" style="margin:0">Les erreurs et les réponses marquées « doute » rejoignent ton carnet d'erreurs.
              ${waiting ? 'Le serveur recorrigera la session et mettra à jour le carnet en ligne à la synchronisation.' : ''}</p>
            ${waiting ? `<button class="btn primary" id="sync" ${state.net === 'ok' ? '' : 'disabled'}>${icon('sync')}Synchroniser maintenant</button>` : ''}
            <a class="btn" href="#/entrainement">Nouvelle session</a>
          </div>
        </div>
      </div>`;
    $('#sync') && ($('#sync').onclick = () => runSync());
}

// ---------- Carnet d'erreurs ----------

async function renderErrors(filter = 'due') {
    const day = await S.today();
    const errors = await S.all('errors');
    const questions = new Map((await S.all('questions')).map((q) => [q.id, q]));
    const incomplete = (e) => !e.why?.trim() || !e.trap?.trim();
    const filters = {
        due: ['À revoir', (e) => e.status === 'Active' && e.nextReview <= day],
        active: ['Actives', (e) => e.status === 'Active'],
        incomplete: ['Incomplètes', incomplete],
        mastered: ['Maîtrisées', (e) => e.status === 'Mastered'],
    };
    const [, test] = filters[filter] || filters.due;
    const list = errors.filter(test).sort((a, b) => a.nextReview.localeCompare(b.nextReview));
    const replayable = errors.filter((e) => filters.due[1](e) && e.questionId && questions.has(e.questionId)).length;

    view().innerHTML = `
      <div class="row between" style="align-items:flex-end;margin-bottom:20px">
        <div><div class="eyebrow">Répétition espacée J+1, J+3, J+7, J+14</div><h1>Carnet d'erreurs</h1></div>
        <button class="btn primary" id="replay" ${replayable ? '' : 'disabled'}>${icon('replay')}Rejouer ${replayable ? `(${replayable})` : ''}</button>
      </div>
      <div class="segmented" style="margin-bottom:16px">${Object.entries(filters).map(([k, [label, t]]) =>
          `<button data-f="${k}" aria-pressed="${k === (filters[filter] ? filter : 'due')}">${label} <span class="mono muted">${errors.filter(t).length}</span></button>`).join('')}</div>
      ${list.length ? `<div class="card"><ul class="list">${list.map((e) => errorItem(e, questions.get(e.questionId), day)).join('')}</ul></div>`
        : `<div class="empty"><strong>Rien ici</strong>${filter === 'due' ? "Aucune erreur à revoir aujourd'hui." : 'Aucune erreur dans cette catégorie.'}</div>`}`;

    $('#replay').onclick = () => startReplay();
    $$('[data-f]').forEach((b) => b.onclick = () => { location.hash = `#/erreurs/${b.dataset.f}`; });
    $$('form[data-key]').forEach((f) => f.onsubmit = async (ev) => {
        ev.preventDefault();
        try {
            await S.saveErrorAnalysis(f.dataset.key, { why: f.why.value.trim() || null, trap: f.trap.value.trim() || null, mindsetReflex: f.reflex.value.trim() || null });
            toast('Analyse enregistrée sur l\'appareil.');
            await refreshCounts();
        } catch (e) { toast(e.message, 'error'); }
    });
    $$('[data-review]').forEach((b) => b.onclick = async () => {
        await S.reviewExternal(b.dataset.key, b.dataset.review === '1');
        toast(b.dataset.review === '1' ? 'Revue enregistrée : prochaine échéance repoussée.' : 'Revue enregistrée : à revoir demain.');
        await refreshCounts();
        route();
    });
}

function errorItem(e, q, day) {
    const late = e.status === 'Active' && e.nextReview <= day;
    const title = q?.stem || e.summary || '(question supprimée)';
    const correct = q ? q.options.filter((o) => o.correct).map((o) => LETTERS[q.options.indexOf(o)] + '. ' + o.text) : [];
    return `<li><details>
      <summary style="cursor:pointer;list-style:none">
        <div style="margin-bottom:6px">${esc(title.length > 140 ? title.slice(0, 137) + '…' : title)}</div>
        <div class="row small">
          ${e.ecoTask ? `<span class="tag">${esc(e.ecoTask)}</span>` : ''}
          <span class="tag ${e.status === 'Mastered' ? 'ok' : late ? 'warn' : ''}">${e.status === 'Mastered' ? 'Maîtrisée' : late ? 'À revoir' : `Le ${esc(fmtDay(e.nextReview, { day: 'numeric', month: 'short' }))}`}</span>
          <span class="muted">${plural(e.timesWrong, 'erreur', 'erreurs')}</span>
          ${e.origin === 'External' ? `<span class="tag">${esc(e.externalSource || 'Externe')}</span>` : ''}
          ${e.local ? '<span class="tag accent">Nouvelle, hors ligne</span>' : ''}
          ${!e.why?.trim() || !e.trap?.trim() ? '<span class="tag bad">Incomplète</span>' : ''}
        </div>
      </summary>
      <div class="stack" style="margin-top:14px">
        ${q ? `${q.scenario ? `<div class="scenario">${esc(q.scenario)}</div>` : ''}<p class="stem" style="margin:0">${esc(q.stem)}</p>
          <div class="notice ok"><strong>Bonne réponse :</strong> ${correct.map(esc).join(' ; ')}</div>
          ${q.explanation ? `<div class="explanation" style="margin-top:0">${esc(q.explanation)}</div>` : ''}` : ''}
        ${e.origin === 'External' && late && e.id ? `<div class="row"><span class="muted small">Revue :</span>
            <button class="btn small" data-review="1" data-key="${e.key}">${icon('check')}Je savais</button>
            <button class="btn small" data-review="0" data-key="${e.key}">${icon('close')}Je ne savais pas</button></div>` : ''}
        ${e.id ? `<form class="stack" data-key="${e.key}">
            <div class="field"><label>Pourquoi je me suis trompé</label><textarea class="input" name="why">${esc(e.why)}</textarea></div>
            <div class="field"><label>Le piège</label><textarea class="input" name="trap">${esc(e.trap)}</textarea></div>
            <div class="field"><label>Réflexe « mindset »</label><textarea class="input" name="reflex">${esc(e.mindsetReflex)}</textarea></div>
            <div><button class="btn primary small">Enregistrer l'analyse</button></div>
          </form>` : `<p class="small muted" style="margin:0">Erreur créée hors ligne : son analyse sera possible après la synchronisation.</p>`}
      </div></details></li>`;
}

// ---------- Lecture ----------

async function renderReader(page) {
    const pdf = await S.pdfInfo();
    if (!pdf) {
        view().innerHTML = `<div class="eyebrow">Lecture</div><h1>Le cours n'est pas sur cet appareil</h1>
          <p class="lead">Télécharge le PDF du cours depuis Synchronisation pour le lire sans connexion.</p>
          <a class="btn primary" href="#/synchro">${icon('download')}Aller à Synchronisation</a>`;
        return;
    }
    const pack = await S.meta('pack');
    const offset = pack.pageOffset || 0;
    const chapters = await S.all('chapters');
    const ecoTasks = await S.all('ecoTasks');
    let current = page || (await S.meta('lastPage')) || (await S.meta('serverLastPage')) || 1;
    let zoom = 1;
    let total = pdf.pageCount || 0;

    view().innerHTML = `
      <div class="reader-bar">
        <button class="btn small" id="prev" aria-label="Page précédente">${icon('arrowBack')}</button>
        <input class="input mono" id="page" type="number" min="1" value="${current}" aria-label="Page">
        <span class="muted mono small" id="total"></span>
        <button class="btn small" id="next" aria-label="Page suivante">${icon('arrowForward')}</button>
        <span style="flex:1"></span>
        <button class="btn small ghost" id="zout" aria-label="Zoom arrière">${icon('zoomOut')}</button>
        <button class="btn small ghost" id="zin" aria-label="Zoom avant">${icon('zoomIn')}</button>
      </div>
      <div class="reader">
        <div class="pdf-frame" id="frame"><div class="skeleton" style="width:100%;height:70vh"></div></div>
        <aside class="stack">
          <div class="card"><div class="eyebrow">Page <span id="pg"></span></div><h3 id="chapter" style="margin:0"></h3></div>
          <div class="card">
            <h3>Notes de la page</h3>
            <div id="page-notes"></div>
            <form id="add-note" class="stack" style="margin-top:12px">
              <textarea class="input" id="note-text" placeholder="Nouvelle note pour cette page" required></textarea>
              <div class="row"><input class="input" id="note-tags" placeholder="Étiquettes, séparées par des virgules" style="flex:1">
              <select class="input" id="note-eco" style="width:auto"><option value="">ECO</option>${ecoTasks.map((t) => `<option>${esc(t.code)}</option>`).join('')}</select></div>
              <div><button class="btn primary small">${icon('add')}Ajouter</button></div>
            </form>
          </div>
        </aside>
      </div>`;

    const reader = await import('/hors-ligne/reader.js');
    const canvas = document.createElement('canvas');
    const frame = $('#frame');
    try {
        const doc = await reader.open(S.loadPdfBytes);
        total = doc.numPages;
    } catch (e) {
        frame.innerHTML = `<div class="notice error">Impossible d'ouvrir le PDF : ${esc(e.message)}</div>`;
        return;
    }
    frame.replaceChildren(canvas);
    $('#total').textContent = `/ ${total}`;

    // Suivi de lecture : temps actif (5 min max entre deux actions), pages vues.
    const reading = { clientId: S.uuid(), startedUtc: new Date().toISOString(), pageFrom: current, pageTo: current, minutes: 0 };
    let last = Date.now();
    const activity = () => { const gap = Date.now() - last; reading.minutes += Math.min(gap, 5 * 60000) / 60000; last = Date.now(); };

    const notesFor = async () => {
        const notes = (await S.all('notes')).filter((n) => !n.deleted && n.page === current);
        $('#page-notes').innerHTML = notes.length ? `<ul class="list">${notes.map((n) => `<li>
            <div style="white-space:pre-wrap">${esc(n.content)}</div>
            <div class="row small muted" style="margin-top:6px">${n.tags ? esc(n.tags) : ''}${n.ecoTask ? ` <span class="tag">${esc(n.ecoTask)}</span>` : ''}
              <span style="flex:1"></span><button class="btn ghost small" data-del="${n.clientId}" aria-label="Supprimer">${icon('delete')}</button></div></li>`).join('')}</ul>`
            : '<p class="small muted" style="margin:0">Aucune note sur cette page.</p>';
        $$('[data-del]').forEach((b) => b.onclick = async () => {
            if (!(await confirmBox('Supprimer cette note ?', 'Supprimer', true))) return;
            await S.deleteNote(b.dataset.del);
            await refreshCounts();
            notesFor();
        });
    };

    const show = async (p) => {
        current = Math.min(Math.max(p, 1), total);
        $('#page').value = current;
        $('#pg').textContent = offset ? `${current} · imprimée ${current - offset}` : current;
        const ch = chapters.filter((c) => c.startPage <= current && c.endPage >= current).sort((a, b) => b.startPage - a.startPage)[0];
        $('#chapter').textContent = ch ? `${ch.code} ${ch.title}` : '';
        if (location.hash !== `#/lecture/${current}`) history.replaceState(null, '', `#/lecture/${current}`);
        await reader.render(canvas, frame, current, zoom);
        frame.scrollTop = 0;
        activity();
        reading.pageFrom = Math.min(reading.pageFrom, current);
        reading.pageTo = Math.max(reading.pageTo, current);
        reading.updatedUtc = new Date().toISOString();
        await S.setLastPage(current);
        if (reading.minutes >= 0.25) await S.trackReading({ ...reading, minutes: Math.round(reading.minutes * 10) / 10 });
        await notesFor();
        refreshCounts();
    };

    $('#prev').onclick = () => show(current - 1);
    $('#next').onclick = () => show(current + 1);
    $('#page').onchange = (e) => show(Number(e.target.value) || 1);
    $('#zin').onclick = () => { zoom = Math.min(zoom * 1.2, 3); show(current); };
    $('#zout').onclick = () => { zoom = Math.max(zoom / 1.2, 0.5); show(current); };
    $('#add-note').onsubmit = async (e) => {
        e.preventDefault();
        const ch = chapters.filter((c) => c.startPage <= current && c.endPage >= current).sort((a, b) => b.startPage - a.startPage)[0];
        await S.saveNote({ page: current, content: $('#note-text').value.trim(), tags: $('#note-tags').value.trim(), ecoTask: $('#note-eco').value || null, chapterId: ch?.id ?? null });
        $('#note-text').value = '';
        toast('Note enregistrée sur l\'appareil.');
        await refreshCounts();
        notesFor();
    };
    const onKey = (e) => {
        if (e.target.closest('input, textarea, select')) return;
        if (e.key === 'ArrowRight' || e.key === 'PageDown') show(current + 1);
        if (e.key === 'ArrowLeft' || e.key === 'PageUp') show(current - 1);
    };
    const onResize = debounce(() => show(current), 250);
    document.addEventListener('keydown', onKey);
    window.addEventListener('resize', onResize);
    cleanup = () => {
        document.removeEventListener('keydown', onKey);
        window.removeEventListener('resize', onResize);
        activity();
        if (reading.minutes >= 0.25) S.trackReading({ ...reading, updatedUtc: new Date().toISOString(), minutes: Math.round(reading.minutes * 10) / 10 });
    };
    await show(current);
}

function debounce(fn, ms) { let t; return () => { clearTimeout(t); t = setTimeout(fn, ms); }; }

// ---------- Notes ----------

async function renderNotes() {
    const notes = (await S.all('notes')).filter((n) => !n.deleted).sort((a, b) => (b.updatedUtc || '').localeCompare(a.updatedUtc || ''));
    const waiting = new Set((await S.pending()).filter((o) => o.kind === 'note').map((o) => o.clientId));
    const ecoTasks = await S.all('ecoTasks');

    view().innerHTML = `
      <div class="row between" style="align-items:flex-end;margin-bottom:20px">
        <div><div class="eyebrow">${plural(notes.length, 'note', 'notes')}</div><h1>Notes</h1></div>
        <button class="btn primary" id="new">${icon('add')}Nouvelle note</button>
      </div>
      <div class="field" style="margin-bottom:16px"><input class="input" id="q" type="search" placeholder="Rechercher dans les notes et les étiquettes"></div>
      <div class="stack" id="notes"></div>`;

    const draw = () => {
        const words = $('#q').value.toLowerCase().split(/\s+/).filter(Boolean);
        const shown = notes.filter((n) => words.every((w) => `${n.content} ${n.tags} ${n.ecoTask || ''}`.toLowerCase().includes(w)));
        $('#notes').innerHTML = shown.length ? shown.map((n) => `<article class="card">
            <div class="row" style="margin-bottom:8px">
              ${n.page ? `<a class="tag accent" href="#/lecture/${n.page}">page ${n.page}</a>` : ''}
              ${n.ecoTask ? `<span class="tag">${esc(n.ecoTask)}</span>` : ''}
              ${(n.tags || '').split(',').map((t) => t.trim()).filter(Boolean).map((t) => `<span class="tag">${esc(t)}</span>`).join('')}
              ${waiting.has(n.clientId) ? '<span class="tag warn">non envoyée</span>' : ''}
              <span style="flex:1"></span>
              <span class="small muted">${esc(fmtDateTime(n.updatedUtc))}</span>
            </div>
            <div style="white-space:pre-wrap">${esc(n.content.length > 600 ? n.content.slice(0, 597) + '…' : n.content)}</div>
            <div class="row" style="margin-top:10px">
              <button class="btn ghost small" data-edit="${n.clientId}">${icon('edit')}Modifier</button>
              <button class="btn ghost small" data-del="${n.clientId}">${icon('delete')}Supprimer</button>
            </div></article>`).join('')
            : `<div class="empty"><strong>Aucune note</strong>${words.length ? 'Rien ne correspond à la recherche.' : 'Ajoute une note ici ou depuis le lecteur.'}</div>`;
        $$('[data-edit]').forEach((b) => b.onclick = () => editNote(notes.find((n) => n.clientId === b.dataset.edit), ecoTasks));
        $$('[data-del]').forEach((b) => b.onclick = async () => {
            if (!(await confirmBox('Supprimer cette note ?', 'Supprimer', true))) return;
            await S.deleteNote(b.dataset.del);
            await refreshCounts();
            route();
        });
    };
    $('#q').oninput = draw;
    $('#new').onclick = () => editNote(null, ecoTasks);
    draw();
}

function editNote(note, ecoTasks) {
    const dlg = modal(`<form method="dialog" class="stack" id="note-form">
        <h2>${note ? 'Modifier la note' : 'Nouvelle note'}</h2>
        <div class="field"><label for="n-content">Note (Markdown)</label><textarea class="input" id="n-content" rows="8" required>${esc(note?.content)}</textarea></div>
        <div class="form-grid">
          <div class="field"><label for="n-page">Page du PDF</label><input class="input mono" id="n-page" type="number" min="1" value="${note?.page ?? ''}"></div>
          <div class="field"><label for="n-eco">Tâche ECO</label><select class="input" id="n-eco"><option value="">Aucune</option>${ecoTasks.map((t) => `<option ${note?.ecoTask === t.code ? 'selected' : ''}>${esc(t.code)}</option>`).join('')}</select></div>
        </div>
        <div class="field"><label for="n-tags">Étiquettes</label><input class="input" id="n-tags" value="${esc(note?.tags)}" placeholder="séparées par des virgules"></div>
        <div class="row" style="justify-content:flex-end"><button type="button" class="btn ghost" id="n-cancel">Annuler</button><button class="btn primary">Enregistrer</button></div>
      </form>`, (d) => {
        $('#n-cancel', d).onclick = () => d.close();
        $('#note-form', d).onsubmit = async (e) => {
            e.preventDefault();
            const content = $('#n-content', d).value.trim();
            if (!content) return;
            await S.saveNote({ ...(note || {}), content, page: Number($('#n-page', d).value) || null, ecoTask: $('#n-eco', d).value || null, tags: $('#n-tags', d).value.trim() });
            d.close();
            toast('Note enregistrée sur l\'appareil.');
            await refreshCounts();
            route();
        };
    });
    $('#n-content', dlg).focus();
}

// ---------- Synchronisation ----------

async function runSync(options = {}) {
    const entry = await S.sync({ reason: 'manuel', ...options });
    await refreshCounts();
    if (entry.ok) toast(entry.sent ? `Synchronisé : ${entry.sent}.` : entry.pulled ? 'Données mises à jour.' : 'Changements envoyés.');
    else if (entry.auth) { state.net = 'auth'; renderStatus(); toast('Connecte-toi à ton compte pour synchroniser.', 'error'); }
    else toast(entry.error || 'Synchronisation impossible.', 'error');
    route();
    return entry;
}

async function renderSync() {
    const [pack, pdf, lastSync, items, log, auto, name] = await Promise.all([
        S.meta('pack'), S.pdfInfo(), S.meta('lastSync'), S.describePending(), S.journal(), S.meta('autoSync'), S.deviceName()]);
    const storage = navigator.storage?.estimate ? await navigator.storage.estimate().catch(() => null) : null;
    const persisted = navigator.storage?.persisted ? await navigator.storage.persisted().catch(() => false) : false;
    const online = state.net === 'ok';
    const loginUrl = '/Account/Login?ReturnUrl=' + encodeURIComponent('/hors-ligne/index.html#/synchro');
    const kinds = items.reduce((m, i) => ({ ...m, [i.kind]: (m[i.kind] || 0) + 1 }), {});

    const connection = {
        ok: ['ok', 'Connecté au serveur et au compte.'],
        auth: ['warn', `Le serveur répond mais ta session a expiré. <a href="${loginUrl}">Se connecter</a> puis revenir ici.`],
        offline: ['warn', navigator.onLine ? 'Le serveur ne répond pas. Tes changements restent sur l\'appareil.' : 'Pas de réseau. Tes changements restent sur l\'appareil et partiront à la prochaine synchronisation.'],
        error: ['error', 'Le serveur répond avec une erreur. Réessaie plus tard.'],
        checking: ['', 'Vérification de la connexion…'],
    }[state.net];

    view().innerHTML = `
      <div class="eyebrow">Mode hors ligne</div>
      <h1>Synchronisation</h1>
      <p class="lead">Cet appareil garde une copie de tes questions, de ton carnet d'erreurs, de tes notes et du plan. Ce que tu fais sans connexion part vers la version en ligne à la synchronisation : le serveur recorrige les sessions et fusionne les notes sans rien perdre.</p>

      <div class="notice ${connection[0]}" style="margin-bottom:16px">${connection[1]}</div>

      <div class="grid two">
        <div class="card stack">
          <div class="row between"><h2 style="margin:0">Changements en attente</h2><span class="tag ${items.length ? 'warn' : 'ok'}">${items.length ? plural(items.length, 'élément', 'éléments') : 'à jour'}</span></div>
          ${items.length ? `<div class="row">${Object.entries(kinds).map(([k, n]) => `<span class="tag">${esc(k)} · ${n}</span>`).join('')}</div>
            <ul class="list small">${items.slice(0, 12).map((i) => `<li class="row between"><span>${esc(i.text)}</span><span class="muted">${esc(fmtDateTime(i.at))}</span></li>`).join('')}</ul>
            ${items.length > 12 ? `<p class="small muted" style="margin:0">et ${items.length - 12} autre(s).</p>` : ''}`
            : '<p class="muted" style="margin:0">Rien à envoyer. Tout ce qui a été fait sur cet appareil est déjà en ligne.</p>'}
          <div class="row">
            <button class="btn primary" id="sync" ${online ? '' : 'disabled'}>${icon('sync')}${pack ? 'Synchroniser maintenant' : 'Préparer cet appareil'}</button>
            ${items.length ? `<button class="btn" id="push" ${online ? '' : 'disabled'}>Envoyer seulement</button>` : ''}
          </div>
          <p class="small muted" style="margin:0">Synchroniser envoie d'abord les changements, puis télécharge la dernière version des données.${lastSync ? ` Dernier passage ${esc(ago(lastSync.atUtc))} : ${lastSync.ok ? 'réussi' : 'échoué'}.` : ''}</p>
        </div>

        <div class="card stack">
          <h2 style="margin:0">Données sur cet appareil</h2>
          ${pack ? `<div class="metrics four">
              <div class="metric"><span class="v">${pack.counts.questions}</span><span class="l">questions</span></div>
              <div class="metric"><span class="v">${pack.counts.errors}</span><span class="l">erreurs</span></div>
              <div class="metric"><span class="v">${pack.counts.notes}</span><span class="l">notes</span></div>
              <div class="metric"><span class="v">${pack.counts.plan}</span><span class="l">jours de plan</span></div>
            </div>
            <p class="small muted" style="margin:0">Téléchargées ${esc(ago(pack.downloadedUtc))} (${fmtBytes(pack.bytes)}). Fuseau : ${esc(pack.timeZone)}.</p>`
            : '<p class="muted" style="margin:0">Aucune donnée pour l\'instant : lance « Préparer cet appareil » (connexion requise).</p>'}
          <div class="row between" style="border-top:1px solid var(--line);padding-top:12px">
            <span><strong style="font-weight:560">PDF du cours</strong><br><span class="small muted">${pdf ? `Sur l'appareil · ${fmtBytes(pdf.bytes)} · ${esc(ago(pdf.cachedUtc))}`
              : pack?.pdf ? `Non téléchargé · ${fmtBytes(pack.pdf.length)}` : 'Aucun PDF chargé en ligne'}</span>
              ${pdf && pack?.pdf?.uploadedUtc && pdf.uploadedUtc !== pack.pdf.uploadedUtc ? '<br><span class="tag warn">Une nouvelle version est en ligne</span>' : ''}</span>
            ${pdf ? `<span class="row"><button class="btn small" id="pdf-get" ${online ? '' : 'disabled'}>Mettre à jour</button><button class="btn small danger" id="pdf-del">Retirer</button></span>`
              : pack?.pdf ? `<button class="btn small" id="pdf-get" ${online ? '' : 'disabled'}>${icon('download')}Télécharger</button>` : ''}
          </div>
          <div class="progress hidden" id="pdf-progress"><span style="width:0"></span></div>
          ${storage ? `<p class="small muted" style="margin:0">Espace utilisé par le site : ${fmtBytes(storage.usage || 0)}${storage.quota ? ` sur ${fmtBytes(storage.quota)} disponibles` : ''}.
            ${persisted ? 'Stockage protégé contre l\'effacement automatique.' : 'Le navigateur peut effacer ces données s\'il manque de place.'}</p>` : ''}
        </div>
      </div>

      <div class="grid two" style="margin-top:16px">
        <div class="card stack">
          <h2 style="margin:0">Réglages</h2>
          <label class="switch"><span><strong style="font-weight:560">Synchronisation automatique</strong><br><span class="small muted">Envoie les changements dès que la connexion revient, et rafraîchit les données toutes les 6 heures quand l'app en ligne est ouverte.</span></span>
            <input type="checkbox" id="auto" ${auto === false ? '' : 'checked'}></label>
          <div class="field"><label for="name">Nom de cet appareil</label><input class="input" id="name" value="${esc(name)}"><span class="help">Visible dans la version en ligne (Réglages et imports).</span></div>
          <div class="row"><a class="btn ghost small" href="/" id="online">${icon('openInNew')}Ouvrir la version en ligne</a></div>
        </div>
        <div class="card stack">
          <h2 style="margin:0">Journal</h2>
          ${log.length ? `<ul class="list log">${log.slice(0, 10).map((j) => `<li><span class="ico ${j.ok ? 'ok' : 'ko'}">${j.ok ? '✓' : '!'}</span><div>
              <div><strong style="font-weight:560">${esc(fmtDateTime(j.atUtc))}</strong> <span class="muted small">· ${esc(j.reason)}</span></div>
              <div class="small ${j.ok ? 'muted' : ''}" style="${j.ok ? '' : 'color:var(--error)'}">${j.ok ? `${j.sent ? `Envoyé : ${esc(j.sent)}.` : 'Rien à envoyer.'}${j.pulled ? ' Données mises à jour.' : ''}` : esc(j.error)}</div>
              ${[...(j.conflicts || []), ...(j.warnings || [])].length ? `<ul>${[...j.conflicts, ...j.warnings].map((c) => `<li>${esc(c)}</li>`).join('')}</ul>` : ''}
            </div></li>`).join('')}</ul>` : '<p class="muted" style="margin:0">Aucune synchronisation pour l\'instant.</p>'}
        </div>
      </div>

      <div class="card stack" style="margin-top:16px">
        <h2 style="margin:0">Effacer les données hors ligne</h2>
        <p class="small muted" style="margin:0">Retire de cet appareil les questions, notes, sessions et le PDF. Les données en ligne ne sont pas touchées.${items.length ? ` <strong style="color:var(--error)">${plural(items.length, 'changement non envoyé sera perdu', 'changements non envoyés seront perdus')}.</strong>` : ''}</p>
        <div><button class="btn danger small" id="wipe">${icon('delete')}Effacer cet appareil</button></div>
      </div>`;

    $('#sync').onclick = async (e) => {
        e.currentTarget.disabled = true;
        e.currentTarget.textContent = 'Synchronisation…';
        await runSync();
    };
    $('#push') && ($('#push').onclick = () => runSync({ pushOnly: true }));
    $('#auto').onchange = (e) => S.setMeta('autoSync', e.target.checked);
    $('#name').onchange = (e) => S.setMeta('deviceName', e.target.value.trim() || null);
    $('#pdf-get') && ($('#pdf-get').onclick = async (e) => {
        const btn = e.currentTarget;
        btn.disabled = true;
        const bar = $('#pdf-progress');
        bar.classList.remove('hidden');
        try {
            const size = await S.downloadPdf((got, total) => {
                bar.firstElementChild.style.width = total ? `${100 * got / total}%` : '50%';
                btn.textContent = total ? `${Math.round(100 * got / total)} %` : fmtBytes(got);
            });
            (await import('/hors-ligne/reader.js')).close();
            toast(`PDF du cours enregistré sur l'appareil (${fmtBytes(size)}).`);
        } catch (err) {
            toast(err instanceof S.AuthError ? 'Connecte-toi pour télécharger le PDF.' : `Téléchargement impossible : ${err.message}`, 'error');
        }
        route();
    });
    $('#pdf-del') && ($('#pdf-del').onclick = async () => {
        if (!(await confirmBox('Retirer le PDF de cet appareil ?', 'Retirer', true))) return;
        await S.removePdf();
        (await import('/hors-ligne/reader.js')).close();
        route();
    });
    $('#wipe').onclick = async () => {
        const msg = items.length ? `Effacer cet appareil et perdre ${plural(items.length, 'changement non envoyé', 'changements non envoyés')} ?` : 'Effacer les données hors ligne de cet appareil ?';
        if (!(await confirmBox(msg, 'Effacer', true))) return;
        (await import('/hors-ligne/reader.js')).close();
        await S.wipe();
        toast('Données hors ligne effacées.');
        await refreshCounts();
        location.hash = '#/synchro';
        route();
    };
}

// ---------- Démarrage ----------

window.addEventListener('hashchange', route);
window.addEventListener('online', () => checkNetwork());
window.addEventListener('offline', () => { checkNetwork(); if (currentRoute().startsWith('synchro')) route(); });
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') checkNetwork(); });
S.onChange(() => refreshCounts());
setInterval(() => { if (document.visibilityState === 'visible') checkNetwork(); }, 30000);

if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(console.warn);

(async () => {
    try {
        await S.db();
    } catch (e) {
        view().innerHTML = `<div class="notice error">Stockage local indisponible (${esc(e.message)}). Le mode hors ligne nécessite un navigateur qui autorise le stockage pour ce site (pas de navigation privée).</div>`;
        return;
    }
    await refreshCounts();
    await route();
    await checkNetwork();
    if (currentRoute().startsWith('synchro')) route();
    await autoSync('ouverture');
})();
