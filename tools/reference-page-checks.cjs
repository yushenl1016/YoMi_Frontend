// Run without browser, packages or database: node tools/reference-page-checks.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../YoMi_Frontend');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');
const source = read('wwwroot/js/reference-pages.js');
class Element {
    constructor() { this.events = {}; this.attrs = {}; this.hidden = false; this.value = 'Siens'; this.classes = new Set(); this.classList = {toggle: (key, value) => value ? this.classes.add(key) : this.classes.delete(key)}; }
    addEventListener(name, handler) { this.events[name] = handler; }
    fire(name, event = {}) { return this.events[name]?.(event); }
    setAttribute(name, value) { this.attrs[name] = value; }
    removeAttribute(name) { delete this.attrs[name]; }
    focus() { this.focused = true; }
    select() { this.selected = true; }
    replaceChildren(...children) { this.children = children; }
}
const input = new Element(), editor = new Element(), greeting = new Element(), edit = new Element(), cancel = new Element();
editor.hidden = true;
editor.querySelector = selector => selector === 'input[name="name"]' ? input : cancel;
editor.reset = () => { input.value = 'Siens'; };
const selectors = {'[data-name-editor]': editor, '[data-member-greeting]': greeting, '[data-edit-name]': edit, '.price-tabs': null};
vm.runInNewContext(source, {document: {querySelector: selector => selectors[selector]}});
edit.fire('click'); assert(!editor.hidden && greeting.hidden && input.focused && input.selected);
input.value = 'not saved'; cancel.fire('click'); assert(editor.hidden && !greeting.hidden && edit.focused); assert.equal(input.value, 'Siens');
edit.fire('click'); let prevented = false;
editor.fire('keydown', {key: 'Escape', preventDefault: () => { prevented = true; }}); assert(prevented && editor.hidden);
edit.fire('click'); input.value = '  '; prevented = false;
editor.fire('submit', {preventDefault: () => { prevented = true; }}); assert(prevented && editor.hidden);
edit.fire('click'); input.value = '  New Name  '; prevented = false;
editor.fire('submit', {preventDefault: () => { prevented = true; }}); assert(!prevented); assert.equal(input.value, 'New Name');

async function checkTabs() {
    const tabs = new Element(), results = new Element(), links = [new Element(), new Element()];
    links.forEach((link, i) => {link.href = 'https://localhost/price?tab=' + i; link.closest = () => link;});
    tabs.querySelectorAll = () => links;
    let mode = 'success', pushed, assigned, pending = [], reloads = 0, popstate;
    const document = {querySelector: selector => selector === '.price-tabs' ? tabs : selector === '[data-price-results]' ? results : null};
    const fetch = async (url, options) => {
        assert.equal(options.credentials, 'same-origin');
        if (mode === 'error') throw new Error('offline');
        if (mode === 'race') return new Promise((resolve, reject) => {
            options.signal.addEventListener('abort', () => reject(Object.assign(new Error('abort'), {name:'AbortError'})));
            pending.push(() => resolve({ok:true,text: async () => url}));
        });
        return {ok:true,text: async () => url};
    };
    class Parser { parseFromString(text) { return {querySelector: () => ({childNodes: [text]})}; } }
    vm.runInNewContext(source, {document, fetch, AbortController, DOMParser: Parser, history: {pushState: (a,b,url) => {pushed=url;}}, window: {location:{assign:url => {assigned=url;},reload:() => {reloads++;}}, addEventListener: (name, fn) => {popstate=fn;}}});
    const click = (index, extra = {}) => tabs.fire('click', {target:links[index],button:0,preventDefault(){},...extra});
    await click(0); assert.equal(pushed, links[0].href); assert.equal(results.children[0], pushed); assert.equal(links[0].attrs['aria-current'], 'page'); assert(!results.attrs['aria-busy']);
    await click(1); assert.equal(pushed,links[1].href); assert(!links[0].attrs['aria-current']);
    await click(0,{ctrlKey:true}); assert.equal(pushed,links[1].href);
    mode='race'; const first=click(0); const second=click(1); pending[1](); await Promise.all([first,second]); assert.equal(pushed,links[1].href); assert.equal(assigned,undefined);
    mode='error'; await click(0); assert.equal(assigned,links[0].href); assert(!results.attrs['aria-busy']);
    popstate(); assert.equal(reloads,1);
}
for (const view of ['Home/Price','Home/Vip','Member/Index']) assert(read('Views/' + view + '.cshtml').includes('ViewData["ReferencePage"] = true'));
assert(!read('Views/Home/Index.cshtml').includes('ViewData["ReferencePage"] = true'));
assert(read('Views/Member/Index.cshtml').includes('@Html.AntiForgeryToken()'));
assert(read('Views/Member/Index.cshtml').includes('maxlength="64" required'));
assert(read('Views/Shared/_ReferenceProgress.cshtml').includes('System.Globalization.CultureInfo.InvariantCulture'));
assert(read('Views/Shared/_ReferenceProgress.cshtml').includes('@Model.Progress'));
assert(read('Views/Home/Price.cshtml').includes('@item.Price'));
checkTabs().then(() => console.log('PASS: name edit/cancel/Escape/validation, price switching/history/race/fallback, scoped pages, existing data and progress bindings')).catch(error => {console.error(error); process.exitCode=1;});
