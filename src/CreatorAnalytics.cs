using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class CreatorMetrics {
        public string state { get; set; }
        public string articleCount { get; set; }
        public string reads { get; set; }
        public string plays { get; set; }
        public string scope { get; set; }
        public string readsStatus { get; set; }
        public string playsStatus { get; set; }
        public CreatorMetrics Copy() { return (CreatorMetrics)MemberwiseClone(); }
    }

    readonly Dictionary<string,CreatorMetrics> creatorMetrics=new Dictionary<string,CreatorMetrics>();
    readonly HashSet<string> loadingCreatorMetrics=new HashSet<string>();

    Func<WebView2,Task<CreatorMetrics>>[] CreatorMetricsReaders(string platformId) {
        switch(platformId) {
            case "baijiahao": return new Func<WebView2,Task<CreatorMetrics>>[] { ReadBaijiahaoArticleCount, ReadBaijiahaoReading, ReadBaijiahaoPlaying };
            case "toutiao": return new Func<WebView2,Task<CreatorMetrics>>[] { ReadToutiaoArticleCount, ReadToutiaoReading, ReadToutiaoPlaying };
            case "weibo": return new Func<WebView2,Task<CreatorMetrics>>[] { ReadWeiboArticleCount, ReadWeiboReading, ReadWeiboPlaying };
            case "qiehao": return new Func<WebView2,Task<CreatorMetrics>>[] { ReadQiehaoCreatorMetrics };
            case "sohu": return new Func<WebView2,Task<CreatorMetrics>>[] { ReadSohuCreatorMetrics };
            case "wangyi": return new Func<WebView2,Task<CreatorMetrics>>[] { ReadWangyiCreatorMetrics };
            default: return null;
        }
    }

    async Task RefreshCreatorMetrics(CreatorSite site,bool refresh) {
        var readers=CreatorMetricsReaders(site.Id);
        if(readers==null || browserEnvironment==null || IsDisposed || loadingCreatorMetrics.Contains(site.Id))return;
        CreatorMetrics previous;
        creatorMetrics.TryGetValue(site.Id,out previous);
        if(!refresh && previous!=null && previous.state=="ready")return;
        long startingProof=CreatorConnectionProof(site.Id);
        var started=BeginAccountAnalytics(site.Id);
        loadingCreatorMetrics.Add(site.Id);
        var loading=previous!=null && (previous.state=="ready" || previous.state=="stale")?previous.Copy():new CreatorMetrics();
        loading.state="loading";
        creatorMetrics[site.Id]=loading;
        Render();
        var result=new CreatorMetrics();
        bool signin=false;
        try {
            var pending=new List<Task<CreatorMetrics>>();
            foreach(var read in readers)pending.Add(ReadCreatorMetricsInView(site,read));
            while(pending.Count!=0) {
                var finished=await Task.WhenAny(pending);
                pending.Remove(finished);
                var data=await finished;
                if(data==null)continue;
                if(data.state=="signin") {signin=true;continue;}
                MergeCreatorMetrics(result,data);
                if(!IsDisposed && IsCurrentCreatorMetrics(site,loading)) {
                    MergeCreatorMetrics(loading,data);
                    Render();
                }
            }
        } finally {
            loadingCreatorMetrics.Remove(site.Id);
            if(!IsDisposed && IsCurrentCreatorMetrics(site,loading)) {
                if(!HasCreatorMetrics(result) && signin && CreatorConnectionProof(site.Id)==startingProof) {
                    MarkCreatorSignInRequired(site);
                }
                else {
                    if(HasCreatorMetrics(result)) {
                        result.state="ready";
                        RememberCreator(site.Id);
                        SaveAccountAnalytics(site.Id,started,result.articleCount,result.reads,result.plays);
                    }
                    else if(HasCreatorMetrics(previous) && !signin) {result=previous.Copy();result.state="stale";}
                    else result.state="unavailable";
                    creatorMetrics[site.Id]=result;
                }
                Render();
            }
        }
    }

    bool IsCurrentCreatorMetrics(CreatorSite site,CreatorMetrics loading) {
        CreatorMetrics current;
        return creatorMetrics.TryGetValue(site.Id,out current) && Object.ReferenceEquals(current,loading);
    }

    static void MergeCreatorMetrics(CreatorMetrics target,CreatorMetrics source) {
        var articleCount=CreatorMetricNumber(source.articleCount);
        var reads=CreatorMetricNumber(source.reads);
        var plays=CreatorMetricNumber(source.plays);
        if(articleCount!=null)target.articleCount=articleCount;
        if(reads!=null || source.readsStatus!=null) {target.reads=reads;target.readsStatus=source.readsStatus;}
        if(plays!=null || source.playsStatus!=null) {target.plays=plays;target.playsStatus=source.playsStatus;}
        if(source.scope!=null)target.scope=source.scope;
    }

    async Task<CreatorMetrics> ReadCreatorMetricsInView(CreatorSite site,Func<WebView2,Task<CreatorMetrics>> read) {
        if(IsDisposed)return null;
        try {
            using(var view=new WebView2 {Visible=site.Id=="toutiao", Width=1100, Height=730}) {
                if(site.Id=="toutiao")view.Dock=DockStyle.Fill;
                platform.Controls.Add(view);
                view.SendToBack();
                await InitCreatorView(view,site,allowPopups:false);
                var data=await read(view);
                return IsCreatorLoginPage(site,view.Source)?new CreatorMetrics {state="signin"}:data;
            }
        } catch(InvalidOperationException) {return null;}
        catch(System.Runtime.InteropServices.COMException) {return null;}
    }

    static bool HasCreatorMetrics(CreatorMetrics metrics) {
        return metrics!=null && (metrics.articleCount!=null || metrics.reads!=null || metrics.plays!=null);
    }

    static string CreatorMetricNumber(string value) {
        value=(value??"").Trim();
        return Regex.IsMatch(value,@"\A(?:\d+(?:[,，]\d{3})*|\d+(?:\.\d+)?\s*[万亿])\z")?value:null;
    }

    async Task<CreatorMetrics> ReadCreatorMetricsPage(WebView2 view,string url,string script) {
        var core=view.CoreWebView2;
        var loaded=new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> onLoad=(sender,e)=>
            loaded.TrySetResult(e.IsSuccess && e.HttpStatusCode>=200 && e.HttpStatusCode<300);
        core.NavigationCompleted+=onLoad;
        try {
            core.Navigate(url);
            if(await Task.WhenAny(loaded.Task,Task.Delay(8500))!=loaded.Task) {core.Stop();return null;}
            if(!await loaded.Task)return null;
        } finally {core.NavigationCompleted-=onLoad;}
        CreatorMetrics values=null;
        try {
            await Wait(async()=>{
                values=json.Deserialize<CreatorMetrics>(await view.ExecuteScriptAsync(script));
                return HasCreatorMetrics(values) || (values!=null && values.state=="unavailable");
            },6,"平台账号指标未就绪。");
            return values;
        } catch(TimeoutException) {return null;}
    }
}
