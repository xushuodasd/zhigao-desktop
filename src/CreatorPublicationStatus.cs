using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    readonly HashSet<string> creatorChecks=new HashSet<string>(StringComparer.Ordinal);
    readonly SemaphoreSlim creatorCheckSlots=new SemaphoreSlim(2);
    System.Windows.Forms.Timer creatorReceiptTimer;
    int receiptDelaySeconds;

    // Platform content lists can lag a publish response. Refresh only recent, unresolved IDs.
    void WatchRecentCreatorReceipts() {
        if(IsDisposed || Disposing || !enableStartupRecovery)return;
        if(creatorReceiptTimer==null) {
            creatorReceiptTimer=new System.Windows.Forms.Timer();
            creatorReceiptTimer.Tick+=async delegate {
                creatorReceiptTimer.Stop();
                if(IsDisposed || Disposing)return;
                var recent=Array.FindAll(PendingCreatorRecords(),entry=>{
                    DateTime issued;
                    return DateTime.TryParseExact(entry.time,"yyyy-MM-dd HH:mm",System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,out issued) && issued>=DateTime.Now.AddDays(-1) &&
                        !creatorSignInRequired.Contains(Array.Find(CreatorSites,site=>site.Label==entry.platform).Id);
                });
                if(recent.Length==0)return;
                if(!busy)await VerifySubmittedCreatorRecords(recent);
                if(IsDisposed || Disposing)return;
                receiptDelaySeconds=Math.Min(900,receiptDelaySeconds*3);
                creatorReceiptTimer.Interval=receiptDelaySeconds*1000;
                creatorReceiptTimer.Start();
            };
            FormClosed+=delegate {creatorReceiptTimer.Stop();creatorReceiptTimer.Dispose();};
        }
        receiptDelaySeconds=20;
        creatorReceiptTimer.Interval=receiptDelaySeconds*1000;
        creatorReceiptTimer.Start();
    }

    partial void ConfigureCreatorVerificationView(WebView2 view);

    static Uri CreatorRecordDraft(CreatorSite site,string value) {
        Uri address;
        if(!Uri.TryCreate(value,UriKind.Absolute,out address) || address.Scheme!=Uri.UriSchemeHttps ||
            !address.IsDefaultPort || address.UserInfo.Length!=0)return null;
        return GetCreatorDraftAddress(site,address);
    }

    static bool CanVerifyCreatorRecord(Entry entry) {
        if(entry.label!="草稿" && entry.label!="待核实" && entry.label!="已提交" && entry.label!="审核中" && entry.label!="需处理")return false;
        var site=Array.Find(CreatorSites,s=>s.Label==entry.platform);
        return site!=null && CreatorPublicationId(site,entry.creatorDraft)!=null;
    }

    static string CreatorPublicationId(CreatorSite site,string draft) {
        var address=CreatorRecordDraft(site,draft);
        if(address==null)return null;
        string id;
        switch(site.Id) {
            case "baijiahao": id=BaijiahaoMetricQuery(address,"article_id"); break;
            case "toutiao": id=ToutiaoQueryValue(address,"pgc_id"); break;
            case "weibo": id=Regex.Match(address.Fragment,@"\A#/draft/([0-9]{6,32})\z").Groups[1].Value; break;
            case "qiehao":
                id=QiehaoQueryValue(address,"articleId");
                return Regex.IsMatch(id??"",@"\A[A-Za-z0-9_-]{6,80}\z")?id:null;
            case "sohu": id=QiehaoQueryValue(address,"id"); break;
            case "wangyi": return WangyiPublishDraftId(address);
            default: return null;
        }
        return Regex.IsMatch(id??"",@"\A[0-9]{6,32}\z")?id:null;
    }

    static bool SameCreatorPublication(string platformLabel,string left,string right) {
        if(left==null || right==null)return false;
        var site=Array.Find(CreatorSites,s=>s.Label==platformLabel);
        if(site==null)return false;
        var id=CreatorPublicationId(site,left);
        return id!=null && id==CreatorPublicationId(site,right);
    }

    string CreatorCheckedOutcome(CreatorSite site,string draft) {
        if(CreatorRecordDraft(site,draft)==null)return null;
        var entry=records.Find(record=>record.platform==site.Label && SameCreatorPublication(site.Label,record.creatorDraft,draft) &&
            (record.label=="已发布" || record.label=="审核中" || record.label=="未发出" || record.label=="需处理" || record.label=="已提交"));
        return entry==null?null:entry.label+" · "+entry.detail;
    }

    async Task VerifySubmittedCreatorRecords(Entry[] entries) {
        var checks=new List<Task>();
        foreach(var entry in entries)checks.Add(VerifyCreatorRecord(entry,true));
        await Task.WhenAll(checks);
    }

    Entry[] PendingCreatorRecords(string platformLabel=null) {
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var pending=new List<Entry>();
        foreach(var entry in records) {
            if(platformLabel!=null && entry.platform!=platformLabel)continue;
            var site=Array.Find(CreatorSites,s=>s.Label==entry.platform);
            if(site==null)continue;
            var id=CreatorPublicationId(site,entry.creatorDraft);
            if(id==null || !seen.Add(site.Id+"/"+id))continue;
            if(entry.label=="已提交" || entry.label=="审核中" || entry.label=="待核实")pending.Add(entry);
        }
        return pending.ToArray();
    }

    async Task VerifyCreatorRecord(Entry entry,bool automatic=false) {
        var site=Array.Find(CreatorSites,s=>s.Label==entry.platform);
        if(site==null || IsDisposed || (automatic && creatorSignInRequired.Contains(site.Id)))return;
        var address=CreatorRecordDraft(site,entry.creatorDraft);
        var id=CreatorPublicationId(site,entry.creatorDraft);
        if(id==null)return;
        string key=site.Id+"/"+id;
        if(!creatorChecks.Add(key))return;
        if(!automatic) {
            verifying=true;
            verificationNotice="正在"+site.Label+"核实《"+Path.GetFileNameWithoutExtension(entry.file)+"》";
        }
        SetCreatorChecking(entry,true);
        string notice=null;
        bool occupied=false;
        try {
            Render();
            await creatorCheckSlots.WaitAsync();
            occupied=true;
            if(IsDisposed || (automatic && creatorSignInRequired.Contains(site.Id)))return;
            long startingProof=CreatorConnectionProof(site.Id);
            using(var view=new WebView2 {Visible=site.Id=="toutiao", Width=1100, Height=730}) {
                if(site.Id=="toutiao")view.Dock=DockStyle.Fill;
                platform.Controls.Add(view);
                view.SendToBack();
                await InitCreatorView(view,site,allowPopups:false);
                ConfigureCreatorVerificationView(view);
                var observed=await ReadCreatorPublication(site,view,address,entry.file);
                if(IsDisposed)return;
                if(observed==null) {
                    if(IsCreatorLoginPage(site,view.Source) && CreatorConnectionProof(site.Id)==startingProof) {
                        MarkCreatorSignInRequired(site);
                        notice="请重新登录"+site.Label+"后再核实。";
                    } else notice=site.Label+"暂未返回同编号的明确状态，已保留上次结果。";
                    return;
                }
                if(observed.state=="draft") {
                    notice=site.Label+"仍显示这篇稿件在草稿箱。";
                    return;
                }
                string outcome=DescribeCreatorSubmission(site,observed);
                if(outcome!=entry.label+" · "+entry.detail)SaveVerification(entry,outcome);
                notice=site.Label+"核实结果 · "+entry.label;
            }
        } catch(Exception) {
            notice=site.Label+"核实未完成，可稍后再查。";
        } finally {
            if(occupied)creatorCheckSlots.Release();
            creatorChecks.Remove(key);
            SetCreatorChecking(entry,false);
            if(!automatic) {verifying=false;verificationNotice=notice;}
            if(!IsDisposed)Render();
        }
    }

    void SetCreatorChecking(Entry entry,bool checking) {
        foreach(var record in records)
            if(SameVerificationTarget(record,entry))record.checking=checking;
        foreach(var pair in batchEntries)
            if(SameVerificationTarget(pair.Value,entry))pair.Key.checking=checking;
    }

    Task<CreatorSubmission> ReadCreatorPublication(CreatorSite site,WebView2 view,Uri address,string file) {
        switch(site.Id) {
            case "baijiahao": return ReadBaijiahaoPublication(view,CreatorPublicationId(site,address.AbsoluteUri));
            case "toutiao": return ReadToutiaoPublication(view,address,file);
            case "weibo": return ReadWeiboPublication(view,CreatorPublicationId(site,address.AbsoluteUri));
            case "qiehao": return ReadQiehaoPublication(view,address);
            case "sohu": return ReadSohuPublication(view,CreatorPublicationId(site,address.AbsoluteUri),file);
            case "wangyi": return ReadWangyiPublication(view,CreatorPublicationId(site,address.AbsoluteUri),file);
            default: return Task.FromResult<CreatorSubmission>(null);
        }
    }

    async Task<bool> NavigateCreatorPublicationList(WebView2 view,string url) {
        var core=view.CoreWebView2;
        var loaded=new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> onLoad=(sender,e)=>
            loaded.TrySetResult(e.IsSuccess && e.HttpStatusCode>=200 && e.HttpStatusCode<300);
        core.NavigationCompleted+=onLoad;
        try {
            core.Navigate(url);
            if(await Task.WhenAny(loaded.Task,Task.Delay(9000))!=loaded.Task) {core.Stop();return false;}
            return await loaded.Task;
        } finally {core.NavigationCompleted-=onLoad;}
    }

    async Task<CreatorSubmission> ReadCreatorPublicationResponse(WebView2 view,Uri request,Func<string,CreatorSubmission> parse,string form=null) {
        if(view.Source==null || view.Source.GetLeftPart(UriPartial.Authority)!=request.GetLeftPart(UriPartial.Authority))return null;
        var options=form==null?(object)new {cache="no-store",redirect="error"}:new {cache="no-store",redirect="error",method="POST",body=form,
            headers=new Dictionary<string,string> {
                {"Content-Type","application/x-www-form-urlencoded"},{"Accept","application/json"}
            }};
        string expression=CreatorPublicationFetch+"("+json.Serialize(request.AbsoluteUri)+","+json.Serialize(options)+")";
        var pending=view.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",json.Serialize(new {
            expression,awaitPromise=true,returnByValue=true
        }));
        if(await Task.WhenAny(pending,Task.Delay(6500))!=pending)return null;
        var reply=json.Deserialize<CreatorPublicationFetchReply>(await pending);
        return reply!=null && reply.result!=null && reply.result.value!=null?parse(reply.result.value):null;
    }

    sealed class CreatorPublicationFetchReply {
        public CreatorPublicationFetchValue result { get; set; }
    }

    sealed class CreatorPublicationFetchValue {
        public string value { get; set; }
    }

    const string CreatorPublicationFetch=@"(async (address,options) => {
        if(new URL(address).origin!==location.origin)return null;
        try {
            const response=await fetch(address,{...options,signal:AbortSignal.timeout(6000)});
            if(response.status!==200 || response.url!==address || !response.body)return null;
            const input=response.body.getReader(),decoder=new TextDecoder();
            let body='',bytes=0;
            for(;;) {
                const {value,done}=await input.read();
                if(done)return body+decoder.decode();
                bytes+=value.byteLength;
                if(bytes>1048576) {await input.cancel();return null;}
                body+=decoder.decode(value,{stream:true});
            }
        } catch {return null;}
    })";
}
