document.querySelectorAll("[data-dismissible] button").forEach((button) => {
    button.addEventListener("click", () => button.closest("[data-dismissible]")?.remove());
});

document.querySelectorAll("form[data-confirm]").forEach((form) => {
    form.addEventListener("submit", (event) => {
        if (!window.confirm(form.dataset.confirm || "確定執行此操作？")) event.preventDefault();
    });
});

const authShell = document.querySelector("[data-auth-shell]");
if (authShell) {
    const panels = authShell.querySelectorAll("[data-auth-panel]");
    const title = authShell.querySelector("[data-auth-title]");
    const subtitle = authShell.querySelector("[data-auth-subtitle]");
    const copy = {
        "email-check": ["郵箱登入", "輸入你的郵箱開始"],
        "password-login": ["密碼登入", "輸入密碼登入"],
        register: ["創建帳號", "設定用戶名稱和密碼"]
    };
    const showStep = (step, email = "") => {
        panels.forEach((panel) => panel.classList.toggle("is-active", panel.dataset.authPanel === step));
        authShell.querySelectorAll("[data-auth-email]").forEach((input) => { input.value = email; });
        authShell.querySelectorAll("[data-selected-email]").forEach((element) => { element.textContent = email; });
        if (title) title.textContent = copy[step][0];
        if (subtitle) subtitle.textContent = copy[step][1];
    };

    const checkForm = authShell.querySelector("[data-email-check]");
    checkForm?.addEventListener("submit", async (event) => {
        event.preventDefault();
        const button = checkForm.querySelector("button[type=submit]");
        const error = checkForm.querySelector("[data-email-error]");
        const email = checkForm.querySelector("input[name=email]").value.trim();
        if (error) error.textContent = "";
        button.disabled = true;
        button.textContent = "檢查中…";
        try {
            const response = await fetch(checkForm.action, { method: "POST", body: new FormData(checkForm), credentials: "same-origin" });
            const result = await response.json();
            if (!response.ok) throw new Error(result.message || "檢查郵箱失敗");
            showStep(result.exists ? "password-login" : "register", email);
        } catch (requestError) {
            if (error) error.textContent = requestError.message || "檢查郵箱失敗";
        } finally {
            button.disabled = false;
            button.textContent = "繼續 →";
        }
    });

    authShell.querySelectorAll("[data-auth-back]").forEach((button) => {
        button.addEventListener("click", () => {
            authShell.querySelectorAll("form").forEach((form) => form.reset());
            showStep("email-check");
            authShell.querySelector("input[name=email]")?.focus();
        });
    });
}

const soundButton = document.querySelector("[data-sound-toggle]");
const backgroundVideo = document.querySelector("[data-background-video]");
if (soundButton && backgroundVideo) {
    soundButton.addEventListener("click", () => {
        backgroundVideo.muted = !backgroundVideo.muted;
        soundButton.textContent = backgroundVideo.muted ? "♫" : "♪";
        soundButton.setAttribute("aria-label", backgroundVideo.muted ? "開啟背景音效" : "關閉背景音效");
    });
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

document.querySelectorAll('[data-member-menu]').forEach(menu => {
    menu.querySelector('.member-referrer-link')?.addEventListener('click', () => {
        menu.open = false;
        const mobileNav = menu.closest('.mobile-nav');
        if (mobileNav) mobileNav.open = false;
    });
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
