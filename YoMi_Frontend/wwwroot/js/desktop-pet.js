(() => {
    const button = document.querySelector('[data-copy-sha]');
    if (!button) return;
    const status = document.querySelector('[data-copy-status]');
    button.addEventListener('click', async () => {
        try { await navigator.clipboard.writeText(button.dataset.copySha); status.textContent = '已複製校驗碼'; }
        catch { status.textContent = '無法存取剪貼簿，請選取上方校驗碼手動複製。'; }
    });
})();
