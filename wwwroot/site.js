// Utilitaires JS de NutriPlan — invoqués via IJSRuntime.
window.nutriplan = {
    timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,
    tzOffsetMinutes: () => new Date().getTimezoneOffset(),
    print: () => window.print(),
    copyText: (text) => navigator.clipboard.writeText(text),
};
