// iPhone draws its keyboard over the bottom of the page instead of shrinking it.
// Measure how much is covered so bottom sheets can sit above the keyboard.
if (window.visualViewport) {
    const vv = window.visualViewport;
    const measure = () => {
        const covered = Math.max(0, window.innerHeight - vv.height - vv.offsetTop);
        document.documentElement.style.setProperty('--kb', (covered > 60 ? covered : 0) + 'px');
    };
    vv.addEventListener('resize', measure);
    vv.addEventListener('scroll', measure);
    measure();
}

// Small helpers the app calls from C#.
window.budget = {
    // The key is kept in this browser only. If storage is blocked (private browsing),
    // these fail quietly and the key lasts until the app is closed.
    getKey() {
        try { return localStorage.getItem('budget.key'); } catch { return null; }
    },
    setKey(value) {
        try {
            if (value) localStorage.setItem('budget.key', value);
            else localStorage.removeItem('budget.key');
        } catch { /* storage unavailable */ }
    },
    // Small remembered choices (e.g. CPF age band). Same quiet failure as the key.
    getPref(name) {
        try { return localStorage.getItem('budget.pref.' + name); } catch { return null; }
    },
    setPref(name, value) {
        try { localStorage.setItem('budget.pref.' + name, value); } catch { /* storage unavailable */ }
    },
    // Hands a file to the phone: the share sheet where available, otherwise a normal download.
    async saveFile(name, type, base64) {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const file = new File([bytes], name, { type });
        if (navigator.canShare && navigator.canShare({ files: [file] })) {
            try { await navigator.share({ files: [file] }); return; }
            catch (e) { if (e && e.name === 'AbortError') return; }
        }
        const url = URL.createObjectURL(file);
        const a = document.createElement('a');
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    }
};
