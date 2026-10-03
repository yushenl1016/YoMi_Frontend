// Run: node tools/member-referrer-checks.cjs; exercises the production dialog script without real bindings.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../YoMi_Frontend/wwwroot/js/member-referrer.js'), 'utf8');
class Element {
    constructor() { this.handlers = {}; this.hidden = false; this.value = ''; this.textContent = ''; }
    addEventListener(event, handler) { this.handlers[event] = handler; }
    fire(event) { return this.handlers[event]?.({preventDefault() {}}); }
    focus() { this.focused = true; }
    setAttribute() {}
}
async function run() {
    const elements = Object.fromEntries(['referrer-dialog', 'referrer-form', 'referrer-entry', 'referrer-preview',
        'referrer-error', 'submit-referrer', 'back-referrer', 'open-referrer', 'close-referrer', 'preview-name', 'preview-code',
        'bound-name', 'bound-code', 'referrer-bound', 'referrer-unbound', 'binding-status'].map(key => [key, new Element()]));
    const input = new Element(), heading = new Element();
    const select = selector => selector === '[name=code]' ? input : selector === '#my-referrer-title' ? heading
        : elements[selector.slice(6, -1)];
    const form = elements['referrer-form'], dialog = elements['referrer-dialog'];
    Object.values(elements).forEach(element => { element.querySelector = select; });
    form.querySelectorAll = () => ['submit-referrer', 'back-referrer', 'close-referrer'].map(key => elements[key]);
    form.action = '/virtual/my-records/referrer/bind';
    form.dataset = {previewUrl: '/virtual/my-records/referrer/preview'};
    form.reset = () => { input.value = ''; };
    dialog.showModal = () => { dialog.open = true; };
    dialog.close = () => { dialog.open = false; dialog.fire('close'); };
    const calls = [];
    let reply, pending;
    class FormData extends Map { constructor() { super([['__RequestVerificationToken', 'test-token'], ['code', input.value]]); } }
    vm.runInNewContext(source, {
        document: {querySelector: select}, FormData,
        fetch: async (url, options) => {
            calls.push({url, options});
            if (pending) await new Promise(resolve => { pending = resolve; });
            if (reply instanceof Error) throw reply;
            return {ok: true, status: 200, json: async () => ({name: '<script>example</script>', code: 'FEDCBA9876543210'}), ...reply};
        }
    });
    elements['open-referrer'].fire('click');
    assert(dialog.open && input.focused);
    input.value = '  fedcba9876543210  '; input.fire('input');
    assert.equal(input.value, 'FEDCBA9876543210');
    pending = true;
    const first = form.fire('submit');
    assert(form.querySelectorAll().every(button => button.disabled));
    await form.fire('submit');
    assert.equal(calls.length, 1, 'repeated submit while pending cannot duplicate a request');
    pending(); pending = null; await first;
    assert.equal(calls[0].url, form.dataset.previewUrl);
    assert.equal(calls[0].options.body.get('__RequestVerificationToken'), 'test-token');
    assert.equal(elements['preview-name'].textContent, '<script>example</script>', 'untrusted names are displayed as text');
    assert(elements['referrer-entry'].hidden && !elements['referrer-preview'].hidden && input.readOnly);
    assert.equal(elements['submit-referrer'].textContent, '確認綁定');
    elements['back-referrer'].fire('click');
    assert(!input.readOnly && !elements['referrer-entry'].hidden && elements['referrer-preview'].hidden);
    await form.fire('submit');
    input.value = '0000000000000000';
    reply = {ok: false, status: 400, json: async () => ({message: '你已綁定其他推薦人，不能重複或改綁。'})};
    await form.fire('submit');
    assert.equal(calls.at(-1).url, form.action);
    assert.equal(calls.at(-1).options.body.get('code'), 'FEDCBA9876543210', 'confirmation binds only the code shown in preview');
    assert(dialog.open && elements['referrer-error'].textContent.includes('不能重複或改綁'));
    assert(form.querySelectorAll().every(button => !button.disabled), 'failed request can be retried or cancelled');
    elements['close-referrer'].fire('click');
    assert(!dialog.open && input.value === '' && elements['referrer-preview'].hidden);
    elements['open-referrer'].fire('click');
    input.value = 'FEDCBA9876543210';
    reply = {redirected: true}; await form.fire('submit');
    assert(elements['referrer-error'].textContent.includes('登入已過期'));
    reply = {ok: false, json: async () => { throw new Error('HTML response'); }};
    await form.fire('submit');
    assert(elements['referrer-error'].textContent.includes('重新整理'));
    reply = {}; await form.fire('submit');
    reply = {json: async () => ({success: true})}; await form.fire('submit');
    assert(!dialog.open && !elements['referrer-bound'].hidden && elements['referrer-unbound'].hidden);
    assert.equal(elements['bound-name'].textContent, '<script>example</script>');
    assert.equal(elements['bound-code'].textContent, 'FEDCBA9876543210');
    assert.equal(elements['binding-status'].textContent, '推薦人綁定成功。');
    assert(heading.focused);
    assert(calls.every(call => call.options.method === 'POST' && call.options.credentials === 'same-origin' && call.options.cache === 'no-store'));
    vm.runInNewContext(source, {document: {querySelector: () => null}}); // Already-bound page has no dialog.
    console.log('PASS: referral dialog preview/confirmation, CSRF, text escaping, pending requests, errors, retry/cancel, authenticated expiry and bound state');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
