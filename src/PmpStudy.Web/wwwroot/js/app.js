// Petites aides partagées.
window.pmpStudy = {
    // Téléchargement d'un fichier texte généré côté serveur (export Markdown, JSON).
    download: function (fileName, contentType, text) {
        const blob = new Blob([text], { type: contentType });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    copy: async function (text) {
        await navigator.clipboard.writeText(text);
    },
    // Envoie le contenu d'une zone de texte à l'API (gros imports : hors de la connexion Blazor).
    postText: async function (url, id) {
        const el = document.getElementById(id);
        const text = el ? el.value : '';
        if (!text.trim()) return { status: 0, body: '' };
        const res = await fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: text });
        const body = await res.text();
        if (res.ok) el.value = '';
        return { status: res.status, body: body };
    },
    // Empêche de quitter l'examen par erreur.
    guardUnload: function (on) {
        window.onbeforeunload = on ? () => true : null;
    }
};

// ---------- PWA : service worker et notifications ----------
if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => navigator.serviceWorker.register('/sw.js').catch(console.warn));
}

window.pmpPush = {
    // « unsupported », « default », « granted » ou « denied ».
    state: function () {
        if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) return 'unsupported';
        return Notification.permission;
    },
    isStandalone: function () {
        return window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
    },
    // Abonnement courant de cet appareil, ou null.
    current: async function () {
        if (this.state() === 'unsupported') return null;
        const reg = await navigator.serviceWorker.ready;
        const sub = await reg.pushManager.getSubscription();
        return sub ? sub.toJSON() : null;
    },
    subscribe: async function (publicKey) {
        if (this.state() === 'unsupported') throw new Error('Notifications non prises en charge par ce navigateur.');
        const permission = await Notification.requestPermission();
        if (permission !== 'granted') throw new Error('Autorisation refusée : réactive-la dans les réglages du navigateur.');
        const reg = await navigator.serviceWorker.ready;
        let sub = await reg.pushManager.getSubscription();
        if (!sub) {
            sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(publicKey) });
        }
        return sub.toJSON();
    },
    unsubscribe: async function () {
        if (this.state() === 'unsupported') return null;
        const reg = await navigator.serviceWorker.ready;
        const sub = await reg.pushManager.getSubscription();
        if (!sub) return null;
        const endpoint = sub.endpoint;
        await sub.unsubscribe();
        return endpoint;
    }
};

function base64UrlToBytes(value) {
    const padding = '='.repeat((4 - value.length % 4) % 4);
    const raw = atob((value + padding).replace(/-/g, '+').replace(/_/g, '/'));
    return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

// ---------- Thème mémorisé sur l'appareil ----------
window.pmpTheme = {
    get: function () {
        try { return localStorage.getItem('pmp-theme'); } catch { return null; }
    },
    set: function (value) {
        try { localStorage.setItem('pmp-theme', value); } catch { /* stockage indisponible : thème non mémorisé */ }
    }
};

// ---------- Mode hors ligne : envoi des changements faits sans connexion ----------
// Si cet appareil a déjà servi hors ligne, la version en ligne envoie ce qui attend et rafraîchit les données locales.
window.addEventListener('load', async () => {
    if (location.pathname.startsWith('/Account') || !('indexedDB' in window)) return;
    try {
        if (indexedDB.databases) {
            const dbs = await indexedDB.databases();
            if (!dbs.some((d) => d.name === 'pmpstudy-offline')) return;
        }
        const store = await import('/hors-ligne/store.js');
        if (!(await store.hasPack())) return;
        const run = async (reason) => {
            const entry = await store.autoSync(reason);
            if (entry?.ok && entry.sent) offlineToast(`Mode hors ligne : ${entry.sent} envoyé${entry.conflicts?.length ? `, ${entry.conflicts.length} conflit(s) résolu(s) sans perte` : ''}.`);
            else if (entry && !entry.ok && !entry.offline) offlineToast(`Mode hors ligne : synchronisation impossible (${entry.error}).`);
        };
        await run('version en ligne');
        window.addEventListener('online', () => run('retour du réseau'));
    } catch (e) {
        console.warn('Synchronisation hors ligne', e);
    }
});

function offlineToast(text) {
    const el = document.createElement('div');
    el.className = 'offline-toast';
    el.setAttribute('role', 'status');
    el.textContent = text;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 6000);
}
