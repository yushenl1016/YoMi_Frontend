// Run: node tools/member-menu-checks.cjs (no packages or real member data).
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../YoMi_Frontend');
const source = fs.readFileSync(path.join(root, 'wwwroot/js/site.js'), 'utf8');
class Element {
    constructor() { this.listeners = {}; this.textContent = ''; this.disabled = true; this.open = false; }
    addEventListener(event, handler) { this.listeners[event] = handler; }
    fire(event, args = {}) { return this.listeners[event]?.(args); }
    focus() { this.focused = true; }
}
function widget(dropdown) {
    const menu = dropdown ? new Element() : null;
    const summary = new Element(), code = new Element(), copy = new Element(), status = new Element(), link = new Element();
    const mobileNav = new Element();
    const panel = {
        dataset: {codeUrl: '/virtual-site/my-records/referral-code'},
        closest: () => menu,
        querySelector: selector => ({'[data-referral-code]': code, '[data-copy-referral]': copy, '[data-referral-status]': status})[selector]
    };
    if (menu) {
        menu.querySelector = selector => selector === '.member-referrer-link' ? link : summary;
        menu.contains = target => target === copy || target === summary;
        menu.closest = () => mobileNav;
    }
    return {menu, summary, panel, code, copy, status, link, mobileNav};
}
async function run() {
    const desktop = widget(true), mobile = widget(true), inline = widget(false);
    const widgets = [desktop, mobile, inline], calls = [], copied = [], clickHandlers = [];
    let responseOk = false, resolvePending;
    const navigator = {clipboard: {writeText: async text => { copied.push(text); }}};
    const document = {
        querySelector: () => null,
        querySelectorAll: selector => selector === '[data-referral]' ? widgets.map(x => x.panel)
            : selector === '[data-member-menu]' ? [desktop.menu, mobile.menu] : [],
        addEventListener: (event, handler) => { assert.equal(event, 'click'); clickHandlers.push(handler); }
    };
    vm.runInNewContext(source, {document, navigator, fetch: async (url, options) => {
        calls.push({url, cache: options.cache, credentials: options.credentials});
        if (responseOk === 'pending') await new Promise(resolve => { resolvePending = resolve; });
        return {ok: responseOk !== false, json: async () => ({code: '0123456789ABCDEF'})};
    }});
    await new Promise(setImmediate);
    assert.equal(calls.length, 1, 'inline code loads immediately; closed menus do not request data');
    assert.equal(inline.code.textContent, '暫時無法取得');
    desktop.menu.open = true;
    await desktop.menu.fire('toggle');
    assert.equal(desktop.code.textContent, '暫時無法取得');
    assert(desktop.copy.disabled);
    responseOk = 'pending';
    const pending = desktop.menu.fire('toggle');
    await desktop.menu.fire('toggle');
    assert.equal(calls.length, 3, 'reopening during a pending request does not duplicate it');
    resolvePending(); await pending;
    assert.equal(desktop.code.textContent, '0123456789ABCDEF');
    assert.equal(desktop.copy.disabled, false);
    await desktop.menu.fire('toggle');
    assert.equal(calls.length, 3, 'loaded permanent code is reused');
    responseOk = true;
    mobile.menu.open = true;
    await mobile.menu.fire('toggle');
    assert.equal(mobile.code.textContent, desktop.code.textContent);
    assert(calls.every(call => call.url === '/virtual-site/my-records/referral-code' && call.cache === 'no-store' && call.credentials === 'same-origin'));
    await desktop.copy.fire('click', {stopPropagation() {}});
    assert.deepEqual(copied, ['0123456789ABCDEF']);
    assert.equal(desktop.status.textContent, '推薦碼已複製');
    navigator.clipboard = undefined;
    await mobile.copy.fire('click', {stopPropagation() {}});
    assert.equal(mobile.copy.textContent, '請選取複製');
    assert.equal(mobile.code.textContent, '0123456789ABCDEF');
    let stopped = false;
    desktop.menu.fire('keydown', {key: 'Escape', preventDefault() {}, stopPropagation() {stopped = true;}});
    assert(!desktop.menu.open && desktop.summary.focused && stopped);
    clickHandlers.forEach(handler => handler({target: mobile.copy}));
    assert(mobile.menu.open, 'clicking within a menu keeps it open');
    clickHandlers.forEach(handler => handler({target: {}}));
    assert(!mobile.menu.open, 'outside click closes menu');
    mobile.menu.open = mobile.mobileNav.open = true;
    mobile.link.fire('click');
    assert(!mobile.menu.open && !mobile.mobileNav.open, 'referrer anchor closes both menus so the target card is visible');
    // A fresh page renders the same component inline and copies without opening a menu.
    const inlineOnly = widget(false);
    document.querySelectorAll = selector => selector === '[data-referral]' ? [inlineOnly.panel] : [];
    navigator.clipboard = {writeText: async text => { copied.push(text); }};
    vm.runInNewContext(source, {document, navigator, fetch: async () => ({ok: true, json: async () => ({code: '0123456789ABCDEF'})})});
    await new Promise(setImmediate);
    assert.equal(inlineOnly.code.textContent, '0123456789ABCDEF');
    await inlineOnly.copy.fire('click', {stopPropagation() {}});
    assert.equal(copied.length, 2);
    console.log('PASS: desktop/mobile menu, inline display, lazy loading, retry, pending requests, code reuse, copy/fallback, Escape and outside click');
}
run().catch(error => {console.error(error); process.exitCode = 1;});
