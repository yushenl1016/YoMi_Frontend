import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const file = new URL('../../YoMi_Frontend/wwwroot/js/desktop-pet.js', import.meta.url);
function element() { return { textContent: '', children: [], append(...nodes) { this.children.push(...nodes); }, replaceChildren(...nodes) { this.children = nodes; }, setAttribute() {} }; }
async function render(fetch) {
  const list = element(), status = element();
  const document = { querySelector: selector => selector === '[data-pet-announcements]' ? list : selector === '[data-pet-announcements-status]' ? status : null, createElement: element };
  vm.runInNewContext(fs.readFileSync(file, 'utf8'), { document, fetch });
  await new Promise(resolve => setImmediate(resolve));
  return { list, status };
}
test('download page reads shared announcements using safe text nodes', async () => {
  const calls = [];
  const { list, status } = await render(async url => { calls.push(url); return { ok: true, json: async () => ({ announcements: [{ title: '<img src=x>', body: '測試公告', pinned: true }] }) }; });
  assert.deepEqual(calls, ['/api/desktop/announcements']);
  assert.equal(list.children[0].children[0].textContent, '置頂 · <img src=x>');
  assert.equal(list.children[0].children[1].textContent, '測試公告');
  assert.equal(status.textContent, '');
});
test('download page gives readable status when announcements unavailable', async () => {
  const { status } = await render(async () => { throw new Error('offline'); });
  assert.match(status.textContent, /暫時無法/);
});
