const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
let playwright;
try {
  playwright = require('playwright');
} catch {
  playwright = require('../../zhihu-desktop/node_modules/playwright');
}

function readScript(file, name) {
  const source = fs.readFileSync(path.join(__dirname, '..', 'src', file), 'utf8');
  const match = new RegExp('const string ' + name + '=@"((?:[^"]|"")*)";').exec(source);
  assert.ok(match, 'Missing platform script ' + name);
  return match[1].replace(/""/g, '"');
}

const filters = readScript('BaijiahaoCreatorMetrics.cs', 'BaijiahaoPublishedFilters');
const traffic = readScript('BaijiahaoCreatorMetrics.cs', 'BaijiahaoTrafficTab');
const controls = readScript('BaijiahaoCreatorPublish.cs', 'BaijiahaoPublishControls');
const bodyCover = readScript('BaijiahaoCreatorPublish.cs', 'BaijiahaoBodyCoverControls');
const editor = fs.readFileSync(path.join(__dirname, '..', 'src', 'creator-editor.js'), 'utf8');
const origin = 'https://baijiahao.baidu.com';
const draft = origin + '/builder/rc/edit?type=news&article_id=12345678';

(async () => {
  const browser = await playwright.chromium.launch({channel: 'msedge', headless: true});
  try {
    const page = await browser.newPage();
    const tab = (label, active = false) => '<div class="cheetah-tabs-tab' +
      (active ? ' cheetah-tabs-tab-active' : '') + '"><div class="cheetah-tabs-tab-btn">' + label + '</div></div>';
    const tabBehavior = '<script>for(const tab of document.querySelectorAll(".cheetah-tabs-tab")){' +
      'tab.onclick=()=>{for(const s of tab.parentElement.children)s.classList.remove("cheetah-tabs-tab-active");tab.classList.add("cheetah-tabs-tab-active");}};</script>';
    const content = Object.fromEntries([
      ['/builder/rc/content', '<div>' + tab('全部', true) + tab('图文') + tab('视频') + '</div><div>' +
        tab('全部', true) + tab('已发布') + tab('草稿') + '</div>' + tabBehavior],
      ['/builder/rc/analysiscontent', '<div>' + tab('整体数据', true) + '</div><div>' +
        tab('全部', true) + tab('图文') + tab('视频') + '</div><div class="cheetah-picker-suffix">近7天</div>' + tabBehavior],
      ['/builder/rc/edit', [
        '<button id="outside">发布</button>',
        '<div class="cheetah-form-item"><div class="cover-radio-group"><label><input type="radio" checked>单图</label></div>',
        '<img id="cover" class="FeEditorApp-real-coverImg" hidden src="/cover.png"></div>',
        '<label><input id="podcast" type="checkbox" checked>自动生成播客</label>',
        '<label><input id="declaration" type="checkbox" checked>采用AI生成内容</label>',
        '<div class="editor-component-operator"><div class="op-list-wrap-news"><div class="op-list-right">',
        '<div class="op-btn-outter-content"><button class="cheetah-btn">存草稿</button></div>',
        '<div class="op-btn-outter-content"><button class="cheetah-btn">预览</button></div>',
        '<div class="op-btn-outter-content"><button class="cheetah-btn">定时发布</button></div>',
        '<div class="op-btn-outter-content"><button id="submit" class="cheetah-btn cheetah-btn-primary">发布</button></div></div></div></div>',
        '<script>window.submissions=0;window.outside=0;',
        'document.querySelector("#submit").onclick=()=>window.submissions++;',
        'document.querySelector("#outside").onclick=()=>window.outside++;</script>',
      ].join('')],
    ]);
    await page.route('**/*', (route) => {
      const url = new URL(route.request().url());
      if (url.origin !== origin) return route.abort();
      if (['/cover.png','/body.png','/wrong.png'].includes(url.pathname)) {
        const png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==';
        return route.fulfill({contentType: 'image/png', body: Buffer.from(png, 'base64')});
      }
      if (!content[url.pathname]) return route.abort();
      return route.fulfill({contentType: 'text/html; charset=utf-8', body: content[url.pathname]});
    });

    await page.goto(origin + '/builder/rc/content');
    assert.equal(await page.evaluate(filters), false);
    assert.equal(await page.evaluate(filters), false);
    assert.equal(await page.evaluate(filters), true);
    assert.deepEqual(await page.locator('.cheetah-tabs-tab-active').allTextContents(), ['图文', '已发布']);

    await page.goto(origin + '/builder/rc/analysiscontent');
    assert.equal(await page.evaluate(traffic + "('图文')"), false);
    assert.equal(await page.evaluate(traffic + "('图文')"), true);
    assert.equal(await page.evaluate(traffic + "('视频')"), false);
    assert.equal(await page.evaluate(traffic + "('视频')"), true);
    await page.locator('.cheetah-picker-suffix').evaluate(e => { e.textContent = '近30天'; });
    assert.equal(await page.evaluate(traffic + "('视频')"), false);

    await page.goto(draft);
    const run = (operation, address = draft) => page.evaluate(controls + '(' + JSON.stringify(address) + ',' +
      JSON.stringify(operation) + ')');
    assert.equal(await run('prepare'), 'cover');
    assert.ok(await page.locator('#podcast').isChecked());
    await page.locator('#cover').evaluate(e => { e.hidden = false; });
    await page.waitForFunction(() => document.querySelector('#cover').naturalWidth > 0);
    assert.equal(await run('publish', draft.replace('12345678', '87654321')), null);
    assert.equal(await run('publish'), null);
    assert.equal(await run('prepare'), 'ready');
    assert.equal(await page.locator('#podcast').isChecked(), false);
    assert.ok(await page.locator('#declaration').isChecked());
    await page.locator('body').evaluate(e => e.insertAdjacentHTML('beforeend', '<div role="dialog">待设置</div>'));
    assert.equal(await run('publish'), null);
    await page.locator('[role=dialog]').evaluate(e => e.remove());
    assert.equal(await run('publish'), 'clicked');
    assert.deepEqual(await page.evaluate(() => [window.submissions, window.outside]), [1, 0]);
    await page.reload();
    const coverStep = (step, image = origin + '/body.png') => page.evaluate(bodyCover + '(' +
      JSON.stringify(draft) + ',' + JSON.stringify(image) + ',' + JSON.stringify(step) + ')');
    const card = 'FeEditorApp-e8c90bfac9d4eab4-';
    const candidate = (file, id) => `<div class="${card}imgItem" id="${id}"><div class="${card}imgWrapper">` +
      `<img src="/${file}" class="${card}img"></div><span class="${card}checkbox">1</span>` +
      `<button class="${card}hotArea">选图</button></div>`;
    await page.locator('.cover-radio-group').evaluate((group,mark) => {
      group.insertAdjacentHTML('afterend', '<div class="FeEditorApp-_73a3a52aab7e3a36-content">' +
        '<div class="FeEditorApp-_73a3a52aab7e3a36-text">选择封面</div></div>');
      group.nextElementSibling.onclick=()=>{
        document.body.insertAdjacentHTML('beforeend',mark);
        const sheet=document.querySelector('[role=dialog]');
        const confirm=sheet.querySelector('#confirm-cover');
        for (const candidate of sheet.querySelectorAll('[id^=pick-]')) {
          candidate.querySelector('button').onclick=()=>{
            candidate.querySelector('span').classList.add('FeEditorApp-e8c90bfac9d4eab4-selected');
            confirm.textContent='确定 (1)';
            confirm.disabled=false;
          };
        }
        confirm.onclick=()=>{ document.querySelector('#cover').hidden=false;sheet.remove(); };
      };
    }, '<div class="cheetah-modal FeEditorApp-f3b5c632d7f1821c-tabsModal" role="dialog">' +
      '<span class="cheetah-tabs-tab-btn" role="tab" aria-selected="true">正文/本地上传</span>' +
      `<div class="cheetah-tabs-tabpane-active" role="tabpanel"><div class="${card}wrap">` +
      candidate('wrong.png', 'pick-wrong') + candidate('body.png', 'pick-word') +
      `<button disabled id="confirm-cover" class="${card}confirmBtn">确定</button></div></div></div>`);
    assert.equal(await coverStep('open'), 'opened');
    assert.equal(await coverStep('confirm'), null);
    assert.equal(await coverStep('select', origin + '/missing.png'), null);
    assert.equal(await coverStep('select'), 'selected');
    assert.equal(await coverStep('select', origin + '/wrong.png'), 'conflict');
    assert.ok(await page.locator('#pick-word span').evaluate(e=>e.className.endsWith('-selected')));
    assert.equal(await coverStep('confirm'), 'confirmed');
    assert.equal(await coverStep('ready'), 'ready');
    assert.equal(await coverStep('open'), 'conflict');
    assert.equal(await page.evaluate(() => window.submissions), 0);

    await page.reload();
    const localCover = step => page.evaluate(bodyCover + '(' + JSON.stringify(draft) + ',null,' + JSON.stringify(step) + ')');
    const uploadedCard = (file, id) => candidate(file, id).replace('</button></div>',
      '</button><button class="' + card + 'deleteBtn">删除</button></div>');
    await page.locator('.cover-radio-group').evaluate((group,fixture) => {
      const trigger=document.createElement('div');
      trigger.className='FeEditorApp-_73a3a52aab7e3a36-content';
      trigger.innerHTML='<div class="FeEditorApp-_73a3a52aab7e3a36-text">选择封面</div>';
      group.after(trigger);
      trigger.onclick=()=>{
        document.body.insertAdjacentHTML('beforeend',fixture.modal);
        const dialog=document.querySelector('[role=dialog]');
        const select=dialog.querySelector('input[type=file]');
        const confirm=dialog.querySelector('#confirm-local');
        select.onchange=()=>{
          if(select.files.length!==1 || select.files[0].type!=='image/png')return;
          confirm.insertAdjacentHTML('beforebegin',fixture.newCard);
          const selected=dialog.querySelector('#pick-uploaded');
          selected.querySelector('button').onclick=()=>{
            selected.querySelector('span').classList.add(fixture.selectedStyle);
            confirm.disabled=false;
            confirm.textContent='确定 (1)';
          };
        };
        confirm.onclick=()=>{ document.querySelector('#cover').hidden=false;dialog.remove(); };
      };
    }, {
      selectedStyle:card+'selected',
      newCard:uploadedCard('cover.png','pick-uploaded'),
      modal:'<div class="cheetah-modal FeEditorApp-f3b5c632d7f1821c-tabsModal" role="dialog">' +
        '<span class="cheetah-tabs-tab-btn" role="tab" aria-selected="true">正文/本地上传</span>' +
        '<div class="cheetah-tabs-tabpane-active" role="tabpanel"><div class="' + card + 'wrap">' +
        '<span class="cheetah-upload">本地上传<input hidden type="file" accept="image/*"></span>' +
        uploadedCard('wrong.png','pick-stale')+
        '<button disabled id="confirm-local" class="' + card + 'confirmBtn">确定</button></div></div></div>'
    });
    assert.equal(await localCover('open'), 'opened');
    assert.equal(await localCover('upload-control'), 'upload-ready');
    assert.equal(await localCover('select'), null, '不能选中原来已经存在的上传图');
    const uploadInput=page.locator('input[data-zhigao-baijiahao-title-cover]');
    await uploadInput.setInputFiles({name:'zhigao-title-cover-test.png',mimeType:'image/png',
      buffer:Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==','base64')});
    assert.equal(await localCover('select'), 'selected');
    assert.ok(await page.locator('#pick-uploaded span').evaluate(e=>e.className.endsWith('-selected')));
    assert.equal(await localCover('confirm'), 'confirmed');
    await page.waitForFunction(() => document.querySelector('#cover').naturalWidth > 0);
    assert.equal(await localCover('ready'), 'ready');
    assert.equal(await page.evaluate(() => window.submissions), 0);

    // The live picker auto-selects a same-origin blob before the crop canvas has painted.
    await page.locator('#cover').evaluate(el=>{el.hidden=true;});
    assert.equal(await localCover('open'),'opened');
    await page.evaluate(()=>{delete window.__zhigaoBaijiahaoTitleCover;});
    assert.equal(await localCover('upload-control'),'upload-ready');
    await page.locator('input[data-zhigao-baijiahao-title-cover]').setInputFiles({
      name:'zhigao-title-cover-blob.png',mimeType:'image/png',
      buffer:Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==','base64')
    });
    await page.locator('#pick-uploaded').evaluate((target,css)=>{
      target.querySelector('img').src=URL.createObjectURL(document.querySelector('input[type=file]').files[0]);
      target.querySelector('button').click();
      const crop=document.createElement('div');
      crop.className=css+'cropper';
      crop.innerHTML='<canvas class="preview-imgCanvas" width="20" height="15"></canvas>';
      target.after(crop);
    },card);
    assert.equal(await localCover('select'),'selected');
    assert.equal(await localCover('confirm'),null,'裁剪画布未绘制时不确认');
    await page.locator('canvas').evaluate(c=>c.getContext('2d').fillRect(0,0,c.width,c.height));
    await page.waitForFunction(bodyCover+'('+JSON.stringify(draft)+",null,'confirm')==='confirmed'");
    assert.equal(await localCover('ready'),'ready');
    assert.equal(await page.evaluate(()=>window.submissions),0);
    const freshEditor = origin + '/builder/rc/edit?type=news';
    await page.goto(freshEditor);
    await page.setContent([
      '<div class="input-box"><div contenteditable="true" style="height:38px;width:300px"><p><br></p></div></div>',
      '<div class="news-editor-pc" contenteditable="true"><p><br></p></div>',
      '<button id="covering-layer" style="position:fixed;top:0;left:0;width:360px;height:50px">悬浮工具栏</button>',
      '<script>window.enteredTitle=null;',
      'document.querySelector(".input-box > div").addEventListener("input", event=>{',
      '  if (event.isTrusted) window.enteredTitle=event.target.textContent;',
      '});</script>',
    ].join(''));
    const editorConfig = {
      address: freshEditor,
      titleFields: '.input-box > [contenteditable=true],.input-box textarea',
      bodyFields: "iframe[id^='ueditor'],.news-editor-pc[contenteditable=true]"
    };
    const action = (operation, data) => page.evaluate('(' + editor + ')(' +
      JSON.stringify(operation) + ',' + JSON.stringify(editorConfig) + ',' + JSON.stringify(data ?? null) + ')');
    const field = await page.locator('.input-box > div').boundingBox();
    const point = { x: field.x + field.width / 2, y: field.y + field.height / 2 };
    const cdp = await page.context().newCDPSession(page);
    const title = 'AI时代程序员的出路思考';
    for (const type of ['mousePressed','mouseReleased']) {
      await cdp.send('Input.dispatchMouseEvent',{type,button:'left',clickCount:1,x:point.x,y:point.y});
    }
    await cdp.send('Input.insertText',{text:title});
    assert.equal((await action('inspect')).title, '', '旧版坐标点击会被浮层拦住');
    assert.equal(await action('prepare-title',{title}), 'started');
    assert.equal((await action('inspect')).title, title);
    assert.equal(await page.evaluate(() => window.enteredTitle), title);
    assert.equal(await action('prepare-title',{title:'覆盖现有标题'}), 'occupied');
    assert.equal((await action('inspect')).title, title);
    await page.locator('.input-box > div').evaluate(e => { e.innerHTML = '<p><br></p>'; });
    await page.locator('.news-editor-pc').evaluate(e => { e.textContent = '保留已有正文'; });
    assert.equal(await action('prepare-title',{title:'替换已有正文的标题'}), 'occupied');
    assert.equal((await action('inspect')).title, '');
    console.log('PASS 百家号数据筛选、封面、唯一发布按钮、标题受遮挡时的可信输入和草稿保护');
  } finally {
    await browser.close();
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
