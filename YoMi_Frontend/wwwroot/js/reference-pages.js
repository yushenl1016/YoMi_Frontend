(() => {
    const editor = document.querySelector('[data-name-editor]');
    if (editor) {
        const greeting = document.querySelector('[data-member-greeting]');
        const editButton = document.querySelector('[data-edit-name]');
        const input = editor.querySelector('input[name="name"]');
        const closeEditor = () => {
            editor.reset();
            editor.hidden = true;
            greeting.hidden = false;
            editButton.focus();
        };
        editButton.addEventListener('click', () => {
            greeting.hidden = true;
            editor.hidden = false;
            input.focus();
            input.select();
        });
        editor.querySelector('[data-cancel-name]').addEventListener('click', closeEditor);
        editor.addEventListener('keydown', event => {
            if (event.key === 'Escape') { event.preventDefault(); closeEditor(); }
        });
        editor.addEventListener('submit', event => {
            input.value = input.value.trim();
            if (!input.value) { event.preventDefault(); closeEditor(); }
        });
    }

    const tabs = document.querySelector('.price-tabs');
    if (!tabs) return;
    let request;
    tabs.addEventListener('click', async event => {
        const link = event.target.closest('a');
        if (!link || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        if (request) request.abort();
        request = new AbortController();
        const signal = request.signal;
        const results = document.querySelector('[data-price-results]');
        results.setAttribute('aria-busy', 'true');
        try {
            const response = await fetch(link.href, { signal, credentials: 'same-origin' });
            if (!response.ok) throw new Error('Unable to load prices');
            const page = new DOMParser().parseFromString(await response.text(), 'text/html');
            const updated = page.querySelector('[data-price-results]');
            if (!updated) throw new Error('Missing prices');
            if (signal.aborted) return;
            results.replaceChildren(...updated.childNodes);
            tabs.querySelectorAll('a').forEach(tab => {
                tab.classList.toggle('is-active', tab === link);
                if (tab === link) tab.setAttribute('aria-current', 'page');
                else tab.removeAttribute('aria-current');
            });
            history.pushState(null, '', link.href);
        } catch (error) {
            if (error.name !== 'AbortError') window.location.assign(link.href);
        } finally {
            if (!signal.aborted) results.removeAttribute('aria-busy');
        }
    });
    window.addEventListener('popstate', () => window.location.reload());
})();
