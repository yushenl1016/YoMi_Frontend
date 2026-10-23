import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

function element(value = '') {
    return { value, textContent: '', handlers: {}, attributes: {}, addEventListener(event, handler) { this.handlers[event] = handler; },
        setAttribute(key, value) { this.attributes[key] = value; }, getAttribute() { return '/icons.svg#volume'; } };
}
for (const home of [true, false]) {
    const button = element(), slider = element('0'), label = element(), icon = element();
    const video = { muted: true, volume: 0, paused: true, calls: 0,
        play() { this.calls++; this.paused = false; return this.fail ? Promise.reject(new Error('blocked')) : Promise.resolve(); } };
    const map = { '[data-background-video]': video, '[data-sound-toggle]': button };
    const lists = { '[data-home-volume]': [slider], '[data-home-volume-label]': [label],
        '[data-home-volume-icon]': [icon], '[data-home-mute]': [button] };
    const document = { querySelector: key => map[key] ?? null, querySelectorAll: key => lists[key] ?? [], addEventListener() {} };
    vm.runInNewContext(readFileSync(new URL('../YoMi_Frontend/wwwroot/js/' + (home ? 'home.js' : 'site.js'), import.meta.url), 'utf8'), { document, window: {} });
    button.handlers.click();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(video.muted, false, 'Click must unmute the MP4.');
    assert.equal(video.calls, 1, 'Click must resume playback in the user gesture.');
    assert.ok(video.volume > 0, 'Sound must have audible volume.');
    if (home) {
        slider.value = '70';
        slider.handlers.input();
        assert.equal(video.volume, .7);
        assert.equal(video.muted, false);
    }
    button.handlers.click();
    assert.equal(video.muted, true);
    video.fail = true;
    button.handlers.click();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(video.muted, true, 'Rejected playback must restore muted UI state.');
}
console.log('PASS: MP4 sound click resumes playback, volume control, mute and rejected playback.');
