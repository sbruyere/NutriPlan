// Utilitaires JS de NutriPlan — invoqués via IJSRuntime.
window.nutriplan = {
    timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,
    tzOffsetMinutes: () => new Date().getTimezoneOffset(),
    print: () => window.print(),
    copyText: (text) => navigator.clipboard.writeText(text),

    // Défilement page à page du journal : la piste (3 pages côte à côte, base
    // translateX(-100%)) suit le doigt en temps réel. touch-action: pan-y sur le
    // conteneur laisse le défilement vertical au navigateur — écouteurs passifs.
    // Au lâcher : au-delà de 22 % de la largeur, la piste s'anime vers la page
    // voisine puis .NET recharge et recale (resetPager) ; sinon retour élastique.
    attachDayPager: (id, dotnetRef) => {
        const wrap = document.getElementById(id);
        if (!wrap || wrap.dataset.pagerAttached) return;
        wrap.dataset.pagerAttached = '1';
        const track = () => wrap.querySelector('[data-pager-track]');
        const GAP = 24; // gouttière gap-6 entre les pages — incluse dans la base et la course
        let x0 = 0, y0 = 0, dx = 0, width = 1, active = false, horizontal = null, settling = false;
        const setX = (px, animate) => {
            const t = track();
            if (!t) return;
            t.style.transition = animate ? 'transform .26s cubic-bezier(.2,.7,.3,1)' : 'none';
            t.style.transform = `translateX(calc(-100% - ${GAP}px + ${px}px))`;
        };
        wrap.addEventListener('touchstart', e => {
            if (settling || e.touches.length !== 1 || wrap.dataset.pagerEnabled !== '1') { active = false; return; }
            const t = e.touches[0];
            x0 = t.clientX; y0 = t.clientY; dx = 0; horizontal = null;
            width = wrap.clientWidth || 1;
            active = true;
        }, { passive: true });
        wrap.addEventListener('touchmove', e => {
            if (!active) return;
            const t = e.touches[0];
            dx = t.clientX - x0;
            const dy = t.clientY - y0;
            if (horizontal === null && (Math.abs(dx) > 8 || Math.abs(dy) > 8))
                horizontal = Math.abs(dx) > Math.abs(dy);
            if (horizontal) {
                // butée douce vers demain quand on est déjà sur aujourd'hui
                const blocked = dx < 0 && wrap.dataset.canNext !== '1';
                setX(blocked ? dx / 3 : dx, false);
            }
        }, { passive: true });
        wrap.addEventListener('touchend', () => {
            if (!active) return;
            active = false;
            if (!horizontal) return;
            const goNext = dx < 0;
            const commit = Math.abs(dx) > width * 0.22 && (!goNext || wrap.dataset.canNext === '1');
            if (!commit) { setX(0, true); return; }
            settling = true;
            setX(goNext ? -(width + GAP) : width + GAP, true);
            setTimeout(() => {
                dotnetRef.invokeMethodAsync('OnPagerSettled', goNext ? 'next' : 'prev')
                    .finally(() => { settling = false; });
            }, 270);
        }, { passive: true });
        wrap.addEventListener('touchcancel', () => { if (active) { active = false; if (horizontal) setX(0, true); } }, { passive: true });
    },

    // Recale la piste au centre sans animation, après le rechargement du contenu.
    resetPager: (id) => {
        const t = document.getElementById(id)?.querySelector('[data-pager-track]');
        if (t) { t.style.transition = 'none'; t.style.transform = 'translateX(calc(-100% - 24px))'; }
    },
};
