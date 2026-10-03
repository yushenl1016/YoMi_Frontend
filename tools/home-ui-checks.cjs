// Run: node tools/home-ui-checks.cjs (no packages, browser or database required).
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../YoMi_Frontend');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const home = read('Views/Home/Index.cshtml');
const layout = read('Views/Shared/_Layout.cshtml');
const css = read('wwwroot/css/home.css');
assert(home.includes('ViewData["HomeReplica"] = true'));
assert(layout.includes('var isHome = ViewData["HomeReplica"] is true'));
assert(layout.includes('@if (!isHome)'));
assert.equal((home.match(/class="home-service"/g) || []).length, 3);
assert.equal((home.match(/class="home-footer"/g) || []).length, 1);
assert(home.includes('VipRules.Tiers'));
assert(home.includes('tier.Discount * 100'));
assert(home.includes('tier.Level == 3 ? "97折起"'));
assert(home.includes('discount = "9折"'));
for (const setting of ['site_name', 'site_description', 'site_subtitle', 'announcement', 'footer_text', 'contact_discord', 'contact_line']) {
    assert(home.includes(`Model.Get("${setting}"`), setting);
}
for (const label of ['護航服務', '趣味單', '代儲值', '陪玩服務', '代打代練', '帳號安全', 'VIP 福利', '精英打手']) assert(home.includes(label));
assert(home.includes('copy < 2'));
assert(home.includes('aria-hidden="@(copy == 1 ? "true" : "false")"'));
assert(css.includes('home-marquee 22s linear infinite'));
assert(css.includes('home-float 3.5s ease-in-out infinite'));
assert(css.includes('@media (prefers-reduced-motion: reduce)'));
assert(css.includes('animation: none'));
assert(css.includes('.home-page .brand-panel { width: 100%; order: 1;'));
assert(css.includes('.home-page .hero-copy { width: 100%; order: 2;'));
assert(!home.includes('cdn.discordapp.com'));
assert(layout.includes('@Html.AntiForgeryToken()'));
assert(!layout.includes('href="/admin"'), 'desktop and mobile navigation have no frontend admin entry');
const sprite = read('wwwroot/images/home-icons.svg');
for (const id of ['zap','shield','trophy','star','chevron','user','logout','volume-muted','volume','menu','close']) assert(sprite.includes(`id="${id}"`));

class Element {
    constructor(attrs = {}) { this.attrs = attrs; this.listeners = {}; this.value = '0'; this.open = true; }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    getAttribute(name) { return this.attrs[name]; }
    setAttribute(name, value) { this.attrs[name] = value; }
    fire(name, event = {}) { this.listeners[name]?.(event); }
}
function checkVolume(withVideo) {
    const sliders = [new Element(),new Element()];
    const labels = [new Element(),new Element()];
    const buttons = [new Element(),new Element()];
    const icons = [new Element({href:'/images/home-icons.svg#volume-muted'})];
    const mobileVolume = new Element();
    const panels = [new Element(),new Element()];
    const video = withVideo ? {} : null;
    const document = new Element();
    document.querySelector = () => video;
    const selectors = {
        '[data-home-volume]':sliders, '[data-home-volume-label]':labels,
        '[data-home-volume-icon]':icons, '[data-home-mute]':buttons,
        '.mobile-nav .home-volume':[mobileVolume], '.header-actions .home-volume, .mobile-nav':panels
    };
    document.querySelectorAll = selector => {
        assert(selector in selectors, selector);
        return selectors[selector];
    };
    vm.runInNewContext(read('wwwroot/js/home.js'), {document});
    assert.equal(labels[0].textContent, '靜音');
    assert.equal(mobileVolume.open, true);
    buttons[0].fire('click');
    assert.equal(labels[1].textContent, '50%');
    assert.equal(buttons[1].attrs['aria-pressed'], 'true');
    assert.equal(icons[0].attrs.href, '/images/home-icons.svg#volume');
    sliders[1].value = '72'; sliders[1].fire('input');
    assert.equal(sliders[0].value, '72');
    if (video) { assert.equal(video.volume, .72); assert.equal(video.muted, false); }
    sliders[0].value = '0'; sliders[0].fire('input');
    assert.equal(labels[1].textContent, '靜音');
    if (video) assert.equal(video.muted, true);
    buttons[1].fire('click'); assert.equal(sliders[0].value, '50');
    sliders[0].value = '200'; sliders[0].fire('input'); assert.equal(sliders[1].value, '100');
    sliders[0].value = '-5'; sliders[0].fire('input'); assert.equal(sliders[1].value, '0');
    sliders[0].value = 'bad'; sliders[0].fire('input'); assert.equal(labels[0].textContent, '靜音');
    document.fire('keydown', {key:'Escape'}); assert(panels.every(panel => !panel.open));
}
checkVolume(true);
checkVolume(false);
console.log('PASS: homepage structure, original labels/motion, scoped layout, settings binding, desktop/mobile volume sync, mute, bounds, Escape, and no-video fallback');
