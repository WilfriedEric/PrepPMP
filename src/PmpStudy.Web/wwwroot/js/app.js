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
