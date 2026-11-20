const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
let playwright;
try { playwright = require('playwright'); }
catch { playwright = require('../../zhihu-desktop/node_modules/playwright'); }
const source = fs.readFileSync(path.join(__dirname, '../src/ZhihuTopics.cs'), 'utf8');
const script = name => {
  const marker = 'const string ' + name + ' = @"';
  const start = source.indexOf(marker);
  assert.notEqual(start, -1);
  return source.slice(start + marker.length, source.indexOf('";', start + marker.length)).replaceAll('""','"');
};
const controls = script('ZhihuTopicControls');
const phrases = script('ZhihuSearchPhrases');

(async () => {
  const browser = await playwright.chromium.launch({channel:'msedge', headless:true});
  try {
    const page = await browser.newPage();
    const fixture = '<textarea placeholder="请输入标题"></textarea><div class="public-DraftEditor-content"></div>' +
      '<div><label>文章话题</label><div><div>已有话题</div><button><svg class="ZDI--CircleClose24"></svg></button></div>' +
      '<label><input aria-label="搜索话题"><button><svg class="ZDI--CircleClose24"></svg></button></label></div>';
    await page.route('**/*',route=>route.fulfill({contentType:'text/html;charset=utf-8',body:fixture}));
    await page.goto('https://zhuanlan.zhihu.com/p/123/edit');
    const inspect = (title,code) => page.evaluate('(()=>{const expectedTitle='+JSON.stringify(title)+';'+controls+code+'})()');
    const set = (title,body) => page.evaluate(([t,b])=>{
      document.querySelector('textarea').value=t;
      document.querySelector('.public-DraftEditor-content').textContent=b;
    },[title,body]);
    const title = 'AI时代程序员的出路思考';
    await set(title,'AI与程序员。AI与程序员。AI与程序员。');
    assert.deepEqual(await inspect(title,phrases),['人工智能','程序员']);
    assert.deepEqual(await inspect(title,'return selected();'),['已有话题']);
    assert.equal(await inspect('其他标题',phrases),null);
    await set('蓝莓种植和土壤','蓝莓的酸性土壤。蓝莓、土壤需要调节，种植时需确认。');
    const related = await inspect('蓝莓种植和土壤',phrases);
    assert(related.includes('蓝莓'));
    assert(related.includes('土壤'));
    assert(!related.includes('人工智能'));
    await set('今日思考','花谢了。');
    assert.deepEqual(await inspect('今日思考',phrases),[]);
    await page.goto('https://zhuanlan.zhihu.com/p/123');
    await set(title,'AI与程序员。');
    assert.equal(await inspect(title,phrases),null);
    console.log('PASS article-specific exact topics, selected chips, title and draft guards');
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});
