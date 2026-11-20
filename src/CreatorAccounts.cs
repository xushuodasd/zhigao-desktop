using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class WorkbenchAccount {
        public string id { get; set; }
        public string platform { get; set; }
        public string mark { get; set; }
        public string name { get; set; }
        public bool connected { get; set; }
        public string state { get; set; }
        public bool publishable { get; set; }
        public bool? canSubmit { get; set; }
        public string publishBlocker { get; set; }
        public bool analytics { get; set; }
    }

    sealed class CreatorSite {
        public readonly string Id, Label, Mark, Home, Editor;
        readonly string[] hosts;
        public readonly string[] editorPaths;
        public readonly string LoginPath;
        public readonly string TitleFields, BodyFields;

        public CreatorSite(string id, string label, string mark, string home, string editor, string[] editorPaths,
            string loginPath, string titleFields, string bodyFields, params string[] hosts) {
            Id=id; Label=label; Mark=mark; Home=home; Editor=editor; this.editorPaths=editorPaths;
            LoginPath=loginPath; TitleFields=titleFields; BodyFields=bodyFields; this.hosts=hosts;
        }

        public string Profile { get { return "Zhigao-Creator-"+Id; } }
        public bool Allows(string address) {
            Uri url;
            return address=="about:blank" || (Uri.TryCreate(address,UriKind.Absolute,out url) &&
                url.Scheme==Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length==0 &&
                (url.Host==new Uri(Home).Host || hosts.Contains(url.Host,StringComparer.OrdinalIgnoreCase) ||
                Id=="weibo" && url.Host=="security.weibo.com" && url.AbsolutePath=="/verify/index"));
        }
        public bool IsEditor(Uri address) {
            if(address==null || !Allows(address.AbsoluteUri) || address.Host!=new Uri(Editor ?? Home).Host)return false;
            string path=address.AbsolutePath.TrimEnd('/');
            string fragmentPath=address.Fragment.Split('?')[0];
            return editorPaths.Contains(path,StringComparer.OrdinalIgnoreCase) ||
                editorPaths.Contains(path+fragmentPath,StringComparer.OrdinalIgnoreCase) ||
                (Id=="wangyi" && path=="/subscribe_v4/index.html" &&
                    Regex.IsMatch(fragmentPath,@"^#/article-publish/[a-zA-Z0-9_-]{6,80}$"));
        }
        public string SecureLegacyNavigation(string address) {
            Uri url;
            if(!Uri.TryCreate(address,UriKind.Absolute,out url) || url.Scheme!="http" || !url.IsDefaultPort ||
                url.UserInfo.Length!=0 || url.Host!=new Uri(Home).Host)return null;
            if(Id=="baijiahao" && url.AbsolutePath=="/pcui/register/index")
                return "https://baijiahao.baidu.com/builder/theme/bjh/login";
            if(Id=="wangyi" && url.AbsolutePath=="/subscribe_v4/index.html")
                return new UriBuilder(url) {Scheme="https",Port=-1}.Uri.AbsoluteUri;
            return null;
        }
    }

    const string CommonTitle = "textarea[placeholder*='标题'],input[placeholder*='标题'],[contenteditable='true'][data-placeholder*='标题']";
    const string CommonBody = ".ProseMirror[contenteditable='true'],.ql-editor[contenteditable='true'],.w-e-text[contenteditable='true'],.public-DraftEditor-content[contenteditable='true'],[contenteditable='true'][data-placeholder*='正文']";
    static readonly CreatorSite[] CreatorSites = {
        new CreatorSite("baijiahao","百家号","百","https://baijiahao.baidu.com/builder/rc/home",
            "https://baijiahao.baidu.com/builder/rc/edit?type=news",new[]{"/builder/rc/edit"},@"/builder/theme/bjh/login|/login",
            ".input-box > [contenteditable=true],.input-box textarea","iframe[id^='ueditor'],.news-editor-pc[contenteditable=true]","passport.baidu.com","wappass.baidu.com"),
        new CreatorSite("toutiao","头条号","头","https://mp.toutiao.com/",
            "https://mp.toutiao.com/profile_v4/graphic/publish",new[]{"/profile_v4/graphic/publish","/profile_v4/graphic/article/publish"},
            @"/auth/page/login|/login",CommonTitle,CommonBody,"sso.toutiao.com","passport.toutiao.com","sso.bytedance.com"),
        new CreatorSite("weibo","新浪微博","微","https://card.weibo.com/article/v5/editor",
            "https://card.weibo.com/article/v5/editor",new[]{"/article/v5/editor"},@"/newlogin|/signin|/login",
            CommonTitle,CommonBody+",#editor[contenteditable='true'],#editor div[contenteditable='true'],iframe[id^='ueditor']","weibo.com","www.weibo.com",
            "passport.weibo.com","login.sina.com.cn","api.weibo.com","account.weibo.com","me.weibo.com"),
        new CreatorSite("qiehao","企鹅号","企","https://om.qq.com/main",
            "https://om.qq.com/main/creation/article",new[]{"/main/creation/article"},@"/userAuth/(?:index|login)|/login",
            CommonTitle+",#omEditorTitle[contenteditable='true'],#omEditorTitle [contenteditable='true'],#omEditorTitle [data-placeholder*='标题']",
            CommonBody+",iframe[id^='ueditor']","graph.qq.com","xui.ptlogin2.qq.com","ssl.ptlogin2.qq.com",
            "open.weixin.qq.com","wx.qq.com","accounts.qq.com"),
        new CreatorSite("sohu","搜狐号","狐","https://mp.sohu.com/mpfe/v4/",
            "https://mp.sohu.com/mpfe/v4/contentManagement/news/addarticle",
            new[]{"/mpfe/v4/contentManagement/news/addarticle"},@"/login",
            CommonTitle,CommonBody+",iframe[id^='ueditor']","passport.sohu.com","login.sohu.com","open.weixin.qq.com",
            "graph.qq.com","api.weibo.com"),
        new CreatorSite("wangyi","网易号","易","https://mp.163.com/",
            "https://mp.163.com/subscribe_v4/index.html#/article-publish",
            new[]{"/subscribe_v4/index.html#/article-publish","/subscribe_v4/index.html#/edit/article"},@"/login(?:\.html)?",
            CommonTitle,CommonBody+",iframe[id^='ueditor']","reg.163.com","dl.reg.163.com","passport.163.com","open.weixin.qq.com")
    };

    sealed class CreatorSession {
        public readonly CreatorSite Site;
        public readonly WebView2 Web = new WebView2 { Dock=DockStyle.Fill, Visible=false };
        public string State="waiting", Notice;
        public string Declaration;
        public bool Checking, Navigating;
        public DateTime CheckUntil;
        public ulong NavigationId;
        public CreatorSession(CreatorSite site) { Site=site; }
    }
    readonly Dictionary<string,CreatorSession> creatorSessions = new Dictionary<string,CreatorSession>(StringComparer.Ordinal);
    readonly HashSet<string> rememberedCreators = new HashSet<string>(StringComparer.Ordinal);
    readonly HashSet<string> creatorSignInRequired = new HashSet<string>(StringComparer.Ordinal);
    readonly Dictionary<string,long> creatorConnectionProofs = new Dictionary<string,long>(StringComparer.Ordinal);
    readonly string rememberedCreatorsFile=Path.Combine(dataFolder,"creator-accounts.txt");
    CreatorSession creatorVisible;
    readonly Button openCreatorEditor = new Button { Dock=DockStyle.Right, Width=142, Text="文章编辑器", Visible=false, FlatStyle=FlatStyle.Flat };
    readonly Button loadCreatorWord = new Button { Dock=DockStyle.Right, Width=164, Text="导入并验证草稿", Visible=false, FlatStyle=FlatStyle.Flat };
    readonly System.Windows.Forms.Timer creatorTimer = new System.Windows.Forms.Timer { Interval=1600 };

    void WireCreatorToolbar(Panel top) {
        LoadCreatorProfiles();
        try {
            if(File.Exists(rememberedCreatorsFile)) {
                foreach(string record in File.ReadLines(rememberedCreatorsFile)) {
                    const string signinPrefix="signin:";
                    bool expired=record.StartsWith(signinPrefix,StringComparison.Ordinal);
                    string id=expired?record.Substring(signinPrefix.Length):record;
                    if(!CreatorSites.Any(site=>site.Id==id))continue;
                    if(expired) {
                        if(rememberedCreators.Contains(id))creatorSignInRequired.Add(id);
                    } else {
                        rememberedCreators.Add(id);
                        creatorSignInRequired.Remove(id);
                    }
                }
            }
        } catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        foreach(var button in new[]{loadCreatorWord,openCreatorEditor}) {
            button.FlatAppearance.BorderSize=0;
            top.Controls.Add(button);
        }
        openCreatorEditor.Click += async delegate {
            var session=creatorVisible;
            if(session==null)return;
            if(session.State=="error") { await OpenCreator(session.Site); return; }
            if(session.Web.CoreWebView2!=null && session.Site.Editor!=null)await OpenCreatorEditor(session);
        };
        loadCreatorWord.Click += async delegate { if(creatorVisible!=null) await LoadCreatorWord(creatorVisible); };
        creatorTimer.Tick += async delegate {
            var session=creatorVisible;
            if(session!=null && !busy)await CheckCreator(session);
        };
        FormClosed += delegate { creatorTimer.Dispose(); };
    }

    async Task OpenCreatorEditor(CreatorSession session) {
        if(session.Site.Id=="weibo" && session.State=="connected" && session.Site.IsEditor(session.Web.Source)) {
            if(await session.Web.ExecuteScriptAsync(WeiboNewArticle+"(true)")=="true")return;
        }
        session.Web.CoreWebView2.Navigate(session.Site.Editor);
    }

    IEnumerable<WorkbenchAccount> CreatorAccounts() {
        foreach(var site in CreatorSites) {
            CreatorSession session;
            bool started=creatorSessions.TryGetValue(site.Id,out session);
            bool remembered=rememberedCreators.Contains(site.Id);
            bool canPublish=CreatorCanPublish(site);
            CreatorMetrics metrics;
            creatorMetrics.TryGetValue(site.Id,out metrics);
            string state;
            if(creatorSignInRequired.Contains(site.Id))state="signin";
            else if(started && (session.State=="editor" || session.State=="connected"))state=session.State;
            else if(verifiedCreatorProfiles.Contains(site.Id) || (metrics!=null && metrics.state=="ready"))state="connected";
            else if(readingCreatorProfiles.Contains(site.Id))state="checking";
            else if(started && session.State=="error")state="error";
            else state=remembered?"saved":"idle";
            yield return new WorkbenchAccount {
                id=site.Id, platform=site.Label, mark=site.Mark, state=state,name=CreatorProfileName(site.Id),
                connected=remembered || (started && (session.State=="editor" || session.State=="connected")),
                publishBlocker=site.Id=="sohu" && !canPublish && (state=="connected" || state=="editor")?
                    checkingSohuDraftAccess?"checking":sohuDraftAccess==SohuDraftAccess.IdentityRequired?"identity-required":"unavailable":null,
                publishable=CreatorPublisher(site.Id)!=null, canSubmit=canPublish, analytics=CreatorMetricsReaders(site.Id)!=null
            };
        }
    }

    async Task OpenCreator(CreatorSite site,string draftAddress=null) {
        if(browserEnvironment==null)return;
        CreatorSession session;
        bool initialize=!creatorSessions.TryGetValue(site.Id,out session);
        if(initialize) {
            session=new CreatorSession(site);
            creatorSessions.Add(site.Id,session);
            platform.Controls.Add(session.Web);
        }
        HideCreator();
        creatorVisible=session;
        csdnVisible=false;
        csdn.Visible=false;
        page.Visible=false;
        dashboard.Visible=false;
        platform.BringToFront();
        session.Web.Visible=true;
        session.Web.BringToFront();
        creatorTimer.Start();
        Render();
        if(!initialize && session.Web.CoreWebView2!=null) {
            if(draftAddress!=null || session.State=="error" ||
                (creatorSignInRequired.Contains(site.Id) && !IsCreatorLoginPage(site,session.Web.Source)))
                session.Web.CoreWebView2.Navigate(draftAddress ?? site.Home);
            else await CheckCreator(session);
            return;
        }
        try {
            await InitCreatorView(session.Web,site,()=>{
                session.Navigating=false;
                session.Notice="登录跳转未完成，请重新打开官方页面。";
                UpdateCreatorState(session,"error");
            });
            if(IsDisposed)return;
            session.Web.CoreWebView2.NavigationStarting+=(s,e)=>{
                if(e.Cancel)return;
                session.NavigationId=e.NavigationId;
                session.Navigating=true;
                session.State="waiting";
                if(!busy)session.Notice=null;
                session.CheckUntil=DateTime.UtcNow.AddSeconds(8);
                if(creatorVisible==session)RenderCreatorToolbar();
            };
            session.Web.CoreWebView2.DOMContentLoaded+=async(s,e)=>{
                if(session.NavigationId!=e.NavigationId)return;
                session.Navigating=false;
                await CheckCreator(session);
            };
            session.Web.CoreWebView2.NavigationCompleted+=async (s,e)=>{
                if(session.NavigationId!=e.NavigationId)return;
                session.Navigating=false;
                if(!e.IsSuccess && e.WebErrorStatus!=CoreWebView2WebErrorStatus.OperationCanceled &&
                    session.State!="editor" && session.State!="connected" && session.State!="signin")
                    UpdateCreatorState(session,"error");
                else await CheckCreator(session);
            };
            session.Web.CoreWebView2.Navigate(draftAddress ?? site.Home);
        } catch(InvalidOperationException) { UpdateCreatorState(session,"error"); }
        catch(System.Runtime.InteropServices.COMException) { UpdateCreatorState(session,"error"); }
    }

    async Task InitCreatorView(WebView2 view,CreatorSite site,Action navigationBlocked=null,bool allowPopups=true) {
        var options=browserEnvironment.CreateCoreWebView2ControllerOptions();
        options.ProfileName=site.Profile;
        await view.EnsureCoreWebView2Async(browserEnvironment,options);
        var core=view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed=false;
        core.Settings.IsWebMessageEnabled=false;
        core.NavigationStarting+=(s,e)=>{
            if(site.Allows(e.Uri))return;
            e.Cancel=true;
            string secure=site.SecureLegacyNavigation(e.Uri);
            if(e.IsRedirected && secure!=null) {
                BeginInvoke(new Action(()=>{if(!view.IsDisposed)core.Navigate(secure);}));
            } else if(navigationBlocked!=null)navigationBlocked();
        };
        if(allowPopups)core.NewWindowRequested+=(s,e)=>OpenCreatorPopup(site,e);
        else core.NewWindowRequested+=(s,e)=>e.Handled=true;
    }

    async void OpenCreatorPopup(CreatorSite site,CoreWebView2NewWindowRequestedEventArgs request) {
        var deferral=request.GetDeferral();
        request.Handled=true;
        Form popup=null;
        try {
            if(!request.IsUserInitiated || !site.Allows(request.Uri) || IsDisposed || creatorVisible==null ||
                creatorVisible.Site!=site)return;
            popup=new Form { Text=site.Label+" · 官方页面", StartPosition=FormStartPosition.CenterParent,
                ClientSize=new Size(540,650), ShowInTaskbar=false };
            var view=new WebView2 { Dock=DockStyle.Fill };
            popup.Controls.Add(view);
            await InitCreatorView(view,site);
            if(IsDisposed || creatorVisible==null || creatorVisible.Site!=site){popup.Dispose();return;}
            request.NewWindow=view.CoreWebView2;
            view.CoreWebView2.WindowCloseRequested+=delegate { popup.Close(); };
            popup.Show(this);
        } catch(InvalidOperationException) { if(popup!=null)popup.Dispose(); }
        catch(System.Runtime.InteropServices.COMException) { if(popup!=null)popup.Dispose(); }
        finally { deferral.Complete(); }
    }

    void HideCreator() {
        if(creatorVisible==null)return;
        creatorVisible.Web.Visible=false;
        creatorVisible=null;
        creatorTimer.Stop();
        foreach(var owned in OwnedForms)owned.Close();
    }

    void RenderCreatorToolbar() {
        var session=creatorVisible;
        openCreatorEditor.Visible=session!=null && (session.State=="error" ||
            (session.Site.Editor!=null && session.State!="editor" && session.State!="signin" && !session.Navigating));
        openCreatorEditor.Text=session!=null && session.State=="error"?"重新打开":"文章编辑器";
        loadCreatorWord.Visible=session!=null && session.State=="editor" && queuedArticlePath!=null;
        openCreatorEditor.Enabled=!busy;
        loadCreatorWord.Enabled=!busy;
        if(session==null)return;
        platformCaption.Text=session.Site.Label+" · "+(session.Notice ?? (session.State=="editor"?"文章编辑器已就绪":
            session.State=="connected"?"已登录":
            session.State=="signin"?"请在官方页面登录":session.State=="error"?"官方页面加载失败":
            session.Navigating || session.Web.Source==null?"正在打开官方页面…":"请进入新建图文或文章页面"));
    }

    void UpdateCreatorState(CreatorSession session,string state) {
        if(IsDisposed || session.State==state)return;
        if(session.State=="error" && (state=="editor" || state=="connected" || state=="signin"))session.Notice=null;
        session.State=state;
        if(state=="signin")MarkCreatorSignInRequired(session.Site);
        if(state=="editor" || state=="connected")RememberCreator(session.Site.Id);
        Render();
    }

    void RememberCreator(string platformId) {
        creatorConnectionProofs[platformId]=CreatorConnectionProof(platformId)+1;
        bool refreshed=creatorSignInRequired.Remove(platformId);
        if(refreshed) {
            CreatorMetrics existing;
            if(creatorMetrics.TryGetValue(platformId,out existing) && existing.state=="signin")creatorMetrics.Remove(platformId);
        }
        if(rememberedCreators.Add(platformId) || refreshed)SaveCreatorAccountEvidence(platformId);
    }

    long CreatorConnectionProof(string platformId) {
        long proof;
        return creatorConnectionProofs.TryGetValue(platformId,out proof)?proof:0;
    }

    void MarkCreatorSignInRequired(CreatorSite site) {
        if(creatorSignInRequired.Add(site.Id)) {
            creatorConnectionProofs[site.Id]=CreatorConnectionProof(site.Id)+1;
            if(rememberedCreators.Contains(site.Id))SaveCreatorAccountEvidence("signin:"+site.Id);
        }
        verifiedCreatorProfiles.Remove(site.Id);
        if(site.Id=="sohu")sohuDraftAccess=SohuDraftAccess.Unknown;
        nextCreatorProfileRead.Remove(site.Id);
        creatorMetrics[site.Id]=new CreatorMetrics {state="signin"};
        analyticsLastAttempt.Remove(site.Id);
        analyticsCollectedAt.Remove(site.Id);
    }

    void SaveCreatorAccountEvidence(string value) {
        try {
            Directory.CreateDirectory(dataFolder);
            File.AppendAllText(rememberedCreatorsFile,value+Environment.NewLine);
        } catch(IOException) { }
        catch(UnauthorizedAccessException) { }
    }

    static bool IsCreatorLoginPage(CreatorSite site,Uri address) {
        return address!=null && site.Allows(address.AbsoluteUri) && address.Scheme=="https" &&
            Regex.IsMatch(address.AbsolutePath,site.LoginPath,RegexOptions.IgnoreCase);
    }

    async Task CheckCreator(CreatorSession session) {
        Uri address=session.Web.Source;
        if(IsDisposed || session.Checking || session.Web.CoreWebView2==null ||
            address==null || !session.Site.Allows(address.AbsoluteUri) || address.Scheme!="https")return;
        if(IsCreatorLoginPage(session.Site,address)) {
            UpdateCreatorState(session,"signin");
            return;
        }
        if(session.Navigating)return;
        bool editor=session.Site.IsEditor(address);
        string homeProbe=CreatorHomeStatusScript(session.Site,address);
        if(!editor && homeProbe==null) {
            if(session.State!="error")UpdateCreatorState(session,"waiting");
            return;
        }
        session.Checking=true;
        try {
            string detected;
            if(editor) {
                var fields=json.Deserialize<CreatorSnapshot>(await CreatorAction(session.Site,session.Web,"probe"));
                detected=fields!=null && fields.state=="editor"?"editor":null;
                if(detected==null && homeProbe!=null && await session.Web.ExecuteScriptAsync(homeProbe)=="true")
                    detected="connected";
            } else {
                detected=await session.Web.ExecuteScriptAsync(homeProbe)=="true"?"connected":null;
            }
            if(session.Web.Source==null || session.Web.Source.AbsoluteUri!=address.AbsoluteUri || session.Navigating)return;
            if(detected!=null) {
                bool resuming=creatorVisible==session && creatorSignInRequired.Contains(session.Site.Id);
                UpdateCreatorState(session,detected);
                if(session.Site.Id=="sohu" && detected=="editor")await ReadSohuDraftAccess(session.Site,session.Web);
                if(resuming && !creatorSignInRequired.Contains(session.Site.Id))
                    await VerifySubmittedCreatorRecords(PendingCreatorRecords(session.Site.Label));
                if(creatorVisible==session)await RefreshCreatorProfile(session.Site,resuming);
            }
            else if(DateTime.UtcNow>session.CheckUntil && session.State!="error")UpdateCreatorState(session,"waiting");
        } catch(InvalidOperationException) { }
        catch(System.Runtime.InteropServices.COMException) { }
        finally { session.Checking=false; }
    }


    static string CreatorHomeStatusScript(CreatorSite site,Uri address) {
        string path=address.AbsolutePath.TrimEnd('/');
        if(site.Id=="baijiahao" && address.Host=="baijiahao.baidu.com" && path=="/builder/rc/home")return BaijiahaoHomeStatus;
        if(site.Id=="toutiao" && address.Host=="mp.toutiao.com" && (path=="/profile_v4/index" || path=="/profile_v4"))
            return ToutiaoHomeStatus;
        if(site.Id=="sohu" && address.Host=="mp.sohu.com" && path=="/mpfe/v4/contentManagement/first/page")
            return SohuHomeStatus;
        if(site.Id=="weibo" && site.IsEditor(address))return WeiboNewArticle+"(false)";
        if(site.Id=="qiehao" && address.Host=="om.qq.com" && path=="/main")
            return CreatorProfileScript+"('qiehao')!==null";
        if(site.Id=="wangyi" && address.Host=="mp.163.com" && path=="/subscribe_v4/index.html")
            return CreatorProfileScript+"('wangyi')!==null";
        return null;
    }

    const string SohuHomeStatus=@"(() => {
        const labels=new Set([...document.querySelectorAll('a,button,li,span')]
            .filter(e=>e.getClientRects().length && !e.children.length)
            .map(e=>e.innerText.trim()));
        return labels.has('发布内容') && labels.has('个人中心') && labels.has('草稿');
    })()";

    const string BaijiahaoHomeStatus=@"(() => {
        const body=document.body?.innerText || '';
        return location.origin==='https://baijiahao.baidu.com' &&
            location.pathname.replace(/\/$/,'')==='/builder/rc/home' &&
            ['发布作品','内容管理','个人中心'].every(label=>body.split('\n').some(line=>line.trim()===label));
    })()";
    const string ToutiaoHomeStatus=@"(() => {
        const workspace=[...document.querySelectorAll('a[href]')].some(a=>a.getClientRects().length &&
            a.textContent.trim()==='作品管理' && a.origin===location.origin && a.pathname==='/profile_v4/manage/content/all');
        const path=location.pathname.replace(/\/$/,'');
        return location.origin==='https://mp.toutiao.com' &&
            (path==='/profile_v4/index' || path==='/profile_v4') && workspace &&
            /创作权益|创作灵感|创作训练营/.test(document.body?.innerText || '');
    })()";
    const string WeiboNewArticle=@"((open,allowExistingDraft=false) => {
        if(location.origin!=='https://card.weibo.com' || location.pathname.replace(/\/$/,'')!=='/article/v5/editor' ||
            (location.hash && location.hash!=='#/draft' &&
              !(allowExistingDraft && /^#\/draft\/[0-9]{6,32}$/.test(location.hash))))return false;
        const buttons=[...document.querySelectorAll('button,.n-button[role=button]')].filter(e=>
            e.getClientRects().length && !e.disabled && e.getAttribute('aria-disabled')!=='true' && e.innerText.trim()==='写文章');
        if(buttons.length!==1)return false;
        if(open)buttons[0].click();
        return true;
    })";
}
