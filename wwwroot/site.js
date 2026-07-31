// Utilitaires JS de NutriPlan — invoqués via IJSRuntime.
window.nutriplan = {
    timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,
    tzOffsetMinutes: () => new Date().getTimezoneOffset(),
    print: () => window.print(),
    copyText: (text) => navigator.clipboard.writeText(text),

    // Swipe horizontal (tactile) pour changer de jour dans le journal.
    // Écouteurs passifs : le défilement vertical n'est jamais bloqué ; le geste
    // n'est retenu que s'il est franc (≥ 60 px, nettement horizontal, < 600 ms).
    attachDaySwipe: (id, dotnetRef) => {
        const el = document.getElementById(id);
        if (!el || el.dataset.swipeAttached) return;
        el.dataset.swipeAttached = '1';
        let x0 = 0, y0 = 0, t0 = 0, tracking = false;
        el.addEventListener('touchstart', e => {
            const t = e.touches[0];
            x0 = t.clientX; y0 = t.clientY; t0 = Date.now();
            tracking = e.touches.length === 1;
        }, { passive: true });
        el.addEventListener('touchend', e => {
            if (!tracking) return;
            tracking = false;
            const t = e.changedTouches[0], dx = t.clientX - x0, dy = t.clientY - y0;
            if (Math.abs(dx) > 60 && Math.abs(dx) > 2 * Math.abs(dy) && Date.now() - t0 < 600)
                dotnetRef.invokeMethodAsync('OnDaySwipe', dx < 0 ? 'next' : 'prev');
        }, { passive: true });
    },
};
