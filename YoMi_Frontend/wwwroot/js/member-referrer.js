const referrerDialog = document.querySelector('[data-referrer-dialog]');
if (referrerDialog) {
    const form = referrerDialog.querySelector('[data-referrer-form]');
    const input = form.querySelector('[name=code]');
    const entry = form.querySelector('[data-referrer-entry]');
    const preview = form.querySelector('[data-referrer-preview]');
    const error = form.querySelector('[data-referrer-error]');
    const submit = form.querySelector('[data-submit-referrer]');
    const back = form.querySelector('[data-back-referrer]');
    const open = document.querySelector('[data-open-referrer]');
    let referrer = null, busy = false;

    const showEntry = () => {
        referrer = null;
        entry.hidden = false;
        preview.hidden = back.hidden = true;
        input.readOnly = false;
        error.textContent = '';
        submit.textContent = '查詢推薦人';
    };
    open.addEventListener('click', () => { referrerDialog.showModal(); input.focus(); });
    form.querySelector('[data-close-referrer]').addEventListener('click', () => referrerDialog.close());
    referrerDialog.addEventListener('cancel', event => { if (busy) event.preventDefault(); });
    referrerDialog.addEventListener('close', () => { form.reset(); showEntry(); });
    back.addEventListener('click', () => { showEntry(); input.focus(); });
    input.addEventListener('input', () => {
        input.value = input.value.trim().toUpperCase();
        error.textContent = '';
    });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy) return;
        const binding = referrer !== null;
        const body = new FormData(form);
        // Submit exactly the code whose owner was shown on the confirmation screen.
        body.set('code', binding ? referrer.code : input.value.trim());
        busy = true;
        error.textContent = '';
        input.readOnly = true;
        form.querySelectorAll('button').forEach(button => { button.disabled = true; });
        submit.textContent = binding ? '綁定中…' : '查詢中…';
        try {
            const response = await fetch(binding ? form.action : form.dataset.previewUrl, {
                method: 'POST', body, credentials: 'same-origin', cache: 'no-store'
            });
            if (response.redirected || response.status === 401)
                throw new Error('登入已過期，請重新登入後再試。');
            const result = await response.json().catch(() => null);
            if (!response.ok) throw new Error(result?.message || '請重新整理頁面後再試。');
            if (binding) {
                if (!result?.success) throw new Error('綁定未完成，請稍後重試。');
                document.querySelector('[data-bound-name]').textContent = referrer.name;
                document.querySelector('[data-bound-code]').textContent = referrer.code;
                document.querySelector('[data-referrer-bound]').hidden = false;
                document.querySelector('[data-referrer-unbound]').hidden = true;
                document.querySelector('[data-binding-status]').textContent = '推薦人綁定成功。';
                referrerDialog.close();
                document.querySelector('#my-referrer-title').setAttribute('tabindex', '-1');
                document.querySelector('#my-referrer-title').focus();
            } else {
                if (!result?.name || !result?.code) throw new Error('暫時無法查詢推薦人，請稍後再試。');
                referrer = result;
                preview.querySelector('[data-preview-name]').textContent = result.name;
                preview.querySelector('[data-preview-code]').textContent = result.code;
                entry.hidden = true;
                preview.hidden = back.hidden = false;
            }
        } catch (requestError) {
            error.textContent = requestError.message || '連線失敗，請稍後重試。';
        } finally {
            busy = false;
            input.readOnly = referrer !== null;
            form.querySelectorAll('button').forEach(button => { button.disabled = false; });
            submit.textContent = referrer ? '確認綁定' : '查詢推薦人';
            if (referrerDialog.open && referrer) submit.focus();
        }
    });
}
