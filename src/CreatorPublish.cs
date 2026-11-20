using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

partial class LoginWindow {
    sealed class CreatorSubmission {
        public string state { get; set; }
        public string message { get; set; }
        public string url { get; set; }
    }

    sealed class CreatorPublishJournal {
        public string draft { get; set; }
        public bool draftStarted { get; set; }
        public CreatorSubmission outcome { get; set; }
    }

    partial void ConfigureCreatorPublishView(CreatorSession session);

    Func<CreatorSession,CreatorWord,Uri,Action,Task<CreatorSubmission>> CreatorPublisher(string platformId) {
        switch(platformId) {
            case "baijiahao": return SubmitBaijiahaoCreatorDraft;
            case "toutiao": return SubmitToutiaoCreatorDraft;
            case "weibo": return SubmitWeiboCreatorDraft;
            case "qiehao": return SubmitQiehaoCreatorDraft;
            case "sohu": return SubmitSohuCreatorDraft;
            case "wangyi": return SubmitWangyiCreatorDraft;
            default: return null;
        }
    }

    bool CreatorCanPublish(CreatorSite site) {
        if(CreatorPublisher(site.Id)==null || creatorSignInRequired.Contains(site.Id))return false;
        CreatorSession session;
        if(site.Id=="sohu")return sohuDraftAccess==SohuDraftAccess.Allowed;
        if(creatorSessions.TryGetValue(site.Id,out session)) {
            if(session.State=="signin")return false;
            if(session.State=="editor" || session.State=="connected")return true;
        }
        CreatorMetrics metrics;
        if(creatorMetrics.TryGetValue(site.Id,out metrics)) {
            if(metrics.state=="signin")return false;
            if(metrics.state=="ready")return true;
        }
        return rememberedCreators.Contains(site.Id);
    }

