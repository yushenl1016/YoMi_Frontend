document.querySelectorAll("[data-dismissible] button").forEach((button) => {
    button.addEventListener("click", () => button.closest("[data-dismissible]")?.remove());
});

document.querySelectorAll("form[data-confirm]").forEach((form) => {
    form.addEventListener("submit", (event) => {
        if (!window.confirm(form.dataset.confirm || "確定執行此操作？")) event.preventDefault();
    });
});

const soundButton = document.querySelector("[data-sound-toggle]");
const backgroundVideo = document.querySelector("[data-background-video]");
if (soundButton && backgroundVideo) {
    const updateSound = () => {
        soundButton.textContent = backgroundVideo.muted ? "♫" : "♪";
        soundButton.setAttribute("aria-label", backgroundVideo.muted ? "開啟背景音效" : "關閉背景音效");
        soundButton.setAttribute("aria-pressed", String(!backgroundVideo.muted));
    };
    soundButton.addEventListener("click", () => {
        backgroundVideo.muted = !backgroundVideo.muted;
        if (!backgroundVideo.muted) {
            if (backgroundVideo.volume === 0) backgroundVideo.volume = .5;
            backgroundVideo.play().catch(() => {
                backgroundVideo.muted = true;
                updateSound();
                soundButton.setAttribute("aria-label", "影片無法播放，點擊重試");
            });
        }
        updateSound();
    });
    updateSound();
}

const flash = document.querySelector(".flash");
if (flash) window.setTimeout(() => flash.remove(), 4500);

document.querySelectorAll('[data-referral]').forEach(panel => {
    const menu = panel.closest('[data-member-menu]');
    const code = panel.querySelector('[data-referral-code]');
    const copy = panel.querySelector('[data-copy-referral]');
    const status = panel.querySelector('[data-referral-status]');
    let referralCode, loading = false;
    const loadCode = async () => {
        if (referralCode || loading) return;
        loading = true;
        code.textContent = '載入中…';
        try {
            const response = await fetch(panel.dataset.codeUrl, { cache: 'no-store', credentials: 'same-origin' });
            if (!response.ok) throw new Error('Referral code request failed');
            const result = await response.json();
            if (!result.code || typeof result.code !== 'string') throw new Error('Referral code is missing');
            referralCode = result.code;
            code.textContent = referralCode;
            copy.disabled = false;
        } catch {
            code.textContent = '暫時無法取得';
        } finally {
            loading = false;
        }
    };
    if (menu) menu.addEventListener('toggle', () => { if (menu.open) return loadCode(); });
    else loadCode();
    copy.addEventListener('click', async event => {
        event.stopPropagation();
        if (!referralCode) return;
        try {
            await navigator.clipboard.writeText(referralCode);
            copy.textContent = '已複製';
            status.textContent = '推薦碼已複製';
        } catch {
            copy.textContent = '請選取複製';
            status.textContent = '請選取推薦碼並手動複製';
        }
    });
});

document.querySelectorAll('[data-member-menu], [data-battle-menu]').forEach(menu => {
    menu.querySelectorAll('.member-referrer-link, .battle-menu-link').forEach(link => link.addEventListener('click', () => {
        menu.open = false;
        const mobileNav = menu.closest('.mobile-nav');
        if (mobileNav) mobileNav.open = false;
    }));
    menu.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || !menu.open) return;
        event.preventDefault();
        event.stopPropagation();
        menu.open = false;
        menu.querySelector('summary').focus();
    });
    document.addEventListener('click', event => {
        if (!menu.contains(event.target)) menu.open = false;
    });
});
