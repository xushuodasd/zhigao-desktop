const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
let playwright;
try {
  playwright = require('playwright');
} catch {
  playwright = require('../../zhihu-desktop/node_modules/playwright');
}

const source = fs.readFileSync(path.join(__dirname, '..', 'src', 'ToutiaoCreatorPublish.cs'), 'utf8');
const match = /const string ToutiaoPublishSettings=@"((?:[^"]|"")*)";/.exec(source);
assert.ok(match, 'Missing Toutiao settings script');
const settings = match[1].replace(/""/g, '"');
const address = 'https://mp.toutiao.com/profile_v4/graphic/publish?pgc_id=12345678';

const fixture = `
  <!doctype html><meta charset="utf-8">
  <style>
    .byte-radio-inner { display: inline-block; width: 12px; height: 12px; }
    .article-cover-add { width: 80px; height: 60px; }
    #chrome { position: fixed; inset: 0; z-index: 3; }
  </style>
  <div id="chrome"></div>
  <div class="article-cover">
    <div class="article-cover-radio-group">
      <label class="byte-radio">
        <input type="radio" name="cover" checked>
        <span class="byte-radio-inner checked"></span>单图
      </label>
      <label class="byte-radio" id="no-cover">
        <input type="radio" name="cover">
        <span class="byte-radio-inner"></span>无封面
      </label>
    </div>
    <div class="article-cover-images"><div class="article-cover-add"></div></div>
  </div>
  <label class="article-ad-radio"><span class="byte-radio-inner checked">保留广告选项</span></label>
  <div class="form-tuwen_wtt_trans">
    <label class="byte-checkbox item-checkbox"><input type="checkbox" checked>同时发布微头条</label>
  </div>
  <script>
    window.coverChanges=0;
    for (const radio of document.querySelectorAll('input[name=cover]')) {
      radio.onchange=()=>{
        window.coverChanges++;
        for(const input of document.querySelectorAll('input[name=cover]'))
          input.nextElementSibling.classList.toggle('checked',input.checked);
      };
    }
  </script>
`;

(async () => {
  const browser = await playwright.chromium.launch({channel: 'msedge', headless: true});
  try {
    const page = await browser.newPage();
    await page.route('**/*', route=>route.request().url()===address ?
      route.fulfill({contentType:'text/html; charset=utf-8',body:fixture}):route.abort());
    await page.goto(address);
    const evaluate = (prepare, direct = false) => page.evaluate(
      settings + '(' + JSON.stringify(address) + ',' + prepare + ',' + direct + ')');
    assert.equal(await evaluate(true, true), 'changed');
    assert.ok(!(await page.locator('input[type=checkbox]').isChecked()));
    assert.equal(await evaluate(true, true), 'changed-cover');
    assert.equal(await page.evaluate(() => window.coverChanges), 1);
    assert.equal(await evaluate(false), 'ready-no-micro');
    assert.equal(await page.locator('.article-ad-radio .checked').count(), 1);

    await page.reload();
    await page.locator('body').evaluate(e => e.insertAdjacentHTML('beforeend', '<div role="dialog">请确认</div>'));
    assert.equal(await evaluate(true, true), 'waiting-dialog');
    assert.equal(await page.evaluate(() => window.coverChanges), 0);
    assert.ok(await page.locator('input[type=checkbox]').isChecked());

    await page.reload();
    await page.locator('input[type=checkbox]').evaluate(e => { e.checked = false; });
    await page.locator('#no-cover input').evaluate(e => { e.disabled = true; });
    assert.match(await evaluate(true, true), /无法使用「无封面」/);
    assert.equal(await page.evaluate(() => window.coverChanges), 0);
    console.log('PASS 头条号无封面精确选择、选中状态核对、不同步微头条和禁用选项保护');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
