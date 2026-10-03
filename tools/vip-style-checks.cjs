const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '../YoMi_Frontend');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const css = read('wwwroot/css/site.css');
const style = css.match(/\.vip-supreme \.vip-tier-name,\s*\.vip-supreme \.vip-discount\s*\{([^}]+)\}/)[1];
assert(style.includes('oklch(0.92 0.22 95) 0%'));
assert(style.includes('oklch(0.85 0.20 195) 25%'));
assert(style.includes('oklch(0.90 0.25 320) 50%'));
assert(style.includes('background-size: 250% auto'));
assert(style.includes('background-clip: text'));
assert(style.includes('animation: supreme-neon 3s linear infinite'));
assert(css.includes('100% { background-position: 250% center; }'));
assert(/@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.vip-supreme \.vip-tier-name,\s*\.vip-supreme \.vip-discount \{ animation: none; \}/.test(css));
for (const view of ['Home/Index', 'Home/Price', 'Home/Vip', 'Member/Index', 'Admin/Index']) {
    const source = read('Views/' + view + '.cshtml');
    const names = [...source.matchAll(/@(tier\.Name|Model\.Status\.Tier\.Name|item\.Status\.Tier\.Name)/g)];
    assert(names.length > 0, view);
    for (const name of names) {
        assert(source.slice(0, name.index).endsWith('class="vip-tier-name">'), view + ' tier name');
    }
    assert(!/class="vip-tier-name">[^<]*@(tier\.Icon|tier\.DiscountLabel|tier\.Tag)/.test(source));
    if (view === 'Home/Price') assert(source.includes('<strong class="vip-discount">@discount</strong>'));
    else assert(!source.includes('class="vip-discount"'), view + ' discount unchanged');
}
console.log('PASS: original supreme gradient/timing, tier names and price-only discount, separate icons/badges and reduced-motion support');
