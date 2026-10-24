(() => {
    const button = document.querySelector('[data-copy-sha]');
    if (!button) return;
    const status = document.querySelector('[data-copy-status]');
    button.addEventListener('click', async () => {
        try { await navigator.clipboard.writeText(button.dataset.copySha); status.textContent = '已複製校驗碼'; }
        catch { status.textContent = '無法存取剪貼簿，請選取上方校驗碼手動複製。'; }
    });
})();

(async () => {
    const list = document.querySelector('[data-pet-announcements]');
    const status = document.querySelector('[data-pet-announcements-status]');
    if (!list || !status) return;
    try {
        const response = await fetch('/api/desktop/announcements', { credentials: 'omit', cache: 'no-store' });
        if (!response.ok) throw new Error('unavailable');
        const result = await response.json();
        if (!Array.isArray(result.announcements)) throw new Error('invalid');
        const articles = result.announcements.slice(0, 20).map(post => {
            const article = document.createElement('article');
            const title = document.createElement('h3');
            const body = document.createElement('p');
            title.textContent = `${post.pinned ? '置頂 · ' : ''}${post.title || ''}`;
            body.textContent = post.body || '';
            article.append(title, body);
            return article;
        });
        list.replaceChildren(...articles);
        status.textContent = articles.length ? '' : '目前沒有新公告。';
    } catch { status.textContent = '公告暫時無法載入，請稍後重新整理。'; }
})();
