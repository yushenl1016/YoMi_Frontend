(() => {
    const controls = document.querySelectorAll("[data-home-volume]");
    const video = document.querySelector("[data-background-video]");
    let muted = true;
    let volume = 50;
    const render = () => {
        controls.forEach(input => { input.value = String(muted ? 0 : volume); });
        document.querySelectorAll("[data-home-volume-label]").forEach(label => {
            label.textContent = muted ? "靜音" : `${volume}%`;
        });
        document.querySelectorAll("[data-home-volume-icon]").forEach(icon => {
            const sprite = icon.getAttribute("href").split("#")[0];
            icon.setAttribute("href", `${sprite}#${muted ? "volume-muted" : "volume"}`);
        });
        document.querySelectorAll("[data-home-mute]").forEach(button => {
            button.setAttribute("aria-label", muted ? "開啟背景音效" : "關閉背景音效");
            button.setAttribute("aria-pressed", String(!muted));
        });
        if (video) {
            video.volume = volume / 100;
            video.muted = muted;
        }
    };
    controls.forEach(input => input.addEventListener("input", () => {
        volume = Math.min(100, Math.max(0, Number(input.value) || 0));
        muted = volume === 0;
        render();
    }));
    document.querySelectorAll("[data-home-mute]").forEach(button => {
        button.addEventListener("click", () => {
            if (muted && volume === 0) volume = 50;
            muted = !muted;
            render();
        });
    });
    // Keep the inline mobile controls visible, independent of the desktop popover.
    document.querySelectorAll(".mobile-nav .home-volume").forEach(panel => { panel.open = true; });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            document.querySelectorAll(".header-actions .home-volume, .mobile-nav").forEach(panel => { panel.open = false; });
        }
    });
    render();
})();
