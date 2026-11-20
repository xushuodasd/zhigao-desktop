const assert = require('node:assert/strict');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
let playwright;
try {
  playwright = require('playwright');
} catch {
  playwright = require('../../zhihu-desktop/node_modules/playwright');
}

const pageURL = pathToFileURL(path.resolve(__dirname, '../src/dashboard.html')).href;
const accounts = [
  { id: 'zhihu', platform: '知乎', mark: '知', name: '作者甲', state: 'editor' },
  { id: 'csdn', platform: 'CSDN', mark: 'C', name: '作者乙', state: 'editor' }
];
const url = 'https://blog.csdn.net/example/article/details/123';
const history = [
  { file: '旧稿.docx', platform: 'CSDN', label: '待核实', detail: '没有收到回执。', time: '12:00' },
  { file: '旧稿.docx', platform: '知乎', label: '未发出', detail: '正文有差异。', time: '12:00' }
];

(async () => {
  const browser = await playwright.chromium.launch({
    channel: 'msedge', headless: true
  });
  try {
    const page = await browser.newPage({viewport:{width:1024,height:720}});
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.sent = [];
      (window.chrome ??= {}).webview = {postMessage: msg => window.sent.push(msg)};
    });
    await page.goto(pageURL);
    const render = data => page.evaluate(s => window.renderDesk(s), data);
    const batchBox = page.locator('#batchSection');
    await render({accounts, history, batch:[], article:{name:'新稿.docx',title:'新稿',bytes:50}, ready:true});
    assert.ok(await batchBox.isHidden());
    assert.equal(await page.locator('#targets input:checked').count(), 0, 'require explicit targets');
    assert.equal(await page.getByText('最近结果',{exact:true}).count(),0);
    assert.equal(await page.locator('#publishView').getByText('旧稿.docx',{exact:true}).count(),0);
    assert.ok(await page.locator('#publishArticle').isDisabled());
    await page.locator('[data-nav="accounts"]').click();
    const accountActions=page.locator('#accountRows').getByRole('button');
    assert.equal(await accountActions.count(),4);
    assert.equal(await accountActions.filter({hasText:/^打开$/}).count(),2);
    const analytics=page.locator('#zhihuAnalytics');
    const dataToggle=page.getByRole('button',{name:'知乎账号数据',exact:true});
    assert.ok(await analytics.isHidden());
    assert.equal(await page.evaluate(()=>window.sent.filter(action=>action.startsWith('stats:')).length),0);
    await dataToggle.press('Enter');
    assert.equal(await dataToggle.getAttribute('aria-expanded'),'true');
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'stats:zhihu');
    assert.equal(await analytics.evaluate(panel=>panel.previousElementSibling.querySelector('b').textContent),'知乎');
    await render({zhihuMetrics:{state:'loading'}});
    assert.ok(await analytics.getByRole('button',{name:'读取中…'}).isDisabled());
    await dataToggle.click();
    await render({zhihuMetrics:{state:'ready',articleCount:'2',reads:'5',plays:'0'}});
    assert.ok(await analytics.isHidden());
    assert.equal(await dataToggle.getAttribute('aria-expanded'),'false');
    await dataToggle.click();
    assert.equal(await page.locator('#analyticsPlays').textContent(),'0');
    await analytics.getByRole('button',{name:'刷新',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'stats:zhihu-refresh');
    const csdnAnalytics=page.locator('#csdnAnalytics');
    const csdnDataToggle=page.getByRole('button',{name:'CSDN账号数据',exact:true});
    await csdnDataToggle.click();
    assert.ok(await analytics.isHidden());
    assert.equal(await dataToggle.getAttribute('aria-expanded'),'false');
    assert.equal(await csdnDataToggle.getAttribute('aria-expanded'),'true');
    assert.equal(await csdnAnalytics.evaluate(panel=>panel.previousElementSibling.querySelector('b').textContent),'CSDN');
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'stats:csdn');
    await render({csdnMetrics:{state:'ready',articleCount:'12',reads:'2,316'}});
    assert.equal(await page.locator('#csdnAnalyticsArticles').textContent(),'12');
    assert.equal(await page.locator('#csdnAnalyticsReads').textContent(),'2,316');
    await csdnAnalytics.getByRole('button',{name:'刷新',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'stats:csdn-refresh');
    await csdnDataToggle.click();
    assert.ok(await csdnAnalytics.isHidden());

    const creatorAccount={id:'qiehao',platform:'企鹅号',mark:'企',name:'企鹅号作者甲',state:'saved',publishable:false,analytics:true};
    await render({accounts:[...accounts,creatorAccount]});
    assert.ok(await page.locator('#accountRows').getByText('企鹅号作者甲',{exact:true}).isVisible());
    const creatorPanel=page.locator('#qiehaoAnalytics');
    await page.getByRole('button',{name:'企鹅号账号数据',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'stats:qiehao');
    await render({creatorMetrics:{qiehao:{state:'ready',articleCount:'0',reads:'2,316',scope:'阅读为昨日数据'}}});
    assert.equal(await creatorPanel.locator('[data-metric=articleCount]').textContent(),'0');
    assert.equal(await creatorPanel.locator('[data-metric=reads]').textContent(),'2,316');
    assert.equal(await creatorPanel.locator('[data-metric=plays]').textContent(),'—');
    assert.equal(await creatorPanel.getByRole('status').textContent(),'阅读为昨日数据');
    await creatorPanel.getByRole('button',{name:'刷新',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'stats:qiehao-refresh');
    const toutiaoAccount={id:'toutiao',platform:'头条号',mark:'头',state:'connected',analytics:true};
    await render({accounts:[...accounts,toutiaoAccount]});
    await page.getByRole('button',{name:'头条号账号数据',exact:true}).click();
    const toutiaoPanel=page.locator('#toutiaoAnalytics');
    await render({creatorMetrics:{toutiao:{state:'loading',articleCount:'0',readsStatus:'平台昨日数据待更新'}}});
    assert.equal(await toutiaoPanel.locator('[data-metric=articleCount]').textContent(),'0');
    assert.equal(await toutiaoPanel.getByText('昨日图文阅读量',{exact:true}).count(),1);
    assert.equal(await toutiaoPanel.getByText('平台昨日数据待更新',{exact:true}).count(),1);
    await render({creatorMetrics:{toutiao:{state:'ready',articleCount:'0',readsStatus:'平台昨日数据待更新'}}});
    assert.equal(await toutiaoPanel.locator('[data-metric-status=plays]').textContent(),'暂无法读取');
    await render({creatorMetrics:{toutiao:{state:'stale',articleCount:'0'}}});
    assert.equal(await toutiaoPanel.locator('[data-metric=articleCount]').textContent(),'0');
    assert.match(await toutiaoPanel.getByRole('status').textContent(),/显示上次读到的数据/);
    await render({analyticsCollectedAt:{toutiao:'2026-11-20 15:00'},analyticsDays:{toutiao:[
      {date:'2026-11-20',articleCount:'0'}, {date:'2026-11-19',articleCount:'2',reads:'14',plays:'0'}
    ]}});
    assert.match(await toutiaoPanel.locator('[data-analytics-freshness]').textContent(),/每小时自动更新 · 上次读取 2026-11-20 15:00/);
    const daily=toutiaoPanel.getByRole('table',{name:'头条号每日统计',exact:true});
    assert.ok(await daily.isHidden());
    await toutiaoPanel.getByText('每日统计',{exact:true}).click();
    assert.ok(await daily.isVisible());
    assert.deepEqual(await daily.getByRole('columnheader').allTextContents(),['日期','已发布文章','当日图文阅读量','当日视频播放量']);
    assert.deepEqual(await daily.locator('tbody tr').nth(0).locator('td').allTextContents(),['0','—','—']);
    assert.deepEqual(await daily.locator('tbody tr').nth(1).locator('td').allTextContents(),['2','14','0']);
    const restoredWeibo={id:'weibo',platform:'新浪微博',mark:'微',name:'微博作者乙',state:'saved',publishable:true,canSubmit:true};
    await render({accounts:[...accounts,restoredWeibo]});
    await page.locator('[data-nav="publish"]').click();
    assert.ok(await page.locator('#targets').getByText('微博作者乙',{exact:true}).isVisible());
    await page.locator('input[data-target=weibo]').check();
    assert.match(await page.locator('#target').textContent(),/微博长文会关联公开动态/);
    await render({accounts:[...accounts,{...restoredWeibo,state:'signin',connected:true,canSubmit:false}]});
    assert.ok(await page.locator('input[data-target=weibo]').isDisabled());
    assert.match(await page.locator('#targets').innerText(),/需重新登录/);
    const sohu={id:'sohu',platform:'搜狐号',mark:'狐',name:'搜狐作者丙',state:'connected',connected:true,canSubmit:false};
    await render({accounts:[...accounts,{...sohu,publishBlocker:'checking'}]});
    const sohuRow=page.locator('#targets .compose-target').filter({hasText:'搜狐号'});
    assert.match(await sohuRow.innerText(),/正在检查发布权限/);
    assert.equal(await sohuRow.getByRole('button',{name:'登录搜狐号',exact:true}).count(),0);
    await render({accounts:[...accounts,{...sohu,publishBlocker:'identity-required'}]});
    assert.match(await sohuRow.innerText(),/已登录 · 需实名认证/);
    assert.ok(await sohuRow.locator('input[data-target=sohu]').isDisabled());
    await render({accounts:[...accounts,{...sohu,publishBlocker:'unavailable'}]});
    await sohuRow.getByRole('button',{name:'检查搜狐号发布权限',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)),'access:sohu');
    await render({accounts:[...accounts,{...sohu,canSubmit:true}]});
    assert.ok(await sohuRow.locator('input[data-target=sohu]').isEnabled());
    assert.ok(await sohuRow.getByText('搜狐作者丙',{exact:true}).isVisible());
    await sohuRow.locator('input[data-target=sohu]').check();
    const declaration=page.getByLabel('创作声明',{exact:true});
    assert.ok(await page.locator('#publishArticle').isDisabled());
    assert.match(await page.locator('#target').textContent(),/请选择.*创作声明/);
    await declaration.selectOption('ai');
    await page.locator('#publishArticle').click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)),'publish:sohu|ai');
    await render({article:{name:'新稿.docx',title:'新稿',bytes:50,selection:2}});
    assert.equal(await declaration.inputValue(),'');
    assert.ok(await page.locator('#publishArticle').isDisabled());
    await render({accounts:[...accounts,{...restoredWeibo,state:'signin',connected:true,canSubmit:false}]});
    await page.locator('[data-nav="accounts"]').click();
    assert.ok(await page.getByRole('button',{name:'登录新浪微博',exact:true}).isVisible());
    await render({accounts});

    await page.getByRole('button',{name:'打开CSDN',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'account:csdn');
    await page.locator('[data-nav="publish"]').click();
    await page.locator('[data-nav="accounts"]').click();
    assert.ok(await analytics.isHidden());
    await page.locator('[data-nav="publish"]').click();
    await page.locator('#allTargets').click();
    assert.equal(await page.locator('#targets input:checked').count(),2);
    await page.locator('#publishArticle').click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'publish:zhihu,csdn');
    await page.locator('#allTargets').click();
    assert.equal(await page.locator('#targets input:checked').count(),0);
    const draftButton=page.locator('#saveZhihuDraft');
    assert.ok(await draftButton.isHidden());
    await page.getByRole('checkbox',{name:/知乎/}).focus();
    await page.keyboard.press('Space');
    assert.equal(await page.getByRole('checkbox',{name:/知乎/}).evaluate(e=>e===document.activeElement),true);
    await page.getByRole('checkbox',{name:/知乎/}).check();
    assert.ok(await draftButton.isVisible());
    await draftButton.click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'draft:zhihu');
    await render({drafting:true,busy:true,status:'正在回读知乎草稿'});
    assert.equal(await draftButton.textContent(),'正在存草稿…');
    assert.ok(await draftButton.isDisabled());
    assert.ok(await page.locator('#publishArticle').isDisabled());
    assert.equal(await page.locator('#publishArticle').textContent(),'发布文章');
    assert.ok(await page.getByRole('checkbox',{name:/CSDN/}).isDisabled());
    assert.ok(await page.locator('#replaceFile').isDisabled());
    assert.match(await page.locator('#notice').innerText(),/回读知乎草稿/);
    await render({drafting:false,busy:false,status:''});
    await page.getByRole('checkbox',{name:/CSDN/}).check();
    assert.ok(await draftButton.isHidden(),'draft action is isolated to Zhihu');
    await page.getByRole('checkbox',{name:/知乎/}).uncheck();
    assert.ok(await draftButton.isHidden());
    await page.locator('#publishArticle').click();
    assert.equal(await page.evaluate(() => window.sent.at(-1)), 'publish:csdn');

    const targets = [
      {platform:'zhihu', account:'作者甲', state:'running', step:'正在核对正文'},
      {platform:'csdn', account:'作者乙', state:'queued', step:'等待开始'}
    ];
    await render({busy:true, batch:targets, batchFile:'新稿.docx', status:'无关的旧状态'});
    assert.ok(await batchBox.isVisible());
    assert.equal(await batchBox.locator('.result-row').count(), 2);
    assert.match(await batchBox.innerText(), /正在核对正文/);
    assert.ok(await page.locator('#notice').isHidden());
    assert.equal(await page.locator('#recentRows').count(),0);
    assert.ok(await page.locator('#publishArticle').isDisabled());

    targets[0] = {...targets[0], state:'failed', step:'Word 表格导入不完整，草稿已保留。'};
    targets[1] = {...targets[1], state:'uncertain', step:'尚未取得发布回执。', url};
    await render({batch:targets, busy:false, ready:true});
    assert.match(await batchBox.innerText(), /已公开 0/);
    assert.match(await batchBox.innerText(), /未发出/);
    assert.match(await batchBox.innerText(), /待核实/);
    assert.equal(await batchBox.getByRole('button',{name:'查看文章'}).count(), 0);
    await batchBox.getByRole('button',{name:'核实状态'}).click();
    assert.equal(await page.evaluate(() => window.sent.at(-1)), 'verify-batch:csdn');
    await render({verifying:true, verificationNotice:'正在检查 CSDN 创作中心。'});
    assert.ok(await batchBox.getByRole('button',{name:'核实状态'}).isDisabled());
    assert.ok(await page.locator('#publishArticle').isDisabled());
    await render({verifying:false, verificationNotice:''});
    await render({batch:[{platform:'weibo',state:'blocked',openDraft:true,step:'微博要求安全验证'}]});
    await batchBox.getByRole('button',{name:'打开草稿'}).click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)),'open-batch:weibo');
    await render({batch:[{platform:'qiehao',account:'作者丙',state:'submitted',checking:true,verifyCreator:true}]});
    assert.match(await batchBox.innerText(), /已提交/);
    assert.match(await batchBox.innerText(), /正在核实平台回执/);
    assert.equal(await batchBox.getByRole('button',{name:'核实状态'}).count(),0);
    assert.ok(await page.locator('#publishArticle').isEnabled());
    for (const [state,label] of [['submitted','已提交'],['review','审核中'],['blocked','需处理']]) {
      targets[1] = {...targets[1], state};
      await render({batch:targets});
      assert.match(await batchBox.innerText(), new RegExp(label));
      assert.match(await batchBox.innerText(), /已公开 0/);
      assert.equal(await batchBox.getByRole('button',{name:'查看文章'}).count(), 0);
    }
    targets[1] = {...targets[1], state:'published', step:'已核实公开文章。'};
    await render({batch:targets});
    assert.match(await batchBox.innerText(), /已公开 1/);
    await batchBox.getByRole('button',{name:'查看文章'}).click();
    assert.equal(await page.evaluate(() => window.sent.at(-1)), 'open-batch:csdn');
    await page.setViewportSize({width:650,height:640});
    assert.equal(await page.evaluate(() => {
      const main = document.getElementById('main');
      return main.scrollWidth > main.clientWidth + 1;
    }), false, 'status rows fit small windows');
    const oldLink = {file:'归档.docx',platform:'CSDN',time:'昨天',label:'已提交',detail:'等待审核',link:url};
    const draftUrl='https://zhuanlan.zhihu.com/p/456/edit';
    const saved = {file:'草稿.docx',platform:'知乎',time:'今天',label:'草稿',detail:draftUrl,link:draftUrl};
    const publicEntry = {file:'公开文章.docx',platform:'CSDN',time:'今天',label:'已发布',detail:url,link:url};
    await render({history:[oldLink,...history,saved,publicEntry]});
    await page.locator('[data-nav="history"]').click();
    const table = page.getByRole('table',{name:'发布记录'});
    const filters = page.getByRole('group',{name:'按发布状态筛选'});
    const recordRows = table.locator('.history-row');
    assert.deepEqual(await table.getByRole('columnheader').allTextContents(), ['文章','平台','状态']);
    assert.equal(await recordRows.count(),5);
    assert.ok(await table.getByText('等待审核',{exact:true}).isHidden());
    await filters.getByRole('button',{name:'失败',exact:true}).click();
    assert.equal(await recordRows.count(),1);
    assert.match(await recordRows.innerText(), /旧稿\s+知乎\s+失败/);
    await recordRows.getByRole('button',{name:/^失败/}).click();
    assert.ok(await table.getByText('正文有差异。',{exact:true}).isVisible());
    await filters.getByRole('button',{name:'草稿',exact:true}).click();
    assert.equal(await recordRows.count(),1);
    await recordRows.getByRole('button',{name:'打开草稿：草稿',exact:true}).click();
    assert.equal(await page.evaluate(() => window.sent.at(-1)), 'open:3');
    await recordRows.getByRole('button',{name:/^草稿，查看/}).click();
    await table.getByRole('button',{name:'核实状态',exact:true}).click();
    assert.equal(await page.evaluate(() => window.sent.at(-1)), 'verify-history:3');
    await filters.getByRole('button',{name:'成功',exact:true}).click();
    assert.equal(await recordRows.count(),1);
    await recordRows.getByRole('button',{name:'查看文章：公开文章',exact:true}).click();
    assert.equal(await page.evaluate(() => window.sent.at(-1)), 'open:4');
    await filters.getByRole('button',{name:'待处理',exact:true}).click();
    assert.equal(await recordRows.count(),2,'submitted and unverified records remain pending');
    await page.locator('#filter').fill('CSDN');
    assert.equal(await recordRows.count(),2);
    await recordRows.first().getByRole('button',{name:/^已提交/}).click();
    await table.getByRole('button',{name:'核实状态',exact:true}).click();
    assert.equal(await page.evaluate(() => window.sent.at(-1)), 'verify-history:0');
    await render({verifying:true,verificationNotice:'正在核实。'});
    assert.ok(await table.getByRole('button',{name:'核实状态',exact:true}).isDisabled());
    await render({verifying:false,verificationNotice:''});
    await page.locator('#filter').fill('知乎');
    assert.ok(await table.getByText('没有匹配的发布记录').isVisible());
    await page.setViewportSize({width:430,height:620});
    assert.equal(await page.locator('#main').evaluate(main=>main.scrollWidth>main.clientWidth+1),false);
    await page.locator('[data-nav="publish"]').click();
    assert.match(await batchBox.innerText(), /已公开 1/);
    await render({batch:[{...targets[1],state:'draft',step:'后台已核实草稿。'}],busy:true});
    assert.equal(await page.locator('#targets input:checked').count(),1);
    assert.ok(await page.getByRole('checkbox',{name:/CSDN/}).isChecked());
    assert.match(await batchBox.innerText(), /草稿/);
    await render({busy:false,status:'文件不可用，请重新选择。'});
    assert.match(await page.locator('#notice').innerText(),/文件不可用/);
    await render({batch:[],batchFile:'',status:''});
    assert.ok(await batchBox.isHidden());
    assert.equal(await page.locator('#recentRows').count(),0);
    await render({history:[
      {file:'草稿.docx',platform:'知乎',time:'今天',label:'草稿',detail:draftUrl,link:draftUrl},
      {file:'旧草稿.docx',platform:'CSDN',time:'昨天',label:'草稿',detail:'创作中心已确认。'}
    ]});
    await page.locator('[data-nav="history"]').click();
    await page.locator('#filter').fill('');
    await filters.getByRole('button',{name:'草稿',exact:true}).click();
    assert.equal(await recordRows.count(),2);
    await recordRows.getByRole('button',{name:'打开草稿：草稿',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.sent.at(-1)), 'open:0');
    assert.deepEqual(errors, []);
    console.log('PASS: target selection, isolated Zhihu draft saving and opening, live batch, evidence links, history and small viewport');
  } finally {
    await browser.close();
  }
})().catch(error => {console.error(error);process.exitCode=1;});