    async Task PublishCreator(CreatorSite site,string file,string declaration=null) {
        var submit=CreatorPublisher(site.Id);
        if(submit==null)return;
        bool sent=false;
        var journal=new CreatorPublishJournal();
        string verifiedDraft=null;
        string result;
        try {
            ReportCreatorProgress(site,"正在读取 Word");
            using(var source=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                string fingerprint;
                using(var sha=SHA256.Create())fingerprint=BitConverter.ToString(sha.ComputeHash(source)).Replace("-","");
                source.Position=0;
                var folder=Path.Combine(dataFolder,"CreatorAttempts",site.Id);
                Directory.CreateDirectory(folder);
                string journalPath=Path.Combine(folder,fingerprint+".json");
                FileStream fileLock;
                try {fileLock=File.Open(journalPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException) {throw new PriorAttempt("待核实 · 另一窗口正在处理同一文档。");}
                using(fileLock) {
                    if(File.Exists(journalPath)) {
                        journal=json.Deserialize<CreatorPublishJournal>(File.ReadAllText(journalPath));
                        if(journal==null)throw new PriorAttempt("待核实 · 上一次操作的记录无法读取。");
                        if(journal.outcome!=null && !CanRecheckBlockedToutiao(site,journal) &&
                            !(site.Id=="weibo" && journal.outcome.state=="weibo-verification"))
                            throw new PriorAttempt(CreatorCheckedOutcome(site,journal.draft)??DescribeCreatorSubmission(site,journal.outcome));
                        if(journal.draftStarted && String.IsNullOrEmpty(journal.draft))
                            throw new PriorAttempt("待核实 · 该文档可能已存为草稿，请先在平台核实。");
                    }
                    var word=await Task.Run(()=>new CreatorWord(file,source,site.Id=="wangyi"));
                    Action persist=()=>WriteCsdnAttempt(journalPath,json.Serialize(journal));
                    var session=new CreatorSession(site) {Declaration=declaration};
                    using(var view=session.Web) {
                        view.Width=1200;
                        view.Height=800;
                        view.Visible=site.Id=="toutiao" || site.Id=="baijiahao";
                        platform.Controls.Add(view);
                        view.SendToBack();
                        await InitCreatorView(view,site,allowPopups:false);
                        ConfigureCreatorPublishView(session);
                        Uri existing=null;
                        if(!String.IsNullOrEmpty(journal.draft) && !Uri.TryCreate(journal.draft,UriKind.Absolute,out existing))
                            throw new PriorAttempt("待核实 · 已存草稿地址无法读取。");
                        if(CanRecheckBlockedToutiao(site,journal)) {
                            var remote=await ReadToutiaoPublication(view,existing,file);
                            if(remote==null)throw new PriorAttempt(DescribeCreatorSubmission(site,journal.outcome));
                            if(remote.state!="draft") {
                                verifiedDraft=existing.AbsoluteUri;
                                journal.outcome=remote;
                                persist();
                                throw new PriorAttempt(DescribeCreatorSubmission(site,remote));
                            }
                        }
                        if(existing==null) {
                            var links=records.Where(record=>record.platform==site.Label && record.file==Path.GetFileName(file) &&
                                CreatorRecordDraft(site,record.creatorDraft)!=null).Select(record=>record.creatorDraft)
                                .GroupBy(address=>CreatorPublicationId(site,address)??address,StringComparer.Ordinal).Select(group=>group.First()).ToArray();
                            if(links.Length>1)throw new InvalidOperationException("这个文件名对应多篇草稿，请先确认保留的稿件。");
                            if(links.Length==1)existing=new Uri(links[0]);
                            else if((site.Id=="qiehao" || site.Id=="sohu") && declaration==null)
                                throw new InvalidOperationException("请在工作台选择这篇文章的创作声明，再发布到"+site.Label+"。");
                        }
                        if(existing!=null && GetCreatorDraftAddress(site,existing)==null)
                            throw new PriorAttempt("待核实 · 已存草稿地址校验未通过。");
                        string verified=CreatorCheckedOutcome(site,existing==null?null:existing.AbsoluteUri);
                        if(site.Id=="weibo" && journal.outcome!=null && journal.outcome.state=="weibo-verification") {
                            var remote=await CheckWeiboDraftBeforeRetry(site,word,existing);
                            if(remote.state!="draft") {
                                if(remote.state=="submitted" || remote.state=="published") {
                                    journal.outcome=remote;
                                    persist();
                                }
                                throw new PriorAttempt(DescribeCreatorSubmission(site,remote));
                            }
                            if(verified!=null && (verified.StartsWith("需处理 · ") || verified.StartsWith("未发出 · ")))verified=null;
                        }
                        if(verified!=null) {
                            verifiedDraft=existing.AbsoluteUri;
                            throw new PriorAttempt(verified);
                        }
                        ReportCreatorProgress(site,"正在准备平台稿件");
                        if(existing==null && site.Id=="qiehao")
                            await IsolateQiehaoNewDraftCache(view);
                        await NavigateCreatorPublishEditor(session,existing??new Uri(site.Editor),existing==null);
                        if(existing==null) {
                            existing=await CreateVerifiedCreatorDraft(session,word,source,()=>{
                                journal.draftStarted=true;
                                persist();
                            },declaration);
                            journal.draft=existing.AbsoluteUri;
                            persist();
                            if(view.Source==null || view.Source.AbsoluteUri!=existing.AbsoluteUri)
                                await NavigateCreatorPublishEditor(session,existing,false);
                        }
                        await WaitForCreatorContent(site,view,word,9);
                        if(declaration!=null && (site.Id=="qiehao" || site.Id=="sohu"))
                            await SetCreatorDeclaration(session,declaration);
                        if(journal.draft==null) {
                            journal.draft=existing.AbsoluteUri;
                            persist();
                        }
                        ReportCreatorProgress(site,"正在核对发布设置");
                        var outcome=await submit(session,word,existing,()=>{
                            if(sent)throw new InvalidOperationException("已提交，不能重复点击发布。");
                            journal.outcome=new CreatorSubmission {state="uncertain",message="已准备提交，正在核对平台回执。"};
                            persist();
                            sent=true;
                            ReportCreatorProgress(site,"正在核对平台回执");
                        });
                        if(!sent && (outcome==null || outcome.state!="blocked" && outcome.state!="weibo-verification"))
                            throw new InvalidOperationException("平台未完成发布准备。");
                        if(outcome==null || (sent && outcome.state=="blocked"))
                            outcome=new CreatorSubmission {state="uncertain",message="暂未收到可核对的平台回执。"};
                        if(sent || outcome.state!="blocked") {
                            journal.outcome=outcome;
                            persist();
                        }
                        result=DescribeCreatorSubmission(site,outcome);
                    }
                }
            }
        } catch(PriorAttempt ex) {result=ex.Message;}
        catch(Exception ex) {
            result=(sent || (journal!=null && journal.draftStarted && journal.draft==null)?"待核实 · ":"需处理 · ")+ex.Message;
        }
        if(!sent && journal!=null && journal.draft!=null && result.StartsWith("需处理 · ",StringComparison.Ordinal) &&
            !records.Any(record=>record.platform==site.Label && record.label=="草稿" && record.link==journal.draft && record.file==Path.GetFileName(file))) {
            Uri draft;
            if(Uri.TryCreate(journal.draft,UriKind.Absolute,out draft) && GetCreatorDraftAddress(site,draft)!=null)
                Record(Path.GetFileName(file)+"    "+site.Label+" · 草稿 · "+journal.draft);
        }
        if(verifiedDraft==null && journal!=null && journal.outcome!=null && CreatorRecordDraft(site,journal.draft)!=null)verifiedDraft=journal.draft;
        string draftRecord=verifiedDraft==null?"":"\t草稿记录="+verifiedDraft;
        if(verifiedDraft==null && journal!=null && CreatorRecordDraft(site,journal.draft)!=null && result.StartsWith("需处理 · ",StringComparison.Ordinal))
            draftRecord="\t定位草稿="+journal.draft;
        Record(Path.GetFileName(file)+"    "+site.Label+" · "+result.Replace('\r',' ').Replace('\n',' ')+draftRecord);
    }

    async Task NavigateCreatorPublishEditor(CreatorSession session,Uri address,bool allowLanding) {
        var view=session.Web;
        var core=view.CoreWebView2;
        session.State="waiting";
        bool loaded=await LoadCreatorPublishAddress(core,address);
        if(!loaded && session.Site.Id=="sohu" && !IsCreatorLoginPage(session.Site,view.Source)) {
            await Task.Delay(500);
            loaded=await LoadCreatorPublishAddress(core,address);
        }
        if(!loaded && !IsCreatorLoginPage(session.Site,view.Source))
            throw new InvalidOperationException("平台编辑器加载失败。");
        bool reopenedSohu=false;
        await Wait(async()=>{
            await CheckCreator(session);
            if(session.State=="signin")throw new InvalidOperationException("请重新登录"+session.Site.Label+"。");
            if(session.Site.Id=="sohu" && session.State=="connected" && !reopenedSohu) {
                reopenedSohu=true;
                session.State="waiting";
                core.Navigate(address.AbsoluteUri);
                return false;
            }
            return session.State=="editor" || (allowLanding && session.Site.Id=="weibo" && session.State=="connected");
        },12,"平台编辑器未就绪，请检查账号状态。");
        if(!allowLanding && (view.Source==null || view.Source.AbsoluteUri!=address.AbsoluteUri))
            throw new InvalidOperationException("平台没有打开同一个草稿编号。");
    }

    static async Task<bool> LoadCreatorPublishAddress(CoreWebView2 core,Uri address) {
        var ready=new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> completed=(sender,e)=>{
            if(e.IsSuccess || e.WebErrorStatus!=CoreWebView2WebErrorStatus.OperationCanceled)ready.TrySetResult(e.IsSuccess);
        };
        core.NavigationCompleted+=completed;
        try {
            core.Navigate(address.AbsoluteUri);
            return await Task.WhenAny(ready.Task,Task.Delay(10000))==ready.Task && await ready.Task;
        } finally {core.NavigationCompleted-=completed;}
    }

    static string DescribeCreatorSubmission(CreatorSite site,CreatorSubmission submission) {
        if(submission.state=="published" && IsCreatorPublicArticle(site,submission.url))return "已发布 · "+submission.url;
        var message=String.IsNullOrWhiteSpace(submission.message)?"请在平台创作后台核实。":submission.message;
        if(submission.state=="submitted")return "已提交 · "+message;
        if(submission.state=="review")return "审核中 · "+message;
        if(submission.state=="failed")return "未发出 · "+message;
        if(submission.state=="blocked" || submission.state=="weibo-verification")return "需处理 · "+message;
        return "待核实 · "+message;
    }

    static bool IsCreatorPublicArticle(CreatorSite site,string address) {
        Uri url;
        if(!Uri.TryCreate(address,UriKind.Absolute,out url) || url.Scheme!=Uri.UriSchemeHttps ||
            !url.IsDefaultPort || url.UserInfo.Length!=0 || url.Fragment.Length!=0)return false;
        if(site.Id=="weibo")return (url.Host=="weibo.com" || url.Host=="www.weibo.com") &&
            url.AbsolutePath=="/ttarticle/p/show" && Regex.IsMatch(url.Query,@"\A\?id=[0-9]{6,32}\z");
        if(site.Id=="qiehao")return url.Host=="page.om.qq.com" && url.Query.Length==0 &&
            Regex.IsMatch(url.AbsolutePath,@"\A/page/[A-Za-z0-9_-]{6,80}/?\z");
        if(site.Id=="toutiao")return (url.Host=="www.toutiao.com" || url.Host=="toutiao.com") && url.Query.Length==0 &&
            Regex.IsMatch(url.AbsolutePath,@"\A/(?:article/|item/|i)[0-9]{6,32}/?\z");
        if(site.Id=="baijiahao")return url.Host=="baijiahao.baidu.com" && url.AbsolutePath=="/s" &&
            Regex.IsMatch(url.Query,@"\A\?id=[0-9]{6,32}\z");
        if(site.Id=="wangyi")return url.Host=="www.163.com" && url.Query.Length==0 &&
            Regex.IsMatch(url.AbsolutePath,@"\A/dy/article/[A-Za-z0-9]{6,80}\.html\z");
        if(site.Id=="sohu")return (url.Host=="www.sohu.com" || url.Host=="biznews.sohu.com") &&
            (url.Query.Length==0 || url.Host=="www.sohu.com" && url.Query=="?sec=wd") &&
            Regex.IsMatch(url.AbsolutePath,@"\A/a/[0-9]{6,32}_[0-9]{1,32}/?\z");
        return false;
    }
}
