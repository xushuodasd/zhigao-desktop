const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
let playwright;
try {
  playwright = require('playwright');
} catch {
  playwright = require('../../zhihu-desktop/node_modules/playwright');
}

function script(file, name) {
  const source = fs.readFileSync(path.join(__dirname, '..', 'src', file), 'utf8');
  const match = new RegExp(`const string ${name}=@"((?:[^"]|"")*)";`).exec(source);
  assert.ok(match, `Missing platform script ${name}`);
  return match[1].replace(/""/g, '"');
}

const count = script('WeiboCreatorMetrics.cs', 'WeiboPublishedCountScript');
const daily = script('WeiboCreatorMetrics.cs', 'WeiboDailyMetricScript');
const control = script('WeiboCreatorPublish.cs', 'WeiboPublishControls');
const draft = 'https://card.weibo.com/article/v5/editor#/draft/1234567';

(async () => {
  const browser = await playwright.chromium.launch({channel: 'msedge', headless: true});
  try {
    const page = await browser.newPage();
    let articleResponse = {ok: 1, data: {}};
    const document = {
      '/content/article': '<div class="woo-tab-active">已发布</div><div class="_list_103bb_2"><div>没有更多内容了</div></div>',
      '/data/overview': '<div><span>昨日阅读数</span><span>124</span></div>',
      '/data/video': '<h3>您未开通视频号，快去开通吧~</h3>',
      '/article/v5/editor': `
        <button id="next">下一步</button><button id="outside">发布</button>
        <div class="publish-modal" hidden>
          <textarea>发布了头条文章</textarea>
          <span id="audience">公开</span>
          <div id="follow" role="checkbox" aria-checked="true"><span>关注@头条文章 第一时间获取官方通知</span></div>
          <div id="sync" role="checkbox" aria-checked="true"><span>同步到微博</span></div>
          <button id="submit">发布</button>
        </div>
        <script>
          window.submissions = 0;
          window.outsideClicks = 0;
          document.querySelector('#next').onclick = () => {document.querySelector('.publish-modal').hidden = false;};
          document.querySelector('#submit').onclick = () => {window.submissions++;};
          document.querySelector('#outside').onclick = () => {window.outsideClicks++;};
          for (const box of document.querySelectorAll('[role=checkbox]')) {
            box.onclick = () => box.setAttribute('aria-checked', String(box.getAttribute('aria-checked') !== 'true'));
          }
        </script>`
    };
    await page.route('**/*', route => {
      const url = new URL(route.request().url());
      if (url.href === 'https://card.weibo.com/test-cover.svg') {
        return route.fulfill({contentType: 'image/svg+xml',body: '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60"/>'});
      }
      if (url.origin === 'https://me.weibo.com' && url.pathname === '/api/article/artlist') {
        return route.fulfill({json: articleResponse});
      }
      if (['https://me.weibo.com', 'https://card.weibo.com'].includes(url.origin) && document[url.pathname]) {
        return route.fulfill({contentType: 'text/html; charset=utf-8', body: document[url.pathname]});
      }
      return route.abort();
    });

    await page.goto('https://me.weibo.com/content/article');
    let result = await page.waitForFunction(count);
    assert.deepEqual(await result.jsonValue(), {articleCount: '0'});
    articleResponse = {ok: 1, data: {totalnum: 109, list: [{}]}};
    await page.reload();
    result = await page.waitForFunction(count);
    assert.deepEqual(await result.jsonValue(), {articleCount: '109'});
    articleResponse = {ok: 0, data: {}};
    await page.reload();
    result = await page.waitForFunction(count);
    assert.deepEqual(await result.jsonValue(), {state: 'unavailable'});
    await page.goto('https://me.weibo.com/content/article?id=1');
    assert.equal(await page.evaluate(count), null);

    const reads = daily + "('/data/overview','reads',['昨日阅读数'])";
    await page.goto('https://me.weibo.com/data/overview');
    assert.deepEqual(await page.evaluate(reads), {reads: '124'});
    await page.locator('body').evaluate(e => e.insertAdjacentHTML('beforeend','<p>以下为单条博文分析示例</p>'));
    assert.deepEqual(await page.evaluate(reads), {state: 'unavailable', readsStatus: '官方页面仅展示示例'});
    await page.goto('https://me.weibo.com/data/video');
    assert.deepEqual(await page.evaluate(daily + "('/data/video','plays',['昨日播放量'])"), {state: 'unavailable'});

    await page.goto(draft);
    const call = action => page.evaluate(control + `('1234567', ${JSON.stringify(action)})`);
    assert.equal(await page.evaluate(control + "('0000000','next')"), false);
    assert.equal(await call('publish'), false);
    assert.equal(await call('next'), true);
    assert.equal(await call('ready'), false);
    assert.equal(await call('prepare'), true);
    assert.equal(await page.locator('#follow').getAttribute('aria-checked'), 'false');
    assert.equal(await page.locator('#sync').getAttribute('aria-checked'), 'false');
    await page.locator('#audience').evaluate(e => { e.textContent = '粉丝'; });
    assert.equal(await call('publish'), false);
    await page.locator('#audience').evaluate(e => { e.textContent = '公开'; });
    assert.equal(await call('ready'), true);
    assert.equal(await call('publish'), true);
    assert.deepEqual(await page.evaluate(() => [window.submissions, window.outsideClicks]), [1, 0]);
    await page.reload();
    await page.evaluate(() => {
      const next=document.querySelector('#next');
      next.onclick=()=>{
        document.body.insertAdjacentHTML('beforeend','<div class="n-message">自动为您选择了上一篇文章的封面图，建议您换一张</div><div class="cover-preview"><img class="cover-img" src="/test-cover.svg"></div>');
        next.onclick=()=>{document.querySelector('.publish-modal').hidden=false;};
      };
    });
    assert.equal(await call('continue-cover'), false);
    assert.equal(await call('next'), true);
    await page.waitForFunction(()=>document.querySelector('.cover-img')?.naturalWidth>0);
    await page.locator('.n-message').evaluate(e=>{e.hidden=true;});
    assert.equal(await call('continue-cover'), false);
    await page.locator('.n-message').evaluate(e=>{e.hidden=false;});
    assert.equal(await call('continue-cover'), true);
    assert.equal(await call('continue-cover'), false);
    assert.equal(await call('prepare'), true);
    assert.deepEqual(await page.evaluate(()=>[window.submissions,window.outsideClicks]),[0,0]);
    console.log('PASS 微博真实零值、完整文章数、排除示例与未开通项、同稿号与唯一公开按钮、移除额外同步和关注');
  } finally {
    await browser.close();
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
