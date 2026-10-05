(() => {
    const content = document.querySelector('#order-payment-content');
    if (!content) return;
    const title = content.querySelector('#order-payment-title');
    const form = content.querySelector('.order-payment-form');
    const methods = form ? [...form.querySelectorAll('[name="method"]')] : [];
    const choices = form?.querySelector('[data-payment-choices]');
    const details = form?.querySelector('[data-payment-details]');
    const backButton = form?.querySelector('[data-payment-back]');
    const proof = form?.querySelector('[name="proof"]');
    const updateMethod = () => {
        if (!form) return;
        const selected = methods.find(method => method.checked)?.value;
        choices.hidden = Boolean(selected);
        details.hidden = !selected;
        proof.disabled = !selected;
        backButton.hidden = !selected;
        form.querySelectorAll('[data-payment-method]').forEach(panel => {
            panel.hidden = panel.dataset.paymentMethod !== selected;
        });
        title.textContent = selected ? (selected === '轉帳' ? '銀行轉帳付款資訊' : '無卡存款付款資訊') : '選擇付款方式';
    };
    const resetMethod = () => {
        if (form) form.reset();
        updateMethod();
    };
    methods.forEach(method => method.addEventListener('change', () => {
        proof.value = '';
        updateMethod();
        title.focus();
    }));
    backButton?.addEventListener('click', () => {
        const previous = methods.find(method => method.checked);
        resetMethod();
        previous?.focus();
    });
    resetMethod();

    const paymentDialog = document.querySelector('#order-payment-dialog');
    // Method selection also works inline when the browser has no dialog support.
    if (!paymentDialog || typeof paymentDialog.showModal !== 'function') return;
    const entry = document.querySelector('#order-payment-entry');
    const openButton = entry.querySelector('[data-open-payment]');
    const closeButton = content.querySelector('[data-close-payment]');
    paymentDialog.append(content);
    entry.hidden = closeButton.hidden = false;
    const openPayment = () => {
        resetMethod();
        paymentDialog.showModal();
        methods[0]?.focus();
    };
    openButton.addEventListener('click', openPayment);
    closeButton.addEventListener('click', () => paymentDialog.close());
    paymentDialog.addEventListener('close', () => openButton.focus());
    if (paymentDialog.dataset.autoOpen === 'true') openPayment();
})();
