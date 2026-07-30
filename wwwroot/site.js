// Utilitaires JS de NutriPlan — invoqués via IJSRuntime.
window.nutriplan = {
    timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,
    tzOffsetMinutes: () => new Date().getTimezoneOffset(),
    print: () => window.print(),
    copyText: (text) => navigator.clipboard.writeText(text),
};

// PWA : service worker minimal (installabilité Android/Chrome — aucun cache).
if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/sw.js').catch(() => { /* non bloquant */ });
}
