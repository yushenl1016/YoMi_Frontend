(() => {
    const select = document.querySelector('[data-order-option]');
    if (!select) return;
    const server = document.querySelector('[data-order-server]');
    const price = document.querySelector('[data-option-price]');
    const name = document.querySelector('[data-option-name]');
    const update = () => {
        const option = select.selectedOptions[0];
        price.textContent = option?.value ? `NT$ ${option.dataset.price}` : '請選擇子項目';
        name.textContent = option?.dataset.name || '';
    };
    // Server-tagged options only fit that server; untagged options fit every server. The server re-checks on submit.
    const filter = () => {
        for (const option of select.options) {
            const other = Boolean(option.dataset.server && server.value && option.dataset.server !== server.value);
            option.hidden = option.disabled = other;
        }
        if (select.selectedOptions[0]?.disabled) select.value = '';
        update();
    };
    select.addEventListener('change', () => {
        const tag = select.selectedOptions[0]?.dataset.server;
        if (tag && !server.value) { server.value = tag; filter(); } else update();
    });
    server.addEventListener('change', filter);
    filter();
})();
