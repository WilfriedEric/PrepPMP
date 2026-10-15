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
