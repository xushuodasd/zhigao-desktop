using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class CreatorSnapshot {
        public string state { get; set; }
        public string title { get; set; }
        public string text { get; set; }
        public CreatorSnapshotTable[] tables { get; set; }
        public CreatorSnapshotImage[] images { get; set; }
        public CreatorList[] lists { get; set; }
    }
    sealed class CreatorSnapshotTable {
        public int offset { get; set; }
        public string[][] cells { get; set; }
        public bool merged { get; set; }
    }
    sealed class CreatorSnapshotImage {
        public int offset { get; set; }
        public bool durable { get; set; }
        public bool decoded { get; set; }
        public string url { get; set; }
    }
    sealed class BaijiahaoDraftReceipt {
        public int? errno { get; set; }
        public string errmsg { get; set; }
        public BaijiahaoDraftArticle ret { get; set; }
    }
    sealed class BaijiahaoDraftArticle {
        public string article_id { get; set; }
    }
    sealed class WeiboDraftReceipt {
        public string code { get; set; }
    }
    sealed class SohuDraftReceipt {
        public bool success { get; set; }
        public string data { get; set; }
    }
    sealed class QiehaoDraftReceipt {
        public QiehaoDraftStatus response { get; set; }
        public QiehaoDraftArticle data { get; set; }
    }
    sealed class QiehaoDraftStatus {
        public string code { get; set; }
    }
    sealed class QiehaoDraftArticle {
        public string articleId { get; set; }
    }

    static readonly Lazy<string> creatorScript=new Lazy<string>(()=> {
        using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("creator-editor.js"))
        using(var reader=new StreamReader(stream))return reader.ReadToEnd();
    });
    const string ToutiaoDraftListUrl="https://mp.toutiao.com/profile_v4/manage/draft";
    const string WangyiDraftListUrl="https://mp.163.com/subscribe_v4/index.html#/draft-manage";
    const string SohuDraftVerificationError="搜狐号显示未完成实名认证，当前「存草稿」已禁用。";

    Task<string> CreatorAction(CreatorSite site,WebView2 view,string action,object data=null) {
        var address=view.Source;
        if(!site.IsEditor(address))return Task.FromResult("null");
        var config=new {
            address=address.GetLeftPart(UriPartial.Authority)+address.PathAndQuery+address.Fragment,
            titleFields=site.TitleFields,bodyFields=site.BodyFields
        };
        return view.ExecuteScriptAsync("("+creatorScript.Value+")("+json.Serialize(action)+","+json.Serialize(config)+","+json.Serialize(data)+")");
    }

    async Task<CreatorSnapshot> ReadCreatorSnapshot(CreatorSite site,WebView2 view) {
        string result=await CreatorAction(site,view,"inspect");
        return json.Deserialize<CreatorSnapshot>(result);
    }

    static string CreatorMismatch(CreatorWord word,CreatorSnapshot actual) {
        if(actual==null || actual.state!="editor")return "未找到唯一可核对的文章编辑器";
        if(actual.title!=word.Title)return "标题与 Word 文件名不一致";
        if(actual.tables.Length!=word.Tables.Count)return "表格数量与 Word 不一致";
        for(int n=0;n<word.Tables.Count;n++) {
            var before=word.Tables[n];
            var after=actual.tables[n];
            string prefix="第 "+(n+1)+" 个表格";
            if(after.offset!=before.offset)return prefix+"位置与 Word 不一致";
            if(after.merged || after.cells.Length!=before.cells.Length)return prefix+"行数或合并结构有变";
            for(int row=0;row<before.cells.Length;row++)
                if(!before.cells[row].SequenceEqual(after.cells[row]))return prefix+"第 "+(row+1)+" 行的内容或列数有变";
        }
        if(actual.lists.Length!=word.Lists.Count || word.Lists.Where((item,i)=>
            item.offset!=actual.lists[i].offset || item.tag!=actual.lists[i].tag ||
            item.count!=actual.lists[i].count || item.start!=actual.lists[i].start).Any())
            return "列表位置或编号与 Word 不一致";
        if(actual.images.Length!=word.Images.Count)return "图片数量与 Word 不一致";
        for(int i=0;i<word.Images.Count;i++) {
            var image=actual.images[i];
            if(image.offset!=word.Images[i].Offset)return "第 "+(i+1)+" 张图片位置与 Word 不一致";
            if(!image.durable || !image.decoded)return "第 "+(i+1)+" 张图片尚未取得可重开的平台链接";
            if(word.Images[i].SavedUrl!=null && image.url!=word.Images[i].SavedUrl)
                return "第 "+(i+1)+" 张表格图片与已上传图片不一致";
        }
        if(actual.text!=word.Text) {
            int first=0;
            int length=Math.Min(actual.text.Length,word.Text.Length);
            while(first<length && actual.text[first]==word.Text[first])first++;
            return "正文与 Word 不一致（"+actual.text.Length+"/"+word.Text.Length+" 字，第 "+(first+1)+" 字起有差异）";
        }
        return null;
    }

    async Task LoadCreatorWord(CreatorSession session) {
        string file=queuedArticlePath;
        if(busy || verifying || session.Navigating || !session.Site.IsEditor(session.Web.Source))return;
        if(file==null || !File.Exists(file)) {
            SetCreatorNotice(session,"请先在工作台选择 Word。");
            return;
        }
        busy=true;
        check.Enabled=false;
        session.Notice="正在核对 Word…";
        Render();
        bool saveRequested=false;
        try {
            if(session.Site.Id=="sohu" &&
                await CreatorAction(session.Site,session.Web,"draft-readiness")=="\"sohu-verification-required\"")
                throw new InvalidOperationException(SohuDraftVerificationError);
            using(var source=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                var word=await Task.Run(()=>new CreatorWord(file,source,session.Site.Id=="wangyi"));
                var draft=await CreateVerifiedCreatorDraft(session,word,source,()=>saveRequested=true);
                RecordCreatorDraft(session,file,draft,word.Images.Any(image=>image.Data!=null));
            }
        } catch(Exception ex) {
            if(saveRequested)Record(Path.GetFileName(file)+"    "+session.Site.Label+" · 草稿待核实 · "+ex.Message.Replace('\r',' ').Replace('\n',' '));
            SetCreatorNotice(session,(saveRequested?"草稿状态待核实 · ":"草稿验证暂停 · ")+ex.Message);
        } finally {
            busy=false;
            check.Enabled=true;
            Render();
        }
    }

    async Task<Uri> CreateVerifiedCreatorDraft(CreatorSession session,CreatorWord word,Stream source,Action draftStarted,string declaration=null) {
        var site=session.Site;
        if(word.Html.Length>3000000)throw new InvalidDataException("文档排版过多，无法可靠送入网页。");
        if(site.Id=="weibo") {
            var existing=await FindVerifiedWeiboDraft(site,word);
            if(existing!=null)return existing;
            await PrepareWeiboDraft(session);
        }
        if(site.Id=="toutiao") {
            var existing=await FindToutiaoDraft(site,word.Title);
            if(existing!=null) {
                await ReadCreatorDraftBack(site,existing,word);
                return existing;
            }
        }
        if(declaration!=null && (site.Id=="qiehao" || site.Id=="sohu")) {
            var blank=await ReadCreatorSnapshot(site,session.Web);
            if(blank==null || blank.title!="" || blank.text!="" || blank.images.Length!=0 ||
                blank.tables.Length!=0 || blank.lists.Length!=0)
                throw new InvalidOperationException(site.Label+"已恢复另一篇草稿，原稿已保留。");
            await SetCreatorDeclaration(session,declaration);
        }
        if(site.Id=="qiehao")await RequireQiehaoDraftDeclaration(session);
        bool titlePrepared=site.Id=="baijiahao" || site.Id=="qiehao";
        if(titlePrepared)await EnterCreatorTitle(session,word.Title);
        var importData=new {
            title=word.Title, html=word.Html, titlePrepared
        };
        string imported=json.Deserialize<string>(await CreatorAction(site,session.Web,"import",importData));
        if(site.Id=="weibo" && imported=="occupied") {
            await PrepareWeiboDraft(session);
            imported=json.Deserialize<string>(await CreatorAction(site,session.Web,"import",importData));
        }
        if(imported=="occupied")throw new InvalidOperationException("当前稿件已有内容，请打开一篇空白的新文章。");
        if(imported=="title-length")throw new InvalidOperationException("Word 文件名超过平台标题长度上限。");
        if(site.Id=="wangyi" && (imported=="started" || imported=="body"))draftStarted();
        if(imported=="title")throw new InvalidOperationException("平台没有接收标题。");
        if(imported=="body")throw new InvalidOperationException("平台没有接收 Word 正文。");
        if(imported!="started")throw new InvalidOperationException("无法写入已定位的标题或正文。");
        if(word.Images.Count>0) {
            source.Position=0;
            using(var archive=new ZipArchive(source,ZipArchiveMode.Read,true))
                for(int i=0;i<word.Images.Count;i++)await InsertCreatorImage(session,archive,word.Images[i],i);
        }
        await WaitForCreatorContent(site,session.Web,word,7);
        SetCreatorNotice(session,"图文已核对，正在保存验证草稿…");
        string expected=await CreatorAction(site,session.Web,"inspect");
        string mismatch=CreatorMismatch(word,json.Deserialize<CreatorSnapshot>(expected));
        if(mismatch!=null)throw new InvalidOperationException(mismatch);
        Uri draft;
        if(site.Id=="toutiao") {
            draftStarted();
            await Wait(async()=>await CreatorAction(site,session.Web,"autosave-ready")=="true",
                20,"头条号尚未显示自动保存成功。");
            draft=await FindToutiaoDraft(site,word.Title);
            if(draft==null)throw new InvalidOperationException("暂未在头条号草稿箱找到同标题的稿件。");
        } else if(site.Id=="wangyi") {
            draft=await FindWangyiAutosavedDraft(session,word.Title);
        } else {
            draft=await SaveCreatorDraft(session,expected,draftStarted);
        }
        await ReadCreatorDraftBack(site,draft,word);
        return draft;
    }

    void RecordCreatorDraft(CreatorSession session,string file,Uri draft,bool tableImage=false) {
        string filename=Path.GetFileName(file);
        if(!records.Any(entry=>entry.platform==session.Site.Label && entry.link==draft.AbsoluteUri && entry.file==filename))
            Record(filename+"    "+session.Site.Label+" · 草稿 · "+draft.AbsoluteUri);
        SetCreatorNotice(session,tableImage?"草稿已保存并重开核对，网易号表格以原位图片保留。":
            "草稿已保存并重开核对，标题、正文和图表一致。");
    }

    async Task<Uri> FindToutiaoDraft(CreatorSite site,string headline) {
        using(var list=new WebView2 { Visible=false, Width=1100, Height=730 }) {
            platform.Controls.Add(list);
            await InitCreatorView(list,site);
            Uri received=null;
            list.CoreWebView2.NewWindowRequested+=(s,e)=>{
                Uri proposed;
                if(Uri.TryCreate(e.Uri,UriKind.Absolute,out proposed))
                    received=GetCreatorDraftAddress(site,proposed);
                e.Handled=true;
            };
            var ready=new TaskCompletionSource<bool>();
            list.CoreWebView2.DOMContentLoaded+=(s,e)=>ready.TrySetResult(true);
            list.CoreWebView2.Navigate(ToutiaoDraftListUrl);
            if(await Task.WhenAny(ready.Task,Task.Delay(11000))!=ready.Task)
                throw new InvalidOperationException("头条号草稿箱未加载。");
            int count=0;
            try {
                await Wait(async()=>{
                    string selected=await list.ExecuteScriptAsync(ToutiaoDraftLookup+"("+json.Serialize(headline)+")");
                    if(selected=="null")return false;
                    count=json.Deserialize<int>(selected);
                    return count>0;
                },6,"草稿箱暂无匹配标题。");
            } catch(TimeoutException) {
                if(list.Source==null || list.Source.AbsoluteUri!=ToutiaoDraftListUrl)
                    throw new InvalidOperationException("头条号草稿箱没有打开，请检查登录状态。");
                return null;
            }
            if(count>1)throw new InvalidOperationException("头条号有多个同标题草稿，请先确认要保留的稿件。");
            await Wait(()=>{
                if(received==null)received=GetCreatorDraftAddress(site,list.Source);
                return Task.FromResult(received!=null);
            },8,"头条号草稿箱未提供该稿件的编辑地址。");
            return received;
        }
    }

    const string ToutiaoDraftLookup=@"(headline => {
        if(location.origin!=='https://mp.toutiao.com'||location.pathname!=='/profile_v4/manage/draft')return null;
        const rows=[...document.querySelectorAll('.article-draft-item.draft-item')].filter(row=>
            row.getClientRects().length && [...row.querySelectorAll('main a,main span,main div,main p')]
                .some(title=>title.textContent.trim()===headline));
        if(rows.length!==1)return rows.length;
        const edit=[...rows[0].querySelectorAll('a,button,[role=button]')].filter(button=>
            button.getClientRects().length && button.innerText.trim()==='编辑');
        if(edit.length!==1)return null;
        edit[0].click();
        return 1;
    })";

    async Task<Uri> FindWangyiAutosavedDraft(CreatorSession session,string headline) {
        await WaitForWangyiAutosave(session);
        return await FindWangyiDraftInList(session.Site,headline,15);
    }

    async Task<Uri> FindWangyiDraftInList(CreatorSite site,string headline,int seconds) {
        using(var list=new WebView2 { Visible=false, Width=1100, Height=730 }) {
            platform.Controls.Add(list);
            await InitCreatorView(list,site);
            list.CoreWebView2.Navigate(WangyiDraftListUrl);
            Uri draft=null;
            await Wait(async()=>{
                draft=await ReadWangyiDraftList(site,list,headline);
                return draft!=null;
            },seconds,"网易号草稿管理页未提供该标题的草稿地址，请确认登录及草稿状态。");
            return draft;
        }
    }

    Task WaitForWangyiAutosave(CreatorSession session) {
        return Wait(async()=>await CreatorAction(session.Site,session.Web,"wangyi-autosave-ready")=="true",
            40,"网易号尚未显示自动保存成功。");
    }

    async Task<Uri> ReadWangyiDraftList(CreatorSite site,WebView2 list,string headline) {
        if(list.Source==null || list.Source.AbsoluteUri!=WangyiDraftListUrl)return null;
        var matches=json.Deserialize<string[]>(await list.ExecuteScriptAsync(WangyiDraftLookup+"("+json.Serialize(headline)+")"));
        if(matches==null || matches.Length==0)return null;
        if(matches.Length!=1)throw new InvalidOperationException("网易号有多个同标题草稿，请先确认要保留的稿件。");
        Uri candidate;
        return Uri.TryCreate(matches[0],UriKind.Absolute,out candidate)?GetCreatorDraftAddress(site,candidate):null;
    }

    const string WangyiDraftLookup=@"(headline => {
        if(location.origin!=='https://mp.163.com'||location.pathname!=='/subscribe_v4/index.html'||location.hash!=='#/draft-manage')return null;
        const container=document.querySelector('.src-main-page-ContentManage-index-module-content-list-container.src-main-page-ContentManage-index-module-draft');
        if(!container)return null;
        return [...container.querySelectorAll('.src-main-page-ContentManage-Card-index-module-card')].filter(card=>
            card.getClientRects().length && card.querySelector('.src-main-page-ContentManage-Card-index-module-title')?.textContent.trim()===headline
        ).map(card=>{
            const links=[...card.querySelectorAll('.src-main-page-ContentManage-Card-Operation-index-module-oper a[href]')].filter(link=>
                link.getClientRects().length && link.textContent.trim()==='修改');
            return links.length===1 ? links[0].href : null;
        });
    })";

    async Task EnterCreatorTitle(CreatorSession session,string heading) {
        var previous=await ReadCreatorSnapshot(session.Site,session.Web);
        var existingDraft=GetCreatorDraftAddress(session.Site,session.Web.Source);
        if(previous!=null && previous.title==heading && previous.text=="" && previous.tables.Length==0 &&
            previous.images.Length==0 && previous.lists.Length==0 && existingDraft==null)
            return;
        if(existingDraft!=null)throw new InvalidOperationException("请打开一篇空白的新文章，再导入 Word。");
        var accepted=await CreatorAction(session.Site,session.Web,"prepare-title",new {title=heading});
        if(accepted=="\"occupied\"")throw new InvalidOperationException("当前稿件已有内容，请打开一篇空白的新文章。");
        if(accepted=="\"title-length\"")throw new InvalidOperationException("Word 文件名超过平台标题长度上限。");
        if(accepted!="\"started\"")throw new InvalidOperationException(session.Site.Label+"没有接收标题。");
        await Wait(async()=>{
            var actual=await ReadCreatorSnapshot(session.Site,session.Web);
            return actual!=null && actual.title==heading;
        },5,session.Site.Label+"没有接收标题。");
        // The title counter updates before the platform's delayed title-change callback.
        if(session.Site.Id=="qiehao")await Task.Delay(1800);
    }

    async Task RequireQiehaoDraftDeclaration(CreatorSession session) {
        var declaration=await CreatorAction(session.Site,session.Web,"qiehao-draft-declaration");
        if(declaration=="\"selected\"" || (declaration=="\"missing\"" && session.Declaration=="none"))return;
        throw new InvalidOperationException(declaration=="\"missing\""?
            "请先在企鹅号编辑器的「自主声明」中选择并确认符合这篇文章的声明，再验证草稿。":
            "未能确认企鹅号的自主声明，请在官方编辑器中检查后再验证草稿。");
    }

    async Task<Uri> SaveCreatorDraft(CreatorSession session,string expected,Action submitted) {
        var site=session.Site;
        if(site.Id=="qiehao")await RequireQiehaoDraftDeclaration(session);
        var core=session.Web.CoreWebView2;
        var initial=GetCreatorDraftAddress(site,session.Web.Source);
        bool trackReceipt=site.Id=="baijiahao" || site.Id=="weibo" || site.Id=="qiehao" || site.Id=="sohu";
        Uri receiptAddress=null;
        bool weiboVerification=false;
        bool closed=false;
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> onResponse=async (sender,e)=>{
            Uri request;
            if(e.Request.Method!="POST" || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out request) ||
                !IsCreatorDraftReceiptRequest(site,request) || e.Response.StatusCode<200 || e.Response.StatusCode>=300)
                return;
            try {
                using(var content=await e.Response.GetContentAsync()) {
                    if(content==null || closed)return;
                    using(var reader=new StreamReader(content)) {
                        var chars=new char[16385];
                        int read=await Task.Run(()=>reader.ReadBlock(chars,0,chars.Length));
                        if(read>=chars.Length || closed)return;
                        string payload=new string(chars,0,read);
                        if(site.Id=="weibo" && initial!=null && IsWeiboNextSave(request,CreatorPublicationId(site,initial.AbsoluteUri)) &&
                            WeiboNextSaveState(payload)=="verification") {
                            weiboVerification=true;
                            return;
                        }
                        var receipt=ParseCreatorDraftReceipt(site,request,payload);
                        if(receipt!=null && receiptAddress==null)receiptAddress=receipt;
                    }
                }
            } catch(ArgumentException) { }
            catch(InvalidOperationException) { }
            catch(IOException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        if(trackReceipt)core.WebResourceResponseReceived+=onResponse;
        try {
            string saved=await CreatorAction(site,session.Web,"save-draft",new {expected});
            if(saved=="\"sohu-verification-required\"")
                throw new InvalidOperationException(SohuDraftVerificationError);
            if(saved!="true")throw new InvalidOperationException("图文已载入，但页面没有唯一可用的「保存草稿」按钮。");
            submitted();
            Uri draft=null;
            await Wait(()=>{
                if(weiboVerification)throw new WeiboVerificationRequired();
                var address=GetCreatorDraftAddress(site,session.Web.Source);
                if((site.Id=="weibo" || site.Id=="qiehao") && address!=null && initial!=null && address.AbsoluteUri==initial.AbsoluteUri)
                    address=null;
                draft=receiptAddress ?? address;
                return Task.FromResult(draft!=null && !session.Navigating);
            },8,"平台未提供可定位的草稿地址。");
            return draft;
        } finally {
            closed=true;
            if(trackReceipt)core.WebResourceResponseReceived-=onResponse;
        }
    }

    static bool IsCreatorDraftReceiptRequest(CreatorSite site,Uri request) {
        return site.Allows(request.AbsoluteUri) &&
            (site.Id=="baijiahao" && request.Host=="baijiahao.baidu.com" && request.AbsolutePath=="/pcui/article/save" ||
             site.Id=="weibo" && request.Host=="card.weibo.com" && request.AbsolutePath=="/article/v5/aj/editor/draft/save" ||
             site.Id=="qiehao" && request.Host=="om.qq.com" && request.AbsolutePath=="/marticlepublish/omSave" ||
             site.Id=="sohu" && request.Host=="mp.sohu.com" && request.AbsolutePath=="/mpbp/bp/news/v4/news/draft/v2");
    }

    Uri ParseCreatorDraftReceipt(CreatorSite site,Uri request,string payload) {
        switch(site.Id) {
            case "baijiahao": return ParseBaijiahaoDraftReceipt(payload);
            case "weibo": return ParseWeiboDraftReceipt(request,payload);
            case "qiehao": return ParseQiehaoDraftReceipt(payload);
            case "sohu": return ParseSohuDraftReceipt(payload);
            default: return null;
        }
    }

    Uri ParseQiehaoDraftReceipt(string payload) {
        QiehaoDraftReceipt result;
        try { result=json.Deserialize<QiehaoDraftReceipt>(payload); }
        catch(ArgumentException) { return null; }
        catch(InvalidOperationException) { return null; }
        if(result==null || result.response==null || result.response.code!="0" || result.data==null ||
            !Regex.IsMatch(result.data.articleId??"", @"\A[a-zA-Z0-9_-]{6,80}\z"))return null;
        return new Uri("https://om.qq.com/main/creation/article?articleId="+result.data.articleId);
    }

    Uri ParseSohuDraftReceipt(string payload) {
        SohuDraftReceipt result;
        try { result=json.Deserialize<SohuDraftReceipt>(payload); }
        catch(ArgumentException) { return null; }
        catch(InvalidOperationException) { return null; }
        if(result==null || !result.success || !Regex.IsMatch(result.data??"", @"\A[0-9]{6,32}\z"))return null;
        return new Uri("https://mp.sohu.com/mpfe/v4/contentManagement/news/addarticle?contentStatus=2&id="+result.data);
    }

    Uri ParseWeiboDraftReceipt(Uri request,string payload) {
        var key=Regex.Match(request.Query,@"(?:^\?|&)id=([0-9]{6,32})(?:&|$)");
        if(!key.Success)return null;
        WeiboDraftReceipt result;
        try { result=json.Deserialize<WeiboDraftReceipt>(payload); }
        catch(ArgumentException) { return null; }
        catch(InvalidOperationException) { return null; }
        if(result==null || result.code!="100000")return null;
        return new Uri("https://card.weibo.com/article/v5/editor#/draft/"+key.Groups[1].Value);
    }

    Uri ParseBaijiahaoDraftReceipt(string payload) {
        payload=payload.Trim();
        if(payload.StartsWith("bjhdraft(",StringComparison.Ordinal)) {
            payload=payload.TrimEnd(';').TrimEnd();
            if(!payload.EndsWith(")",StringComparison.Ordinal))return null;
            payload=payload.Substring(9,payload.Length-10);
        }
        BaijiahaoDraftReceipt result;
        try { result=json.Deserialize<BaijiahaoDraftReceipt>(payload); }
        catch(ArgumentException) { return null; }
        catch(InvalidOperationException) { return null; }
        if(result==null || result.errmsg!="success" || (result.errno.HasValue && result.errno.Value!=0) ||
            result.ret==null || !Regex.IsMatch(result.ret.article_id??"", @"\A[0-9]{6,32}\z"))return null;
        return new Uri("https://baijiahao.baidu.com/builder/rc/edit?type=news&article_id="+result.ret.article_id);
    }

    void SetCreatorNotice(CreatorSession session,string notice) {
        if(IsDisposed)return;
        session.Notice=notice;
        ReportCreatorProgress(session.Site,notice);
    }

    async Task InsertCreatorImage(CreatorSession session,ZipArchive archive,CreatorImage image,int index) {
        string encoded;
        if(image.Data!=null)encoded=Convert.ToBase64String(image.Data);
        else {
            var entry=archive.GetEntry(image.Part);
            using(var input=entry.Open())
            using(var output=new MemoryStream((int)entry.Length)) {
                await input.CopyToAsync(output);
                if(output.Length!=entry.Length)throw new InvalidDataException("Word 图片无法完整读取。");
                encoded=Convert.ToBase64String(output.GetBuffer(),0,(int)output.Length);
            }
        }
        SetCreatorNotice(session,image.Data!=null?"正在按原位上传网易号表格图片…":"正在导入第 "+(index+1)+" 张图片…");
        var data=new { token=image.Token, name=image.Name, mime=image.Mime, base64=encoded };
        if(await CreatorAction(session.Site,session.Web,"image",data)!="true")
            throw new InvalidOperationException("无法定位第 "+(index+1)+" 张图片在正文中的位置。");
        CreatorSnapshot snapshot=null;
        await Wait(async()=>{
            snapshot=await ReadCreatorSnapshot(session.Site,session.Web);
            return snapshot!=null && snapshot.state=="editor" && snapshot.images.Length==index+1 &&
                snapshot.images[index].durable && snapshot.images[index].decoded;
        },16,"第 "+(index+1)+" 张图片未得到平台上传回执。");
        if(image.Data!=null)image.SavedUrl=snapshot.images[index].url;
        if(snapshot.text.Contains(image.Token))
            await CreatorAction(session.Site,session.Web,"remove-token",new { token=image.Token });
    }

    async Task WaitForCreatorContent(CreatorSite site,WebView2 view,CreatorWord word,int seconds) {
        string difference=null;
        try {
            await Wait(async()=>{
                difference=CreatorMismatch(word,await ReadCreatorSnapshot(site,view));
                return difference==null;
            },seconds,"编辑器未完成核对。");
        } catch(TimeoutException) {
            throw new InvalidOperationException(difference ?? "未取得完整的编辑器正文。");
        }
    }

    static Uri GetCreatorDraftAddress(CreatorSite site,Uri address) {
        if(!site.IsEditor(address))return null;
        if(site.Id=="wangyi")
            return Regex.IsMatch(address.Fragment,@"^#/article-publish/[a-zA-Z0-9_-]{6,80}\?(?:[^&]+&)*option=editDraft(?:&|$)")?address:null;
        if(Regex.IsMatch(address.Query,@"(?:^\?|&)(?:draftId|articleId|article_id|group_id|contentId|id)=[a-zA-Z0-9_-]{6,80}(?:&|$)") ||
            (site.Id=="toutiao" && Regex.IsMatch(address.Query,@"(?:^\?|&)pgc_id=[0-9]{6,32}(?:&|$)")))
            return address;
        return Regex.IsMatch(address.Fragment,@"^#/draft/[a-zA-Z0-9_-]{6,80}$|[?&](?:draftId|articleId|article_id|id)=[a-zA-Z0-9_-]{6,80}(?:&|$)")?address:null;
    }

    async Task ReadCreatorDraftBack(CreatorSite site,Uri address,CreatorWord word,bool readOnly=false) {
        using(var view=new WebView2 { Visible=false }) {
            platform.Controls.Add(view);
            await InitCreatorView(view,site,allowPopups:!readOnly);
            if(readOnly) {
                var core=view.CoreWebView2;
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(sender,e)=>{
                    if(e.Request.Method!="GET" && e.Request.Method!="HEAD" && e.Request.Method!="OPTIONS")
                        e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(),403,"Read-only verification","");
                };
            }
            var navigation=new TaskCompletionSource<bool>();
            view.CoreWebView2.NavigationCompleted+=(s,e)=>navigation.TrySetResult(e.IsSuccess && e.HttpStatusCode>=200 && e.HttpStatusCode<300);
            await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable","{}");
            await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled","{\"cacheDisabled\":true}");
            view.CoreWebView2.Navigate(address.AbsoluteUri);
            if(await Task.WhenAny(navigation.Task,Task.Delay(11000))!=navigation.Task || !await navigation.Task ||
                view.Source==null || view.Source.AbsoluteUri!=address.AbsoluteUri)
                throw new InvalidOperationException("未能重开同一个平台草稿。");
            await WaitForCreatorContent(site,view,word,9);
        }
    }
}
