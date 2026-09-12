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
