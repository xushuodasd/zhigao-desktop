using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    bool simulatingCreatorPublish;
    int simulatedCreatorSendCount;
    int simulatedCreatorLookupCount;
    string simulatedCreatorLookupState="0";
    bool checkedWhileWorkbenchAvailable;
    bool savedCreatorJournalBeforeSend;

    partial void ConfigureCreatorPublishView(CreatorSession session) {
        if(simulatingCreatorPublish)StubCreatorNavigation(session.Web);
    }

    partial void ConfigureCreatorVerificationView(WebView2 view) {
        if(simulatingCreatorPublish)StubCreatorNavigation(view);
    }

    const string CreatorEditorFixture=@"<!doctype html><meta charset=utf-8>
      <div class='input-box'><textarea placeholder='请输入文章标题'></textarea></div>
      <div class='ProseMirror news-editor-pc' contenteditable='true' style='min-height:50px'></div>
      <button id=save>保存草稿</button><button id=publish>发布文章</button>
      <script>
      let title=document.querySelector('textarea');
      const body=document.querySelector('.ProseMirror');
      if(location.hostname==='card.weibo.com') {
        if(!location.hash || location.hash==='#/draft') {
          title.hidden=!location.hash;
          body.hidden=!location.hash;
          body.innerHTML='<p><span class=""Tiptap-invisible-character ProseMirror-widget"" contenteditable=""false"">¶</span><img class=""ProseMirror-separator""><br class=""ProseMirror-trailingBreak""></p>';
        }
        const begin=document.createElement('button');
        begin.textContent='写文章';
        begin.onclick=()=>{
          begin.remove();
          location.hash='/draft/100001';
          title.value='';
          title.hidden=false;
          body.hidden=false;
          setTimeout(()=>{body.innerHTML='<p class=""is-editor-empty""><br></p>';},500);
        };
        document.body.append(begin);
      }
      if(location.hostname==='baijiahao.baidu.com'){
        const richTitle=document.createElement('div');
        richTitle.contentEditable='true';
        richTitle.style.minHeight='24px';
        title.replaceWith(richTitle);
        title=richTitle;
      }
      if(location.hostname==='om.qq.com'){
        document.querySelector('#save').textContent='存草稿';
        const wrapper=document.createElement('div');
        wrapper.id='omEditorTitle';
        const richTitle=document.createElement('div');
        richTitle.className='ProseMirror';
        richTitle.contentEditable='true';
        richTitle.style.minHeight='24px';
        const placeholder=document.createElement('span');
        placeholder.dataset.placeholder='请输入标题（5-64个字）';
        richTitle.append(placeholder);
        wrapper.append(richTitle);
        title.replaceWith(wrapper);
        title=richTitle;
        title.addEventListener('input',()=>setTimeout(()=>{
          window.fixtureQiehaoTitle=title.textContent;
        },900));
        const declaration=document.createElement('div');
        declaration.className='omui-form__group';
        declaration.innerHTML='<label class=omui-form__label>自主声明</label><div class=omui-contentbutton__footer><button>添加内容自主声明</button></div>';
        document.body.append(declaration);
      }
      if((location.search && location.hash!=='#/draft') || location.hash.startsWith('#/draft/') ||
          location.hash.startsWith('#/article-publish/')){
        if(title.matches('textarea'))title.value=localStorage.getItem('fixtureTitle') || '';
        else title.textContent=localStorage.getItem('fixtureTitle') || '';
        body.innerHTML=localStorage.getItem('fixtureBody') || '';
      } else if(location.hostname==='mp.toutiao.com') {
        body.innerHTML='<p><span class=syl-placeholder contenteditable=false>请输入正文</span><br></p>';
        body.querySelector('span').classList.add('ProseMirror-widget');
      }
      document.querySelector('#publish').onclick=()=>{ window.publishClicks=(window.publishClicks||0)+1; };
      if(location.hostname==='om.qq.com' && new URLSearchParams(location.search).has('articleId')) {
        const toolbar=document.createElement('div');
        toolbar.className='tool_right-cls_fixture';
        const publish=document.querySelector('#publish');
        publish.className='omui-button omui-button--primary';
        publish.textContent='发布';
        const save=document.querySelector('#save');
        save.className='omui-button';
        const schedule=document.createElement('button');
        schedule.className='omui-button';
        schedule.textContent='定时发布';
        toolbar.append(save,schedule,publish);
        document.body.append(toolbar);
        document.querySelector('.omui-contentbutton__footer').outerHTML='<div class=omui-contentbutton__title>该文章由AI生成</div>';
        publish.onclick=()=>fetch('/marticlepublish/omPublish',{method:'POST'}).then(response=>response.json()).catch(()=>{});
      }
      if(location.hostname==='mp.163.com'){
        document.querySelector('#save').hidden=true;
        const status=document.createElement('span');
        status.className='post-footer__container-left-save';
        document.body.append(status);
        body.addEventListener('input',()=>{
          status.textContent='自动保存中';
          setTimeout(()=>{
            localStorage.setItem('fixtureTitle',title.value);
            localStorage.setItem('fixtureBody',body.innerHTML);
            status.textContent='自动保存成功 15:02';
          },400);
        });
      }
      let saveControl=document.querySelector('#save');
      if(location.hostname==='mp.sohu.com'){
        const actions=document.createElement('ul');
        actions.className='button-list';
        actions.innerHTML='<li id=save class=""publish-report-btn normal disable-button"">存草稿</li><li class=rest-number>未实名暂无法发布文章</li>';
        saveControl.replaceWith(actions);
        saveControl=actions.querySelector('#save');
      }
      saveControl.onclick=()=>{
        if(location.hostname==='om.qq.com' && window.fixtureQiehaoTitle!==title.textContent)return;
        localStorage.setItem('fixtureTitle',title.value ?? title.textContent);
        localStorage.setItem('fixtureBody',body.innerHTML);
        if(location.hostname==='baijiahao.baidu.com'){
          fetch('/pcui/article/save?callback=bjhdraft',{method:'POST'}).then(async response=>{
            window.fixtureReceipt=[response.status,(await response.text()).length];
          }).catch(err=>{window.fixtureReceipt=[err.name];});
        } else if(location.hostname==='card.weibo.com') {
          fetch('/article/v5/aj/editor/draft/save?id=100001',{method:'POST'}).then(async response=>{
            window.fixtureReceipt=[response.status,(await response.text()).length];
          }).catch(err=>{window.fixtureReceipt=[err.name];});
        } else if(location.hostname==='om.qq.com') {
          fetch('/marticlepublish/omSave?relogin=1',{method:'POST'}).then(async response=>{
            window.fixtureReceipt=[response.status,(await response.text()).length];
          }).catch(err=>{window.fixtureReceipt=[err.name];});
        } else if(location.hostname==='mp.sohu.com') {
          fetch('/mpbp/bp/news/v4/news/draft/v2',{method:'POST'}).then(async response=>{
            window.fixtureReceipt=[response.status,(await response.text()).length];
          }).catch(err=>{window.fixtureReceipt=[err.name];});
        } else {
          history.replaceState(null,'','?draftId=100001'+location.hash);
        }
        window.saveClicks=(window.saveClicks||0)+1;
      };
      body.addEventListener('paste',event=>{
        const image=event.clipboardData.files[0];
        if(!image)return;
        event.preventDefault();
        const el=document.createElement('img');
        el.src=location.origin+'/test-creator-image.png';
        const range=getSelection().getRangeAt(0);
        range.deleteContents();
        if(location.hostname==='mp.163.com') {
          const figure=document.createElement('figure');
          const caption=document.createElement('figcaption');
          caption.className='rich-editor-image-remark';
          caption.textContent='点击输入图片描述（最多40字）';
          figure.append(el,caption);
          range.insertNode(figure);
          body.dispatchEvent(new InputEvent('input',{bubbles:true}));
        } else range.insertNode(el);
      });
      </script>";
    const string WangyiListFixture=@"<!doctype html><meta charset=utf-8>
      <section class='src-main-page-ContentManage-index-module-content-list-container src-main-page-ContentManage-index-module-draft'>
        <div class='src-main-page-ContentManage-Card-index-module-card'>
          <span class='src-main-page-ContentManage-Card-index-module-title'><a href='#/layout/article-preview?articleId=100001'></a></span>
          <div class='src-main-page-ContentManage-Card-Operation-index-module-oper'>
            <a id=edit href='#/article-publish/100001?option=editDraft'>修改</a>
          </div>
        </div>
      </section>
      <script>document.querySelector('.src-main-page-ContentManage-Card-index-module-title a').textContent=localStorage.getItem('fixtureTitle');</script>";
    const string CreatorTestPng="iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lqUAAAAASUVORK5CYII=";

    void StubCreatorNavigation(WebView2 view) {
        var core=view.CoreWebView2;
        core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
        core.WebResourceRequested+=(sender,e)=>{
            var uri=new Uri(e.Request.Uri);
            if(simulatedCreatorLookupState=="signin" && uri.AbsoluteUri==QiehaoManagementUrl) {
                e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(),302,"Found",
                    "Location: https://om.qq.com/userAuth/login\r\nCache-Control: no-store");
                return;
            }
            bool image=uri.AbsolutePath=="/test-creator-image.png";
            bool baijiahaoReceipt=uri.Host=="baijiahao.baidu.com" && uri.AbsolutePath=="/pcui/article/save";
            bool weiboReceipt=uri.Host=="card.weibo.com" && uri.AbsolutePath=="/article/v5/aj/editor/draft/save";
            bool qiehaoReceipt=uri.Host=="om.qq.com" && uri.AbsolutePath=="/marticlepublish/omSave";
            bool qiehaoPublish=uri.Host=="om.qq.com" && uri.AbsolutePath=="/marticlepublish/omPublish";
            bool publicationLookup=uri.AbsoluteUri=="https://om.qq.com/marticle/article/list?articleIds=qq_100001";
            bool sohuReceipt=uri.Host=="mp.sohu.com" && uri.AbsolutePath=="/mpbp/bp/news/v4/news/draft/v2";
            bool receipt=baijiahaoReceipt || weiboReceipt || qiehaoReceipt || qiehaoPublish || sohuReceipt;
            if(qiehaoPublish && e.Request.Method=="POST") {
                simulatedCreatorSendCount++;
                var journalFolder=Path.Combine(dataFolder,"CreatorAttempts","qiehao");
                savedCreatorJournalBeforeSend=Directory.Exists(journalFolder) && Directory.GetFiles(journalFolder,"*.json")
                    .Any(file=>File.ReadAllText(file).Contains("\"state\":\"uncertain\""));
            }
            if(publicationLookup && e.Request.Method=="GET") {
                simulatedCreatorLookupCount++;
                checkedWhileWorkbenchAvailable|=!busy && !verifying && batch.Any(item=>item.platform=="qiehao" && item.checking);
            }
            string payload=baijiahaoReceipt?"bjhdraft({\"errno\":0,\"errmsg\":\"success\",\"ret\":{\"article_id\":\"100001\"}});":
                weiboReceipt?"{\"code\":100000}":qiehaoReceipt?"{\"response\":{\"code\":0},\"data\":{\"articleId\":\"qq_100001\"}}":
                qiehaoPublish?"{\"response\":{\"code\":0},\"data\":{\"articleId\":\"qq_100001\"}}":
                publicationLookup?json.Serialize(new {response=new {code=0},data=new {articles=new[]{
                    new {article_id="qq_100001", status=simulatedCreatorLookupState, url="https://page.om.qq.com/page/official001"} }}}):
                sohuReceipt?"{\"success\":true,\"data\":100001}":
                uri.Host=="mp.sohu.com" && uri.AbsolutePath=="/mpfe/v4/contentManagement/first/page"?
                    "<!doctype html><meta charset=utf-8><span>发布内容</span><span>个人中心</span><span>草稿</span>":
                uri.Host=="mp.163.com" && uri.AbsolutePath=="/test-draft-list"?WangyiListFixture:CreatorEditorFixture;
            byte[] response=image?Convert.FromBase64String(CreatorTestPng):Encoding.UTF8.GetBytes(payload);
            e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(response),200,"OK",
                "Content-Type: "+(image?"image/png":publicationLookup?"application/json":receipt?"application/javascript; charset=utf-8":"text/html; charset=utf-8")+"\r\nCache-Control: no-store");
        };
    }

    static async Task OpenCreatorFixture(WebView2 view,string address) {
        var navigation=new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> onLoad=(s,e)=>navigation.TrySetResult(e.IsSuccess);
        view.CoreWebView2.NavigationCompleted+=onLoad;
        try {
            if(view.Source!=null && view.Source.AbsoluteUri==address)view.CoreWebView2.Reload();
            else view.CoreWebView2.Navigate(address);
            if(await Task.WhenAny(navigation.Task,Task.Delay(6000))!=navigation.Task || !await navigation.Task)
                throw new Exception("离线测试页未打开，地址 "+address);
        } finally { view.CoreWebView2.NavigationCompleted-=onLoad; }
    }

    async Task CheckSohuCreatorMetrics(CreatorSite site) {
        using(var view=new WebView2 {Visible=false, Width=1100, Height=730}) {
            platform.Controls.Add(view);
            await InitCreatorView(view,site,allowPopups:false);
            var core=view.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            bool unexpectedWrite=false;
            core.WebResourceRequested+=(sender,e)=>{
                var route=new Uri(e.Request.Uri);
                if(e.Request.Method!="GET")unexpectedWrite=true;
                string payload=null;
                string contentType="application/json; charset=utf-8";
                if(route.AbsoluteUri==SohuMetricsHome) {
                    contentType="text/html; charset=utf-8";
                    payload=@"<!doctype html><meta charset=utf-8><div class='menu-item'><span class='title child-level'>内容分析</span></div>
                        <script>
                            const load=url=>fetch(url).then(response=>response.text());
                            document.querySelector('.title').onclick=()=>{
                                history.pushState({},'', '/mpfe/v4/data/analysis');
                                load('/mpbp/bp/news/v4/users/stat/overall/detail?type=0&timeRange=1');
                            };
                            load('/mpbp/bp/news/v4/users/newsInfo');
                            load('/mpbp/bp/news/v4/users/newsInfo?adFlag=1');
                            load('/mpbp/bp/news/v4/users/stat/overall/detail?type=2&timeRange=1');
                        </script>";
                } else if(route.Host=="mp.sohu.com" && route.AbsolutePath=="/mpbp/bp/news/v4/users/newsInfo")
                    payload=route.Query.Contains("adFlag=")?
                        "{\"success\":true,\"code\":2000000,\"data\":{\"text_pass_cnt\":42}}":
                        "{\"success\":true,\"code\":2000000,\"data\":{\"text_pass_cnt\":0,\"news_pass_cnt\":83}}";
                else if(route.Host=="mp.sohu.com" && route.AbsolutePath=="/mpbp/bp/news/v4/users/stat/overall/detail")
                    payload=route.Query.Contains("type=0")?
                        "{\"success\":true,\"code\":2000000,\"data\":[{\"type\":\"pv\",\"all\":11},{\"type\":\"pvValid\",\"all\":0},{\"type\":\"interaction\",\"all\":47}]}":
                        "{\"success\":true,\"code\":2000000,\"data\":[{\"type\":\"pv\",\"all\":912},{\"type\":\"pvValid\",\"all\":643}]}";
                e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(payload??"")),
                    payload==null?404:200,payload==null?"Not Found":"OK","Content-Type: "+contentType+"\r\nCache-Control: no-store");
            };
            var metrics=await ReadSohuCreatorMetrics(view);
            if(metrics==null || metrics.articleCount!="0" || metrics.reads!="11" || metrics.plays!="0" ||
                metrics.scope!=SohuMetricsScope || unexpectedWrite)
                throw new Exception("搜狐号账号数据未正确保留零值、文章分类或全部内容累计口径。");
            var rejected=new CreatorMetrics();
            var address=new Uri("https://mp.sohu.com/mpbp/bp/news/v4/users/newsInfo");
            ApplySohuMetricsReply(rejected,address,"{\"success\":false,\"code\":2000000,\"data\":{\"text_pass_cnt\":0}}");
            ApplySohuMetricsReply(rejected,address,"{\"success\":true,\"code\":2000000,\"data\":{\"news_pass_cnt\":0}}");
            if(HasCreatorMetrics(rejected))throw new Exception("搜狐号账号数据误把失败或缺失值认作真实零值。");
            Console.WriteLine("PASS 搜狐号已发布文章数、累计阅读与累计访问（播放）数");
        }
    }

    async Task CheckToutiaoEmptyArticleCount(CreatorSite site) {
        using(var view=new WebView2 {Visible=true, Width=1100, Height=730}) {
            platform.Controls.Add(view);
            await InitCreatorView(view,site,allowPopups:false);
            var core=view.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            var filter=json.Serialize(new {category="mp_article",page_index="1",status="0",source="0"});
            const string feed="/api/feed/mp_provider/v1/?provider_type=mp_provider&category=mp_article&offset=0&keyword=&client_extra_params=";
            var reply=json.Serialize(new {errno=20100,data=new object[0],api_base_info=new {app_extra_params="{\"total_count\":0}"}});
            if(ReadToutiaoMetricResponse(reply,null)!="0")throw new Exception("头条号空列表的模拟回执不符合解析格式。");
            core.WebResourceRequested+=(sender,e)=>{
                var route=new Uri(e.Request.Uri);
                string payload="",contentType="text/plain";
                int code=404;
                if(route.AbsolutePath==ToutiaoArticleManagement) {
                    contentType="text/html; charset=utf-8";
                    payload="<!doctype html><meta charset=utf-8><div class='byte-tabs-header-title active'>文章</div><script>fetch("+
                        json.Serialize(feed+Uri.EscapeDataString(filter))+").then(response=>response.text())</script>";
                    code=200;
                } else if(IsToutiaoMetricResponse(route,null,"0")) {
                    contentType="application/json; charset=utf-8";
                    payload=reply;
                    code=200;
                }
                e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(payload)),code,
                    code==200?"OK":"Not Found","Content-Type: "+contentType+"\r\nCache-Control: no-store");
            };
            var value=await ReadToutiaoArticleCount(view);
            if(value.articleCount!="0")throw new Exception("空文章列表没有已发布筛选时，应按全量回执确认 0 篇。");
            Console.WriteLine("PASS 头条号空文章列表无已发布筛选仍能核实零篇");
        }
    }

    void CheckDailyAccountAnalytics() {
        var date=new DateTime(2026,11,20);
        var midnight=new DateTimeOffset(date,TimeSpan.FromHours(8));
        if(AnalyticsRefreshDue(midnight,midnight.AddMinutes(59)) || !AnalyticsRefreshDue(midnight,midnight.AddHours(1)) ||
            !AnalyticsRefreshDue(midnight.AddMinutes(-1),midnight))
            throw new Exception("账号统计应按小时刷新，并在跨天后补做一次采集。");
        if(AnalyticsTrafficDay("qiehao",date,null)!=date.AddDays(-1) || AnalyticsTrafficDay("weibo",date,null)!=date.AddDays(-1) ||
            AnalyticsTrafficDay("baijiahao",date,null)!=date || AnalyticsTrafficDay("zhihu",date,"2026-11-18 23:30:00")!=date.AddDays(-2))
            throw new Exception("日报归档日期与平台统计周期不一致。");
        var directory=Path.Combine(AnalyticsDirectory,"qiehao");
        Directory.CreateDirectory(directory);
        SaveAnalyticsDay(directory,date,"2026-11-20 11:00","2",null,null);
        SaveAnalyticsDay(directory,date,"2026-11-21 15:00",null,"0","38");
        SaveAnalyticsDay(directory,date,"2026-11-21 16:00",null,"示例",null);
        SaveAnalyticsDay(directory,date,"2026-11-21 13:00",null,"999","999");
        SaveAnalyticsDay(directory,date.AddDays(1),"2026-11-21 16:00","3",null,null);
        LoadAccountAnalyticsHistory();
        var rows=analyticsDays["qiehao"];
        if(rows.Length!=2 || rows[0].date!="2026-11-21" || rows[0].articleCount!="3" || rows[0].reads!=null ||
            rows[1].articleCount!="2" || rows[1].reads!="0" || rows[1].plays!="38" || analyticsCollectedAt["qiehao"]!="2026-11-21 16:00")
            throw new Exception("日报恢复应合并同日读数并保留真实零值、统计缺口和最近采集时间。");
        Console.WriteLine("PASS 账号统计每小时调度、跨天归档、同日合并和重启读回");
    }

    async Task CheckCreatorEditors(string tableFile,string imageFile) {
        CheckDailyAccountAnalytics();
        var table="4×4";
        var baijiahao=CreatorSites.First(site=>site.Id=="baijiahao");
        if(baijiahao.Home!="https://baijiahao.baidu.com/builder/rc/home" ||
            baijiahao.SecureLegacyNavigation("http://baijiahao.baidu.com/pcui/register/index")!=
                "https://baijiahao.baidu.com/builder/theme/bjh/login" ||
            baijiahao.SecureLegacyNavigation("http://baijiahao.baidu.com/other")!=null)
            throw new Exception("百家号的安全登录跳转失效");
        if(ParseBaijiahaoDraftReceipt("bjhdraft({\"errno\":0,\"errmsg\":\"success\",\"ret\":{\"article_id\":\"100001\"}});")==null ||
            ParseBaijiahaoDraftReceipt("bjhdraft({\"errno\":1,\"errmsg\":\"success\",\"ret\":{\"article_id\":\"100001\"}})")!=null ||
            ParseBaijiahaoDraftReceipt("bjhdraft({\"errmsg\":\"success\",\"ret\":{\"article_id\":\"https://example.com\"}})")!=null ||
            ParseBaijiahaoDraftReceipt("bjhdraft(")!=null)
            throw new Exception("误将不可信回执解析为草稿");
        foreach(var site in CreatorSites) {
            string editor=site.Editor ?? new Uri(new Uri(site.Home),"/article/edit").AbsoluteUri;
            if(!site.IsEditor(new Uri(editor)) || site.Allows("https://example.com/") ||
                site.Allows("http://"+new Uri(site.Home).Host+"/") || site.Allows("https://"+new Uri(site.Home).Host+".example.net/") ||
                GetCreatorDraftAddress(site,new Uri(editor))!=null)
                throw new Exception(site.Label+" 的创作地址校验不完整");
            if(site.Id=="weibo") {
                var receipt=new Uri("https://card.weibo.com/article/v5/aj/editor/draft/save?id=100001");
                if(!IsCreatorDraftReceiptRequest(site,receipt) ||
                    ParseWeiboDraftReceipt(receipt,"{\"code\":\"100000\"}")==null ||
                    ParseWeiboDraftReceipt(receipt,"{\"code\":100001} ")!=null)
                    throw new Exception("微博草稿回执格式核验失败");
                var reply=ReadWeiboPublicationList("{\"ok\":1,\"data\":{\"count\":2,\"list\":[{\"id\":\"100001\",\"status\":0,\"title\":\"原稿\"},{\"id\":\"100002\",\"status\":0,\"title\":\"其他稿\"}]}}",false);
                if(reply.count!=2 || SelectWeiboDraft(site,"不存在",reply.list)!=null ||
                    CreatorPublicationId(site,SelectWeiboDraft(site,"原稿",reply.list).AbsoluteUri)!="100001")
                    throw new Exception("微博应按官网草稿总数和精确标题选稿。");
                bool duplicateBlocked=false;
                try { SelectWeiboDraft(site,"原稿",new[]{reply.list[0],reply.list[0]}); }
                catch(InvalidOperationException) { duplicateBlocked=true; }
                if(!duplicateBlocked)throw new Exception("微博同标题草稿冲突时不应选稿。");
            }
            if(site.Id=="qiehao") {
                var receipt=new Uri("https://om.qq.com/marticlepublish/omSave?relogin=1");
                var result=ParseQiehaoDraftReceipt("{\"response\":{\"code\":\"0\"},\"data\":{\"articleId\":\"qq_100001\"}}");
                if(!IsCreatorDraftReceiptRequest(site,receipt) ||
                    IsCreatorDraftReceiptRequest(site,new Uri("https://om.qq.com/marticlepublish/publish")) ||
                    IsCreatorDraftReceiptRequest(site,new Uri("https://om.qq.com.example.net/marticlepublish/omSave")) ||
                    result==null || result.Query!="?articleId=qq_100001" ||
                    ParseQiehaoDraftReceipt("{\"response\":{\"code\":1},\"data\":{\"articleId\":\"qq_100001\"}}")!=null ||
                    ParseQiehaoDraftReceipt("{\"response\":{},\"data\":{\"articleId\":\"qq_100001\"}}")!=null ||
                    ParseQiehaoDraftReceipt("{\"response\":{\"code\":0},\"data\":{\"articleId\":\"abc123&draft=false\"}}")!=null)
                    throw new Exception("企鹅号草稿回执格式核验失败");
            }
            if(site.Id=="sohu" && (ParseSohuDraftReceipt("{\"success\":true,\"data\":\"100001\"}")==null ||
                ParseSohuDraftReceipt("{\"success\":false,\"data\":100001}")!=null ||
                ParseSohuDraftReceipt("{\"success\":true,\"data\":\"https://example.com\"}")!=null)) {
                throw new Exception("搜狐号草稿回执格式核验失败");
            }
            if(site.Id=="sohu")await CheckSohuCreatorMetrics(site);
            if(site.Id=="toutiao")await CheckToutiaoEmptyArticleCount(site);
            var fixtureSession=new CreatorSession(site);
            using(var view=fixtureSession.Web) {
                view.Visible=true;
                platform.Controls.Add(view);
                platform.BringToFront();
                view.BringToFront();
                await InitCreatorView(view,site);
                if(view.CoreWebView2.Profile.ProfileName!=site.Profile || view.CoreWebView2.Settings.IsWebMessageEnabled)
                    throw new Exception(site.Label+" 未隔离 WebView2 会话");
                StubCreatorNavigation(view);
                if(site.Id=="sohu") {
                    await OpenCreatorFixture(view,"https://mp.sohu.com/mpfe/v4/contentManagement/first/page");
                    await CheckCreator(fixtureSession);
                    if(fixtureSession.State!="connected")throw new Exception("搜狐内容管理页没有识别为已登录");
                    await view.ExecuteScriptAsync("document.querySelector('span:last-child').remove()");
                    if(await view.ExecuteScriptAsync(SohuHomeStatus)!="false")throw new Exception("搜狐内容管理控件不完整时误认已登录");
                    int attempts=0;
                    var core=view.CoreWebView2;
                    core.WebResourceRequested+=(sender,e)=>{
                        if(e.ResourceContext==CoreWebView2WebResourceContext.Document && e.Request.Uri==editor && ++attempts==1)
                            e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(),302,"Found",
                                "Location: https://mp.sohu.com/mpfe/v4/contentManagement/first/page");
                    };
                    await NavigateCreatorPublishEditor(fixtureSession,new Uri(editor),true);
                    if(attempts!=2 || fixtureSession.State!="editor")
                        throw new Exception("搜狐号从编辑器回到内容管理后，应重新进入一次官方编辑器。");
                }
                await OpenCreatorFixture(view,editor);
                if(site.Id=="qiehao") {
                    await view.ExecuteScriptAsync("localStorage.setItem('OM_ARTICLE_CACHE_123','旧稿')");
                    await IsolateQiehaoNewDraftCache(view);
                    await OpenCreatorFixture(view,editor);
                    if(await view.ExecuteScriptAsync(@"(() => {
                        const key='OM_ARTICLE_CACHE_123';
                        if(localStorage.getItem(key)!==null || localStorage[key]!=='旧稿')return false;
                        localStorage.setItem(key,'当前新稿');
                        return localStorage.getItem(key)==='当前新稿' && localStorage[key]==='旧稿';
                    })()")!="true")throw new Exception("企鹅号新稿应该使用独立编辑缓存并保留原稿。");
                }
                if(site.Id=="wangyi" && await CreatorAction(site,view,"wangyi-autosave-ready")!="false")
                    throw new Exception("网易号将空白编辑器误认成已自动保存");
                if(site.Id=="weibo") {
                    await CheckCreator(fixtureSession);
                    if(fixtureSession.State!="connected" || GetCreatorDraftAddress(site,view.Source)!=null)
                        throw new Exception("微博登录探测误判，或未点击时新建了草稿");
                    await OpenCreatorEditor(fixtureSession);
                    await Wait(async()=>{
                        await CheckCreator(fixtureSession);
                        return fixtureSession.State=="editor" && GetCreatorDraftAddress(site,view.Source)!=null;
                    },5,"微博写文章未打开编辑器");
                    await OpenCreatorFixture(view,editor+"?emptyEditor=1#/draft");
                    if(GetCreatorDraftAddress(site,view.Source)!=null)throw new Exception("微博测试夹具应为无编号编辑器");
                    await PrepareWeiboDraft(fixtureSession);
                    if(await CreatorAction(site,view,"weibo-draft-ready")!="true")
                        throw new Exception("微博草稿编号出现后未等待正文载入");
                    await view.ExecuteScriptAsync("localStorage.setItem('fixtureTitle','其他稿');localStorage.setItem('fixtureBody','<p>其他稿正文</p>')");
                    await OpenCreatorFixture(view,editor+"#/draft/100002");
                    if((await ReadCreatorSnapshot(site,view)).title!="其他稿")
                        throw new Exception("微博测试未模拟启动时恢复已占用稿件。");
                    await PrepareWeiboDraft(fixtureSession);
                    var blank=await ReadCreatorSnapshot(site,view);
                    if(view.Source.Fragment!="#/draft/100001" || blank.title!="" || blank.text!="" ||
                        await view.ExecuteScriptAsync("localStorage.getItem('fixtureTitle')")!="\"其他稿\"" ||
                        await view.ExecuteScriptAsync("[window.saveClicks || 0, window.publishClicks || 0]")!="[0,0]")
                        throw new Exception("微博新稿必须为不同编号的空白草稿，并保留已有稿件。");
                    await view.ExecuteScriptAsync("localStorage.removeItem('fixtureTitle');localStorage.removeItem('fixtureBody')");
                }
                string doc=site.Id=="baijiahao"?imageFile:tableFile;
                if(site.Id=="sohu") {
                    verifiedCreatorProfiles.Add(site.Id);
                    await RefreshSohuDraftAccess(site,view);
                    var account=CreatorAccounts().Single(a=>a.id==site.Id);
                    if(account.state!="connected" || account.canSubmit!=false || account.publishBlocker!="identity-required" ||
                        await view.ExecuteScriptAsync("[window.saveClicks || 0, window.publishClicks || 0]")!="[0,0]")
                        throw new Exception("搜狐号已登录但未实名时，应单独标记发布权限，探测不得写入。");
                    queuedArticlePath=doc;
                    await LoadCreatorWord(fixtureSession);
                    var untouched=await ReadCreatorSnapshot(site,view);
                    if(fixtureSession.Notice.IndexOf(SohuDraftVerificationError,StringComparison.Ordinal)<0 ||
                        untouched.title!="" || untouched.text!="" || untouched.tables.Length!=0)
                        throw new Exception("搜狐号未实名时应在写入文章前提示");
                }
                using(var stream=File.OpenRead(doc)) {
                    var word=new CreatorWord(doc,stream);
                    if(doc==tableFile && (word.Tables.Count!=1 || word.Tables[0].cells.Length!=4 || word.Tables[0].cells.Any(row=>row.Length!=4)))
                        throw new Exception("测试稿预期为一个 4×4 表格");
                    if(doc==imageFile && word.Images.Count!=4)throw new Exception("测试稿预期有四张图片");
                    if(site.Id=="wangyi") {
                        var tableShape=word.Tables.Single();
                        var textWithoutTable=word.Text.Remove(tableShape.offset,tableShape.cells.SelectMany(row=>row).Sum(cell=>cell.Length));
                        stream.Position=0;
                        word=new CreatorWord(doc,stream,true);
                        if(word.Tables.Count!=0 || word.Images.Count!=1 || word.Images[0].Offset!=tableShape.offset ||
                            word.Text!=textWithoutTable || word.Images[0].Data==null)
                            throw new Exception("网易号表格图片没有保持原位置或前后正文");
                        using(var imageBytes=new MemoryStream(word.Images[0].Data))
                        using(var tableImage=System.Drawing.Image.FromStream(imageBytes)) {
                            if(tableImage.Width<1400 || tableImage.Height<270)
                                throw new Exception("网易号表格图片分辨率不足");
                        }
                    }
                    if(site.Id=="qiehao") {
                        bool required=false;
                        try { await RequireQiehaoDraftDeclaration(fixtureSession); }
                        catch(InvalidOperationException ex) { required=ex.Message.Contains("选择并确认"); }
                        if(!required || (await ReadCreatorSnapshot(site,view)).title!="")
                            throw new Exception("企鹅号未声明时应在写入前提示");
                        await view.ExecuteScriptAsync(@"(() => {
                            const footer=document.querySelector('.omui-contentbutton__footer');
                            footer.querySelector('button').onclick=()=>{
                                const popup=document.createElement('div');
                                popup.className='omui-dialog-wrapper open';
                                popup.innerHTML='<div class=omui-dialog><div class=omui-dialog-header>发布内容自主声明</div>'+
                                    '<label class=omui-radio><input type=radio name=declare checked>无需标注</label>'+
                                    '<label class=omui-radio><input type=radio name=declare>该文章由AI生成</label><button>确认</button></div>';
                                popup.querySelector('button').onclick=()=>{
                                    const selected=popup.querySelector('.omui-radio input:checked').parentElement.textContent;
                                    const title=document.createElement('div');
                                    title.className='omui-contentbutton__title';
                                    title.textContent='作者声明：'+selected;
                                    footer.replaceWith(title);
                                    popup.remove();
                                };
                                document.body.append(popup);
                            };
                        })()");
                        await SetCreatorDeclaration(fixtureSession,"ai");
                        if(await view.ExecuteScriptAsync("document.querySelector('.omui-contentbutton__title').textContent")!="\"作者声明：该文章由AI生成\"")
                            throw new Exception("企鹅号没有确认用户选定的声明。");
                        await RequireQiehaoDraftDeclaration(fixtureSession);
                    }
                    if(site.Id=="sohu") {
                        await view.ExecuteScriptAsync(@"(() => {
                            const form=document.createElement('div');
                            form.id='info-source-signature';
                            form.innerHTML='<div class=option-item><div class=info-radio>'+
                                '<label class=info-radio-label><input type=radio name=declare checked>无需声明</label>'+
                                '<label class=info-radio-label><input type=radio name=declare>含有AI生成内容</label></div></div>';
                            document.body.append(form);
                        })()");
                        await SetCreatorDeclaration(fixtureSession,"ai");
                        if(await view.ExecuteScriptAsync("document.querySelector('#info-source-signature input:checked').parentElement.textContent")!="\"含有AI生成内容\"")
                            throw new Exception("搜狐号没有确认用户选定的声明。");
                    }
                    bool titlePrepared=site.Id=="baijiahao" || site.Id=="qiehao";
                    if(titlePrepared) {
                        // Real publish views are hidden behind the workbench.
                        view.Visible=false;
                        try {await EnterCreatorTitle(fixtureSession,word.Title);}
                        finally {view.Visible=true;}
                    }
                    var data=new {title=word.Title,html=word.Html,titlePrepared};
                    if(await CreatorAction(site,view,"import",data)!="\"started\"" ||
                        await CreatorAction(site,view,"import",data)!="\"occupied\"")
                        throw new Exception(site.Label+" 的导入或防覆盖失效");
                    if(site.Id=="weibo")await view.ExecuteScriptAsync(@"(() => {
                        const table=document.querySelector('.ProseMirror table');
                        const figure=document.createElement('figure');
                        figure.className='wb-node-table';
                        const wrapper=document.createElement('div');
                        wrapper.className='wb-table-content__wrapper';
                        const controls=document.createElement('div');
                        controls.className='wb-table-content__wrapper__control';
                        controls.textContent='前插行后插行';
                        table.replaceWith(figure);
                        figure.append(wrapper);
                        wrapper.append(table,controls);
                    })()");
                    stream.Position=0;
                    if(word.Images.Count>0) {
                        using(var archive=new ZipArchive(stream,ZipArchiveMode.Read,true)) {
                            for(int i=0;i<word.Images.Count;i++) {
                                var image=word.Images[i];
                                if(image.Data!=null) {
                                    var imageData=new {token=image.Token,name=image.Name,mime=image.Mime,base64=Convert.ToBase64String(image.Data)};
                                    if(await CreatorAction(site,view,"image",imageData)!="true")throw new Exception("表格图片位置未定位");
                                    image.SavedUrl=site.Home+"test-creator-image.png";
                                    continue;
                                }
                                using(var bytes=new MemoryStream())
                                using(var src=archive.GetEntry(image.Part).Open()) {
                                    src.CopyTo(bytes);
                                    var imageData=new {token=image.Token,name=image.Name,mime=image.Mime,base64=Convert.ToBase64String(bytes.ToArray())};
                                    if(await CreatorAction(site,view,"image",imageData)!="true")throw new Exception("图片位置未定位");
                                }
                            }
                        }
                    }
                    await WaitForCreatorContent(site,view,word,4);
                    var expected=await CreatorAction(site,view,"inspect");
                    int submitted=0;
                    Uri draft=null;
                    string expectedCounters="[1,0]";
                    if(site.Id=="wangyi") {
                        await WaitForWangyiAutosave(fixtureSession);
                        expectedCounters="[0,0]";
                    } else {
                        if(site.Id=="sohu") {
                            string disabled=await CreatorAction(site,view,"save-draft",new {expected});
                            if(disabled!="\"sohu-verification-required\"" ||
                                await view.ExecuteScriptAsync("[window.saveClicks || 0, window.publishClicks || 0]")!="[0,0]")
                                throw new Exception("搜狐号未正确拦截需要实名认证的存草稿操作");
                            await view.ExecuteScriptAsync("document.querySelector('#save').classList.remove('disable-button')");
                            if(!await ReadSohuDraftAccess(site,view) || !CreatorCanPublish(site) ||
                                CreatorAccounts().Single(a=>a.id==site.Id).publishBlocker!=null ||
                                await view.ExecuteScriptAsync("[window.saveClicks || 0, window.publishClicks || 0]")!="[0,0]")
                                throw new Exception("后台确认搜狐号权限后应允许勾选，探测不得写入。");
                        }
                        try { draft=await SaveCreatorDraft(fixtureSession,expected,()=>submitted++); }
                        catch(TimeoutException) { }
                    }
                    var counters=await view.ExecuteScriptAsync("[window.saveClicks || 0, window.publishClicks || 0]");
                    if(submitted!=(site.Id=="wangyi"?0:1) || counters!=expectedCounters)
                        throw new Exception(site.Label+" 草稿动作未通过，草稿和发布点击数 "+counters);
                    if(site.Id=="wangyi") {
                        draft=await CheckWangyiDraftList(site,view,word);
                        await OpenCreatorFixture(view,site.Home);
                    }
                    if(draft==null)throw new Exception(site.Label+" 未取得编号，模拟响应 "+
                        await view.ExecuteScriptAsync("window.fixtureReceipt || null"));
                    if(site.Id=="baijiahao" && (GetCreatorDraftAddress(site,view.Source)!=null ||
                        !draft.Query.Contains("article_id=100001")))
                        throw new Exception("百家号未在地址不变时从回执取出编号");
                    if(site.Id=="qiehao" && (GetCreatorDraftAddress(site,view.Source)!=null || draft.Query!="?articleId=qq_100001"))
                        throw new Exception("企鹅号未在地址不变时从草稿回执取出编号");
                    var entry=ParseResult("2026-11-19 12:00    测试.docx    "+site.Label+" · 草稿 · "+draft.AbsoluteUri);
                    if(entry.platform!=site.Label || entry.label!="草稿" || entry.link!=draft.AbsoluteUri)
                        throw new Exception(site.Label+" 草稿历史显示错误");
                    await OpenCreatorFixture(view,draft.AbsoluteUri);
                    await WaitForCreatorContent(site,view,word,4);
                    await view.ExecuteScriptAsync("document.querySelector('.ProseMirror').prepend(document.createTextNode('额外内容'))");
                    if(CreatorMismatch(word,await ReadCreatorSnapshot(site,view))==null)
                        throw new Exception("误接受修改过的草稿");
                    Console.WriteLine("PASS "+site.Label+" 离线编辑器、"+(doc==tableFile?table+" 表格":"四张图片")+
                        "原位核对、保存重开、内容变化拦截");
                }
            }
        }
        if(CreatorSites.Select(s=>s.Profile).Distinct().Count()!=6)throw new Exception("平台会话名称重复");
        await CheckCreatorPublishDispatch(tableFile);
        await CheckToutiaoMicroTransferGuard();
        CheckCreatorPublicationStates();
        CheckNewCreatorPublicationStates();
        var connected=new CreatorSession(baijiahao);
        using(connected.Web)UpdateCreatorState(connected,"connected");
        using(var reopened=new LoginWindow(null)) {
            if(reopened.CreatorAccounts().Single(a=>a.id=="baijiahao").state!="saved" ||
               reopened.CreatorAccounts().Single(a=>a.id=="toutiao").state!="idle")
                throw new Exception("重启后未正确区分已保存和未连接的账号");
        }
        CheckCreatorProfileRestoration();
        await CheckParallelPublishDispatch();
    }

    async Task CheckParallelPublishDispatch() {
        ClearBatch();
        const string file="并行示例.docx";
        var ids=new[]{"baijiahao","toutiao","weibo","qiehao","sohu"};
        var gates=ids.ToDictionary(id=>id,id=>new TaskCompletionSource<bool>());
        foreach(string id in ids)batch.Add(new BatchItem {platform=id,state="queued"});
        int peak=0;
        var work=RunPublishBatch(file,async item=>{
            peak=Math.Max(peak,runningBatchItems.Count);
            await gates[item.platform].Task;
            ReportCreatorProgress(CreatorSites.Single(site=>site.Id==item.platform),"正在核对 "+item.platform);
            Record(file+"    "+BatchPlatformName(item.platform)+" · 未发出 · 测试结果 "+item.platform);
        });
        if(runningBatchItems.Count!=3 || batch.Count(row=>row.state=="queued")!=2)
            throw new Exception("创作平台应该最多三家同时运行，其余平台排队。");
        gates["weibo"].SetResult(true);
        await Wait(()=>Task.FromResult(batch.Single(row=>row.platform=="qiehao").state=="running"),2,"一个平台完成后应自动开始下一家。");
        if(batch.Single(row=>row.platform=="weibo").step!="测试结果 weibo" ||
            batch.Single(row=>row.platform=="baijiahao").state!="running")
            throw new Exception("提前完成的平台回执不应覆盖慢平台的进度。");
        foreach(var gate in gates.Values)gate.TrySetResult(true);
        await work;
        if(peak!=3 || batch.Any(row=>row.state!="failed" || row.step!="测试结果 "+row.platform))
            throw new Exception("并行批次需要按各账号处理，并正确归属每个平台的回执。");
        Console.WriteLine("PASS 三个平台并行上限、顺序补位和独立回执");
    }

    async Task CheckToutiaoMicroTransferGuard() {
        const string id="7709878372297343528";
        const string endpoint="/mp/agw/article/publish?source=mp&type=article&aid=1231";
        string address=ToutiaoPublishDraftUrl+id;
        var site=CreatorSites.Single(s=>s.Id=="toutiao");
        string header="save=1&entrance=main";
        if(ToutiaoPublishMode(header+"&pgc_id="+id,id)!="public" ||
            ToutiaoPublishMode(header+"&modify_gid="+id,id)!="public" ||
            ToutiaoPublishMode(header+"&pgc_id="+id+"&modify_gid=12345678",id)!=null ||
            ToutiaoPublishMode(header+"&pgc_id="+id+"&pgc_id="+id,id)!=null ||
            ToutiaoPublishMode(header+"&timer_status=1&pgc_id="+id,id)!=null ||
            ToutiaoPublishMode("save=0&entrance=&pgc_id="+id+"&is_app_preview=1",id)!="preview")
            throw new Exception("头条号预览或正式请求识别了错误的稿号。");
        var localBlock=new CreatorPublishJournal {draft=address,
            outcome=new CreatorSubmission {state="uncertain",message=ToutiaoLocallyBlocked}};
        if(!CanRecheckBlockedToutiao(site,localBlock))throw new Exception("未识别确实在本机被拦截的旧任务。");
        localBlock.outcome.message="没有取得回执";
        if(CanRecheckBlockedToutiao(site,localBlock))throw new Exception("普通待核实任务被误判为可重试。");
        for(int candidate=0;candidate<5;candidate++)using(var view=new WebView2 {Visible=false,Width=1100,Height=730}) {
            platform.Controls.Add(view);
            await InitCreatorView(view,site,allowPopups:false);
            int forwarded=0;
            var core=view.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(sender,e)=>{
                bool sent=e.Request.Method=="POST" && e.Request.Uri==ToutiaoCreatorOrigin+endpoint;
                if(sent)forwarded++;
                string content=sent?"{\"code\":0,\"data\":{\"pgc_id\":\""+id+"\"}}":@"<!doctype html><meta charset=utf-8>
                    <div class='article-cover'><div class='article-cover-radio-group'><label class='byte-radio'>
                    <span class='byte-radio-inner checked'>无封面</span></label></div></div>
                    <label class='article-ad-radio'><span class='byte-radio-inner checked'>广告</span></label>
                    <div class='form-tuwen_wtt_trans'><label class='byte-checkbox byte-checkbox-disabled byte-checkbox-checked item-checkbox'>
                    <input type='checkbox' checked disabled>同时发布微头条</label></div>";
                e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(content)),200,"OK",
                    "Content-Type: "+(sent?"application/json":"text/html")+"; charset=utf-8\r\nCache-Control: no-store");
            };
            await OpenCreatorFixture(view,address);
            string settings=ToutiaoPublishSettings+"("+json.Serialize(address)+",true)";
            if(json.Deserialize<string>(await view.ExecuteScriptAsync(settings))!="ready-no-micro")
                throw new Exception("头条号禁用的同步偏好被误判为可用开关。");
            using(var guard=new ToutiaoPublishReplies(core,json,id,true)) {
                await guard.Start();
                string form=(candidate<3?"save=1&entrance=main":"save=0&entrance=")+"&pgc_id="+id;
                if(candidate<2)form+="&extra="+Uri.EscapeDataString(json.Serialize(new {tuwen_wtt_trans_flag=candidate.ToString()}));
                if(candidate==3)form+="&is_app_preview=1";
                string send="fetch("+json.Serialize(endpoint)+", {method:'POST',body:"+json.Serialize(form)+"})";
                if(candidate==0)await view.ExecuteScriptAsync("void Promise.allSettled(["+send+","+send+"]).then(()=>window.guardFixtureDone=true);");
                else await view.ExecuteScriptAsync("void "+send+".catch(()=>{});");
                if(candidate==0 || candidate>=3) {
                    await Wait(()=>Task.FromResult(forwarded==1),5,"正常发文、预览或存草稿未到达模拟平台。");
                    if(candidate==0) {
                        await Wait(async()=>await view.ExecuteScriptAsync("window.guardFixtureDone===true")=="true",5,"重复发文请求尚未完成拦截。");
                        if(forwarded!=1)throw new Exception("同一发布任务放行了两次正式提交。");
                    }
                    continue;
                }
                if(await Task.WhenAny(guard.Public.Task,Task.Delay(6000))!=guard.Public.Task)
                    throw new Exception("头条号请求未产生可核实的拦截结果。");
                var result=await guard.Public.Task;
                if(forwarded!=0 || result.state!="uncertain")
                    throw new Exception("头条号请求拦截未正确要求明确关闭同步，发送计数 "+forwarded+"。");
            }
        }
        Console.WriteLine("PASS 头条号禁用同步场景保持发文可用，发往模拟平台前验证同步值为零并拦截其他值");
    }

    async Task CheckCreatorPublishDispatch(string file) {
        var site=CreatorSites.Single(s=>s.Id=="qiehao");
        Record(Path.GetFileName(file)+"    企鹅号 · 草稿 · https://om.qq.com/main/creation/article?articleId=qq_100001");
        creatorMetrics[site.Id]=new CreatorMetrics {state="ready",articleCount="0"};
        var targets=ResolveTargets(site.Id);
        if(targets==null || targets.Creators.Count!=1 || targets.ToZhihu || targets.ToCsdn || ResolveTargets(site.Id+","+site.Id)!=null)
            throw new Exception("工作台未正确验证第三方账号选择及去重");
        simulatingCreatorPublish=true;
        try {
            await PublishSelected(file,false,false,targets.Creators);
            if(simulatedCreatorSendCount!=1 || !savedCreatorJournalBeforeSend || batch.Single().state!="submitted" ||
                records[0].platform!="企鹅号" || records[0].link!=null || !records[0].verifyCreator || !batch.Single().verifyCreator)
                throw new Exception("企鹅号工作台发布或提交回执落盘失败。请求数 "+simulatedCreatorSendCount+
                    "，提交前已登记 "+savedCreatorJournalBeforeSend+"，"+records[0].detail);
            if(simulatedCreatorLookupCount!=1 || !checkedWhileWorkbenchAvailable || batch.Single().checking)
                throw new Exception("未在工作台可用时自动核实同编号回执");
            using(var reopened=new LoginWindow(null)) {
                var pending=reopened.PendingCreatorRecords();
                if(pending.Length!=1 || pending[0].label!="已提交" ||
                    !SameCreatorPublication("企鹅号",pending[0].creatorDraft,records[0].creatorDraft))
                    throw new Exception("重开工作台未恢复同编号的待核实任务。");
            }
            simulatedCreatorLookupState="2";
            await VerifySubmittedCreatorRecords(PendingCreatorRecords("企鹅号"));
            if(simulatedCreatorLookupCount!=2 || batch.Single().state!="review" || records[0].label!="审核中" || batch.Single().checking)
                throw new Exception("恢复查询后未同步平台确认的审核状态。");
            await PublishSelected(file,false,false,targets.Creators);
            if(simulatedCreatorSendCount!=1)
                throw new Exception("已收到平台提交回执后，再次选择同文档仍发送请求");
            if(simulatedCreatorLookupCount!=2 || batch.Single().state!="review" || records[0].label!="审核中" || batch.Single().checking)
                throw new Exception("平台确认审核后未自动更新本次结果与历史记录。查询次数 "+simulatedCreatorLookupCount+
                    "，本次结果 "+batch.Single().state+"，历史 "+records[0].label+"，核实中 "+batch.Single().checking+"。");
            simulatedCreatorLookupState="signin";
            await VerifyCreatorRecord(records[0]);
            var account=CreatorAccounts().Single(row=>row.id==site.Id);
            if(account.state!="signin" || account.canSubmit!=false || !account.connected ||
                records[0].label!="审核中" || ResolveTargets(site.Id)!=null)
                throw new Exception("官方登录跳转没有清除发布选择，或覆盖了已有审核回执。");
            using(var reopened=new LoginWindow(null)) {
                if(reopened.CreatorAccounts().Single(row=>row.id==site.Id).state!="signin")
                    throw new Exception("重启后忘记了平台已确认的登录过期。");
            }
            var relogged=new CreatorSession(site);
            using(relogged.Web)UpdateCreatorState(relogged,"connected");
            if(creatorSignInRequired.Contains(site.Id) || CreatorAccounts().Single(row=>row.id==site.Id).canSubmit!=true)
                throw new Exception("重新确认登录后没有恢复账号发布选择。");
            using(var reopened=new LoginWindow(null)) {
                if(reopened.CreatorAccounts().Single(row=>row.id==site.Id).state!="saved")
                    throw new Exception("重启后沿用了登录前的过期状态。");
            }
            Console.WriteLine("PASS 工作台防重复提交、启动恢复核实、账号过期保护与重新登录恢复");
        } finally {simulatingCreatorPublish=false;}
    }

    void CheckCreatorPublicationStates() {
        const string qqId="qq_100001",qqUrl="https://page.om.qq.com/page/official001";
        Func<string,int,string> qiehao=(id,state)=>json.Serialize(new { response=new {code=0},
            data=new {articles=new[]{new {article_id=id,status=state.ToString(),url=qqUrl}}} });
        if(ParseQiehaoPublication(qqId,qiehao(qqId,0)).state!="draft" || ParseQiehaoPublication(qqId,qiehao(qqId,2)).state!="review" ||
            ParseQiehaoPublication(qqId,qiehao(qqId,3)).state!="failed" || ParseQiehaoPublication(qqId,qiehao(qqId,13)).state!="failed" ||
            ParseQiehaoPublication(qqId,qiehao(qqId,1)).url!=qqUrl || ParseQiehaoPublication(qqId,qiehao("other_1001",1))!=null ||
            ParseQiehaoPublication(qqId,qiehao(qqId,12))!=null)
            throw new Exception("企鹅号按同编号核验状态失败");
        const string ttId="7709878372297343528",ttUrl="https://www.toutiao.com/article/7710008372297343528/";
        Func<string,int,string,string> toutiao=(id,state,url)=>json.Serialize(new { errno=20100,
            data=new[]{new {content=json.Serialize(new {article_attr=new {gid="7710008372297343528",status=state,
                pgc_cell=json.Serialize(new {pgc_id=id,article_url=url})}})}} });
        if(ParseToutiaoPublication(ttId,toutiao(ttId,2,ttUrl)).url!=ttUrl || ParseToutiaoPublication(ttId,toutiao(ttId,1,ttUrl)).state!="review" ||
            ParseToutiaoPublication(ttId,toutiao(ttId,3,ttUrl)).state!="failed" || ParseToutiaoPublication(ttId,toutiao(ttId,4,ttUrl)).state!="blocked" ||
            ParseToutiaoPublication(ttId,toutiao(ttId,2,"https://mp.toutiao.com/profile_v4/graphic/preview?pgc_id="+ttId)).state!="submitted" ||
            ParseToutiaoPublication(ttId,toutiao("7711000000000000000",2,ttUrl))!=null ||
            ParseToutiaoDraftState(ttId,json.Serialize(new {pgc_id=ttId,is_draft=true,article_pgc=new {status=1}})).state!="draft" ||
            ParseToutiaoDraftState(ttId,json.Serialize(new {pgc_id=ttId,is_draft=false}))!=null ||
            ParseToutiaoDraftState(ttId,json.Serialize(new {code=100001,err_no=100001,message="审核中，无法修改",reason="审核中，无法修改"})).state!="review" ||
            ParseToutiaoDraftState(ttId,json.Serialize(new {pgc_id="7711000000000000000",code=100001,err_no=100001,message="审核中，无法修改",reason="审核中，无法修改"}))!=null)
            throw new Exception("头条号按同编号核验状态失败");
        var submitted=records.First(row=>row.platform=="企鹅号" && row.label=="审核中");
        SaveVerification(submitted,"审核中 · 平台确认审核中。");
        if(batch.Single().state!="review" || !records.Where(row=>row.creatorDraft==submitted.creatorDraft).All(row=>row.label=="审核中"))
            throw new Exception("同一草稿的历史及本次发布状态未同步");
        SaveVerification(submitted,"已发布 · "+qqUrl);
        using(var reopened=new LoginWindow(null)) {
            if(batch.Single().state!="published" || batch.Single().url!=qqUrl ||
                !reopened.records.Any(row=>row.platform=="企鹅号" && row.label=="已发布" && row.link==qqUrl) ||
                reopened.PendingCreatorRecords().Length!=0)
                throw new Exception("发布核实没有保存正确的公开地址");
        }
        Console.WriteLine("PASS 企鹅号、头条号按稿件编号核实结果，拒绝预览链接并刷新本次发布与历史记录");
    }

    void CheckNewCreatorPublicationStates() {
        var weiboSite=CreatorSites.First(site=>site.Id=="weibo");
        var securityPrompt=json.Serialize(new {code=100000,data=new {content_length=120,geetest=new {verifyUrl="https://security.weibo.com/verify/index"}}});
        if(WeiboNextSaveState(securityPrompt)!="verification" ||
            WeiboNextSaveState("{\"code\":100000,\"data\":{\"content_length\":120,\"geetest\":[]}}")!="saved" ||
            WeiboNextSaveState("{\"code\":100000,\"data\":{}}")!="unconfirmed" ||
            !IsWeiboNextForm("id=4079672&action=2") || IsWeiboNextForm("action=1") || IsWeiboNextForm("action=1&action=2") ||
            IsWeiboNextSave(new Uri("https://card.weibo.com/article/v5/aj/editor/draft/save?id=4079671"),"4079672") ||
            !weiboSite.Allows("https://security.weibo.com/verify/index") || weiboSite.Allows("https://security.weibo.com/foo") ||
            weiboSite.Allows("https://security.weibo.com.evil.test/verify/index") ||
            WeiboSubmitOutcome("{\"code\":500002,\"msg\":\"发布失败，请手动保存后重新提交\",\"data\":[]}").state!="save-required" ||
            WeiboSubmitOutcome("{\"code\":500002,\"msg\":\"请核实状态\"}").state!="uncertain" ||
            WeiboSubmitOutcome("{\"code\":100000}").state!="submitted")
            throw new Exception("微博同稿保存、人验、明确拒绝与可恢复状态的识别失败");
        const string bjId="1882293766896995541",bjPublic="https://baijiahao.baidu.com/s?id="+bjId;
        Func<string,string,string,string,CreatorSubmission> baijia=(id,type,state,link)=>BaijiahaoPublicationOutcome(bjId,
            ReadBaijiahaoPublicationList(json.Serialize(new {errno=0,data=new {list=new[]{
                new {article_id=id,type,status=state,url=link,quality_status="rejected"}}}})));
        if(baijia(bjId,"news","publish","http://baijiahao.baidu.com/s?id="+bjId).url!=bjPublic ||
            baijia(bjId,"news","draft",bjPublic).state!="draft" || baijia(bjId,"news","audit",null).state!="review" ||
            baijia(bjId,"news","rejected",bjPublic).state!="failed" || baijia(bjId,"news","publish","https://baijiahao.baidu.com/builder/preview/s?id="+bjId).state!="submitted" ||
            baijia(bjId,"video","publish",bjPublic)!=null || baijia("1882293766896995542","news","publish",bjPublic)!=null)
            throw new Exception("百家号公开、审核和草稿识别失败");

        const string wyId="LC4NIP6O0556QNQP",wyLink="https://www.163.com/dy/article/"+wyId+".html";
        Func<string,int,int,string,CreatorSubmission> wangyi=(id,type,state,reason)=>ParseWangyiPublication(wyId,
            json.Serialize(new {code=1,data=new {list=new[]{new {articleId=id,contentType=type,contentState=state,unrecomReason=reason}}}}));
        if(wangyi(wyId,1,0,null).state!="draft" || wangyi(wyId,1,1,null).state!="review" || wangyi(wyId,1,3,null).url!=wyLink ||
            wangyi(wyId,1,3,"内容不符合社区规范，仅自己可见").state!="blocked" || wangyi(wyId,1,2,null).state!="failed" ||
            wangyi(wyId,1,5,null).state!="failed" || wangyi(wyId,2,3,null)!=null || wangyi("wrong-id1",1,3,null)!=null)
            throw new Exception("网易号同编号状态识别或受限文章处理失败");

        Func<string,int?,bool,CreatorSubmission> weibo=(id,state,history)=>WeiboPublicationOutcome("4079518",
            ReadWeiboPublicationList(json.Serialize(new {ok=1,code=100000,
                data=new {list=new[]{new {id,status=state}}}}),history),history);
        if(weibo("4079518",0,false).state!="draft" || weibo("4079518",3,false).state!="failed" ||
            weibo("4079518",1,false)!=null || weibo("4079518",null,true).state!="submitted" ||
            weibo("4079518",null,true).url!=null || weibo("4079519",null,true)!=null)
            throw new Exception("微博长文草稿及发布历史证据识别失败");

        const string sohuId="7710881111111111111";
        Func<string,int,int?,int,CreatorSubmission> sohu=(id,state,visibility,type)=>ParseSohuPublication(sohuId,
            json.Serialize(new {success=true,code=2000000,data=new {news=new[]{new {id,status=state,userId="102003",
                visibleToLoginedUsers=visibility,type,secureScore=10}}}}));
        if(sohu(sohuId,1,0,1).state!="draft" || sohu(sohuId,2,0,1).state!="review" || sohu(sohuId,3,0,1).state!="failed" ||
            sohu(sohuId,4,0,1).url!="https://www.sohu.com/a/"+sohuId+"_102003?sec=wd" ||
            sohu(sohuId,4,0,30).url!="https://biznews.sohu.com/a/"+sohuId+"_102003" ||
            sohu(sohuId,4,1,1).state!="blocked" || sohu(sohuId,4,null,1).state!="submitted" || sohu("7710881111111111211",4,0,1)!=null)
            throw new Exception("搜狐号文章核验或可见范围识别失败");
        if(!SameCreatorPublication("百家号","https://baijiahao.baidu.com/builder/rc/edit?type=news&article_id="+bjId,
                "https://baijiahao.baidu.com/builder/rc/edit?article_id="+bjId+"&type=news") ||
            SameCreatorPublication("百家号","https://baijiahao.baidu.com/builder/rc/edit?type=news&article_id="+bjId,
                "https://baijiahao.baidu.com/builder/rc/edit?article_id="+bjId+"&article_id=1882293766896995542"))
            throw new Exception("记录必须按有效且唯一的稿件编号关联");
        Console.WriteLine("PASS 百家号、微博、搜狐号、网易号按准确编号核实稿件，区分站内、未知和公开状态");
    }

    async Task<Uri> CheckWangyiDraftList(CreatorSite site,WebView2 view,CreatorWord word) {
        await OpenCreatorFixture(view,"https://mp.163.com/test-draft-list");
        await view.ExecuteScriptAsync("history.replaceState(null,'',"+json.Serialize(WangyiDraftListUrl)+")");
        await Wait(()=>Task.FromResult(view.Source!=null && view.Source.AbsoluteUri==WangyiDraftListUrl),5,"网易号草稿列表地址未生效");
        var draft=await ReadWangyiDraftList(site,view,word.Title);
        if(draft==null || !draft.Fragment.EndsWith("/100001?option=editDraft") ||
            await ReadWangyiDraftList(site,view,word.Title+"2")!=null)
            throw new Exception("网易号未定位唯一同标题草稿，或误认相近标题");
        await view.ExecuteScriptAsync("document.querySelector('#edit').href='#/article-publish/100001?option=edit'");
        if(await ReadWangyiDraftList(site,view,word.Title)!=null)throw new Exception("网易号将已发布编辑地址误判为草稿");
        await view.ExecuteScriptAsync("document.querySelector('#edit').href='#/article-publish/100001?option=editDraft'; "+
            "const card=document.querySelector('.src-main-page-ContentManage-Card-index-module-card'); card.after(card.cloneNode(true));");
        try { await ReadWangyiDraftList(site,view,word.Title); }
        catch(InvalidOperationException ex) {
            if(ex.Message.Contains("多个同标题"))return draft;
            throw;
        }
        throw new Exception("网易号误选多个同标题草稿中的一篇");
    }

    async Task CheckBaijiahaoLoginPage() {
        var site=CreatorSites.First(s=>s.Id=="baijiahao");
        await OpenCreator(site);
        var session=creatorVisible;
        await Wait(async()=>{
            if(session.State!="signin" || session.Web.Source==null || session.Web.Source.AbsolutePath!="/builder/theme/bjh/login")
                return false;
            return await session.Web.ExecuteScriptAsync("document.body.innerText.includes('登录/注册百家号')")=="true";
        },20,"知稿没有显示百家号官方登录页。");
        Console.WriteLine("PASS 知稿内嵌百家号 HTTPS 登录页已显示「登录/注册百家号」");
    }

    async Task CheckExistingCreatorSession(string platformId,string document=null,bool inspectEditor=false,bool wangyiReadback=false) {
        var site=CreatorSites.First(s=>s.Id==platformId);
        string existing=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zhigao","WebView2");
        browserEnvironment=await CoreWebView2Environment.CreateAsync(null,existing);
        bool homepageCheck=platformId=="baijiahao" || platformId=="toutiao";
        await OpenCreator(site,homepageCheck?null:site.Editor);
        var session=creatorVisible;
        try {
            await Wait(async()=>{
                if(!session.Navigating)await CheckCreator(session);
                return session.State=="connected" || session.State=="editor" || session.State=="signin";
            },20,site.Label+" 未进入可确认的后台。");
        } catch(TimeoutException ex) {
            var address=session.Web.Source;
            throw new InvalidOperationException(ex.Message+" 状态 "+session.State+"，页面 "+
                (address==null?"未打开":address.GetLeftPart(UriPartial.Path))+"。",ex);
        }
        if(session.State=="signin")throw new InvalidOperationException(site.Label+" 仍停在官方登录页。");
        Console.WriteLine("PASS "+site.Label+" 已登录现有创作后台");
        if(wangyiReadback) {
            using(var file=File.OpenRead(document)) {
                var word=new CreatorWord(document,file,true);
                var draft=await FindWangyiDraftInList(site,word.Title,12);
                await ReadCreatorDraftBack(site,draft,word);
                Console.WriteLine("PASS 真实 网易号 原位表格图片草稿重新核对 "+draft.AbsoluteUri);
            }
            return;
        }
        if(document==null && !inspectEditor)return;
        queuedArticlePath=document;
        if(session.State!="editor" && inspectEditor && site.Id=="weibo")return;
        if(session.State!="editor")await OpenCreatorEditor(session);
        await Wait(async()=>{
            if(!session.Navigating)await CheckCreator(session);
            return session.State=="editor" && !session.Navigating;
        },25,"未定位到 "+site.Label+" 图文编辑器。");
        if(inspectEditor) {
            var current=await ReadCreatorSnapshot(site,session.Web);
            Console.WriteLine(json.Serialize(new { hasDraftId=GetCreatorDraftAddress(site,session.Web.Source)!=null,
                titleSize=current.title.Length,textSize=current.text.Length,tables=current.tables.Length,images=current.images.Length,
                lists=current.lists.Length }));
            Console.WriteLine("draft-readiness="+await CreatorAction(site,session.Web,"draft-readiness"));
            Console.WriteLine(await session.Web.ExecuteScriptAsync(@"[...document.querySelectorAll('button,[role=button],a')].filter(e=>e.getClientRects().length)
                .map(e=>e.innerText.trim()).filter(t=>t.length<=20 && /草稿|新建|创建|清空|继续上次|恢复|暂存|保存|发布|预览/.test(t)).slice(0,24)"));
            Console.WriteLine(await session.Web.ExecuteScriptAsync(@"(document.body.innerText.match(
                /(?:草稿已保存|已自动保存|自动保存|已保存|保存中|草稿箱|保存失败|暂存成功)/g)||[]).slice(0,15)"));
            Console.WriteLine(await session.Web.ExecuteScriptAsync(@"[...document.querySelectorAll('span,label,p,div')].filter(e=>
                e.getClientRects().length && !e.children.length && e.textContent.length<70 && /自动保存/.test(e.textContent))
                .slice(0,8).map(e=>({text:e.textContent.trim(),cls:e.className, parent:e.parentElement?.className}))"));
            Console.WriteLine(await session.Web.ExecuteScriptAsync(@"[...document.querySelectorAll('.ProseMirror[contenteditable=true]')].map(p=>
                [...p.children].map(e=>({name:e.tagName,textSize:e.textContent.length,css:e.className,
                    placeholderText:['请输入正文','请输入内容','请输入文章','点击输入正文','请输入文本'].includes(e.textContent.trim()),
                    marked:[...e.querySelectorAll('*')].slice(0,9).map(d=>({tag:d.tagName, cls:d.className,
                        hasPlaceholder:d.hasAttribute('placeholder')||d.hasAttribute('data-placeholder'),
                        edit:d.getAttribute('contenteditable')}))
                })).slice(0,12))"));
            return;
        }
        await LoadCreatorWord(session);
        var result=records.FirstOrDefault(entry=>entry.file==Path.GetFileName(document) && entry.platform==site.Label);
        if(result==null || result.label!="草稿" || result.link==null)throw new Exception(session.Notice);
        Console.WriteLine("PASS 真实 "+site.Label+" 草稿已保存并重开核对 "+result.link);
    }

    void CheckCreatorProfileRestoration() {
        using(var firstRun=new LoginWindow(null)) {
            foreach(var site in CreatorSites) {
                firstRun.RememberCreator(site.Id);
                firstRun.SaveCreatorProfileName(site,site.Label+"测试作者");
            }
            firstRun.SaveCreatorProfileName(CreatorSites[0],"未登录");
        }
        using(var reopened=new LoginWindow(null)) {
            var accounts=reopened.CreatorAccounts().ToArray();
            if(accounts.Length!=CreatorSites.Length || accounts.Any(row=>row.name!=row.platform+"测试作者" || row.state!="saved"))
                throw new Exception("重开工作台后，各平台应分别显示已保存的名称，等待官方验证。");
            var site=CreatorSites[0];
            reopened.verifiedCreatorProfiles.Add(site.Id);
            if(reopened.CreatorAccounts().First().state!="connected")
                throw new Exception("官方资料验证通过后应恢复已登录状态。");
            reopened.MarkCreatorSignInRequired(site);
            var expired=reopened.CreatorAccounts().First();
            if(expired.state!="signin" || expired.name!=site.Label+"测试作者" || expired.canSubmit!=false)
                throw new Exception("会话过期后应保留上次账号名称，并禁用发布。");
        }
        Console.WriteLine("PASS 账号名称按平台保存，重启恢复，过期账号保留名称但禁用发布");
    }

    async Task CheckLiveCreatorProfiles() {
        string stored=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zhigao","WebView2");
        browserEnvironment=await CoreWebView2Environment.CreateAsync(null,stored);
        await Task.WhenAll(CreatorSites.Select(site=>RefreshCreatorProfile(site,true)));
        foreach(var account in CreatorAccounts()) {
            Console.WriteLine(account.platform+" "+account.state+" "+(account.name??"—"));
            if(account.id=="sohu")Console.WriteLine("搜狐号发布可选="+account.canSubmit+" 原因="+(account.publishBlocker??"—"));
            if(account.id!="wangyi" && (account.state!="connected" || account.name==null))
                throw new InvalidOperationException(account.platform+" 未读到已登录账号的名称。");
        }
    }

    async Task CheckLiveCreatorDeclaration(string platformId) {
        var site=CreatorSites.Single(s=>s.Id==platformId && (s.Id=="qiehao" || s.Id=="sohu"));
        string stored=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zhigao","WebView2");
        browserEnvironment=await CoreWebView2Environment.CreateAsync(null,stored);
        var session=new CreatorSession(site);
        using(var view=session.Web) {
            view.Width=1200;
            view.Height=800;
            platform.Controls.Add(view);
            await InitCreatorView(view,site,allowPopups:false);
            var core=view.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(sender,e)=>{
                if(e.Request.Method!="GET" && e.Request.Method!="HEAD" && e.Request.Method!="OPTIONS")
                    e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(),403,"Read-only check","");
            };
            if(platformId=="qiehao")await IsolateQiehaoNewDraftCache(view);
            await NavigateCreatorPublishEditor(session,new Uri(site.Editor),true);
            await Task.Delay(1750);
            var before=await ReadCreatorSnapshot(site,view);
            if(before==null || before.title!="" || before.text!="" || GetCreatorDraftAddress(site,view.Source)!=null)
                throw new Exception(site.Label+" 新编辑器没有保持空白。");
            await SetCreatorDeclaration(session,"ai");
            var after=await ReadCreatorSnapshot(site,view);
            if(after==null || after.title!="" || after.text!="" || GetCreatorDraftAddress(site,view.Source)!=null)
                throw new Exception(site.Label+" 声明检查改变了文章内容。");
            Console.WriteLine("PASS "+site.Label+" 空白编辑器及声明选项，所有外部写入已拦截");
        }
    }

    async Task CheckLiveCreatorMetrics(string platformId) {
        var site=CreatorSites.Single(s=>s.Id==platformId);
        string stored=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zhigao","WebView2");
        browserEnvironment=await CoreWebView2Environment.CreateAsync(null,stored);
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        var refresh=RefreshCreatorMetrics(site,true);
        while(!refresh.IsCompleted) {
            CreatorMetrics partial;
            if(creatorMetrics.TryGetValue(platformId,out partial) && HasCreatorMetrics(partial)) {
                Console.WriteLine(site.Label+" 首个已确认指标 "+elapsed.ElapsedMilliseconds+" ms");
                break;
            }
            await Task.WhenAny(refresh,Task.Delay(100));
        }
        await refresh;
        CreatorMetrics result;
        if(!creatorMetrics.TryGetValue(platformId,out result) || result.state!="ready")
            throw new InvalidOperationException(site.Label+" 未取得可核对的账号指标。");
        Console.WriteLine("PASS 真实 "+site.Label+" 账号指标 "+elapsed.ElapsedMilliseconds+" ms "+json.Serialize(result));
    }

    async Task CheckLiveCreatorPublication(string platformId,string address,string expected) {
        var site=CreatorSites.Single(candidate=>candidate.Id==platformId);
        var tracked=ParseResult("2026-11-19 12:00    知稿草稿联调表格测试.docx    "+site.Label+" · 草稿 · "+address);
        if(!tracked.verifyCreator)throw new InvalidOperationException("草稿地址无法核实。");
        records.Insert(0,tracked);
        if(expected=="signin")RememberCreator(site.Id);
        string stored=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zhigao","WebView2");
        browserEnvironment=await CoreWebView2Environment.CreateAsync(null,stored);
        await VerifyCreatorRecord(tracked);
        bool matched=expected=="signin"?verificationNotice=="请重新登录"+site.Label+"后再核实。" &&
            CreatorAccounts().Any(account=>account.id==platformId && account.state=="signin" && account.connected && account.canSubmit==false):
            expected=="published"?tracked.label=="已发布" && tracked.link!=null:
            verificationNotice==site.Label+"仍显示这篇稿件在草稿箱。";
        if(!matched)throw new InvalidOperationException(verificationNotice);
        Console.WriteLine("PASS 真实 "+verificationNotice);
    }

    async Task CheckLiveCreatorDraftContent(string platformId,string draftUrl,string file) {
        var site=CreatorSites.Single(candidate=>candidate.Id==platformId);
        var address=CreatorRecordDraft(site,draftUrl);
        string id=CreatorPublicationId(site,draftUrl);
        if(id==null)throw new InvalidOperationException("必须提供该平台已有编号的草稿地址。");
        string stored=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zhigao","WebView2");
        browserEnvironment=await CoreWebView2Environment.CreateAsync(null,stored);
        using(var source=File.OpenRead(file)) {
            var word=new CreatorWord(file,source,site.Id=="wangyi");
            await ReadCreatorDraftBack(site,address,word,true);
            int cells=word.Tables.Sum(table=>table.cells.Sum(row=>row.Length));
            Console.WriteLine("PASS 真实 "+site.Label+" 同编号 "+id+" 草稿重开，标题、正文、表格位置、"+
                cells+" 个表格单元格及 "+word.Images.Count+" 张正文图片均已核对。并已拦截写入请求。");
        }
    }

    async Task CheckLiveToutiaoReadiness(string id) {
        var site=CreatorSites.Single(s=>s.Id=="toutiao");
        string stored=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zhigao","WebView2");
        browserEnvironment=await CoreWebView2Environment.CreateAsync(null,stored);
        using(var view=new WebView2 {Visible=true,Dock=DockStyle.Fill}) {
            platform.Controls.Add(view);
            view.SendToBack();
            await InitCreatorView(view,site,allowPopups:false);
            var core=view.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(sender,e)=>{
                if(e.Request.Method!="GET" && e.Request.Method!="HEAD" && e.Request.Method!="OPTIONS")
                    e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(),403,"Read-only test","");
            };
            string address=ToutiaoPublishDraftUrl+id;
            await OpenCreatorFixture(view,address);
            string observed=null;
            try {
                observed=await PrepareToutiaoPublishSettings(view,ToutiaoPublishSettings+"("+json.Serialize(address)+",");
            } catch(TimeoutException) { }
            if(observed!="ready-no-micro" && observed!="ready") {
                Console.Error.WriteLine(await view.ExecuteScriptAsync(@"[...document.querySelectorAll('.article-cover-radio-group label.byte-radio')].map(e=>
                    ({label:e.innerText.trim(),cls:e.className,checked:e.querySelector('input')?.checked,disabled:e.querySelector('input')?.disabled,
                     type:e.querySelector('input')?.type,inner:e.querySelector('.byte-radio-inner')?.className}))"));
                throw new InvalidOperationException("头条号实机发布设置返回 "+observed);
            }
            Console.WriteLine("PASS 真实头条号草稿已通过无封面和单独发文章的设置检查，仅核对设置");
        }
    }

    public static int RunCreatorPlatformCheck(string tableFile,string imageFile,string testRoot,string mode,string draftUrl=null) {
        typeof(LoginWindow).GetField("dataFolder",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,testRoot);
        int result=1;
        var form=new LoginWindow(null) {ShowInTaskbar=false,Opacity=0};
        form.page.CoreWebView2InitializationCompleted+=(s,e)=>{if(e.IsSuccess)form.StubCreatorNavigation(form.page);};
        form.Shown+=async delegate {
            try {
                await form.Wait(()=>Task.FromResult(form.shellReady),10,"工作台未加载");
                if(mode=="--baijiahao-login")await form.CheckBaijiahaoLoginPage();
                else if(mode=="--baijiahao-session-readonly" || mode=="--baijiahao-live-draft")
                    await form.CheckExistingCreatorSession("baijiahao",mode=="--baijiahao-live-draft"?tableFile:null);
                else if(mode=="--creator-live-draft" || mode=="--creator-inspect-editor")await form.CheckExistingCreatorSession(imageFile,tableFile,mode=="--creator-inspect-editor");
                else if(mode=="--creator-verify-wangyi-draft")await form.CheckExistingCreatorSession("wangyi",tableFile,false,true);
                else if(mode=="--creator-metrics")await form.CheckLiveCreatorMetrics(imageFile);
                else if(mode=="--creator-profiles")await form.CheckLiveCreatorProfiles();
                else if(mode=="--creator-declaration-readonly")await form.CheckLiveCreatorDeclaration(imageFile);
                else if(mode=="--creator-readback")await form.CheckLiveCreatorDraftContent(imageFile,draftUrl,tableFile);
                else if(mode=="--creator-publication-draft" || mode=="--creator-publication-existing" || mode=="--creator-publication-signin")
                    await form.CheckLiveCreatorPublication(imageFile,tableFile,
                        mode=="--creator-publication-signin"?"signin":mode=="--creator-publication-draft"?"draft":"published");
                else if(mode=="--toutiao-readiness")await form.CheckLiveToutiaoReadiness(imageFile);
                else await form.CheckCreatorEditors(tableFile,imageFile);
                result=0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); }
            finally { form.Close(); }
        };
        Application.Run(form);
        return result;
    }
}

static class CreatorPlatformsCheck {
    [STAThread] static int Main(string[] args) {
        if(args.Length==4 && args[0]=="--creator-readback") {
            Application.EnableVisualStyles();
            string readbackRoot=Path.Combine(Path.GetTempPath(),"Zhigao-CreatorTest-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(readbackRoot);
            return LoginWindow.RunCreatorPlatformCheck(args[3],args[1],readbackRoot,args[0],args[2]);
        }
        if(args.Length==2 && args[0]=="--creator-verify-wangyi-draft") {
            Application.EnableVisualStyles();
            return LoginWindow.RunCreatorPlatformCheck(args[1],null,
                Path.Combine(Path.GetTempPath(),"Zhigao-CreatorTest-"+Guid.NewGuid().ToString("N")),args[0]);
        }
        if((args.Length==3 && (args[0]=="--creator-live-draft" || args[0]=="--creator-publication-draft" || args[0]=="--creator-publication-existing" ||
              args[0]=="--creator-publication-signin")) ||
            (args.Length==2 && (args[0]=="--creator-inspect-editor" || args[0]=="--creator-metrics" || args[0]=="--toutiao-readiness" ||
              args[0]=="--creator-declaration-readonly"))) {
            Application.EnableVisualStyles();
            string isolated=Path.Combine(Path.GetTempPath(),"Zhigao-CreatorTest-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolated);
            return LoginWindow.RunCreatorPlatformCheck(args.Length==3?args[2]:null,args[1],isolated,args[0]);
        }
        string mode=args.Length==1 || (args.Length==2 && args[0]=="--baijiahao-live-draft")?args[0]:null;
        if(args.Length!=2 && mode!="--baijiahao-login" && mode!="--baijiahao-session-readonly" && mode!="--creator-profiles") {
            Console.Error.WriteLine("请输入 4×4 表格稿与四张图片稿的路径，或选择百家号登录检查。");
            return 2;
        }
        Application.EnableVisualStyles();
        string root=Path.Combine(Path.GetTempPath(),"Zhigao-CreatorTest-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return LoginWindow.RunCreatorPlatformCheck(mode=="--baijiahao-live-draft"?args[1]:mode==null?args[0]:null,
            mode==null?args[1]:null,root,mode);
    }
}
