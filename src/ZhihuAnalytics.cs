using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string ZhihuAnalyticsUrl = "https://www.zhihu.com/creator/analytics/work/all";
    readonly WebView2 zhihuAnalyticsPage = new WebView2 { Visible=false };
    ZhihuAccountMetrics zhihuMetrics = new ZhihuAccountMetrics { state="idle" };
    DateTime lastZhihuMetricsRequest;
    int zhihuMetricsGeneration;
    bool loadingZhihuMetrics;

    sealed class ZhihuAccountMetrics {
        public string state { get; set; }
        public string articleCount { get; set; }
        public string reads { get; set; }
        public string plays { get; set; }
        public string readsToday { get; set; }
        public string playsToday { get; set; }
        public string updated { get; set; }
        public ZhihuAccountMetrics Copy() { return (ZhihuAccountMetrics)MemberwiseClone(); }
    }

    const string ZhihuAnalyticsControls = @"
        if(location.origin!=='https://www.zhihu.com'||location.pathname.replace(/\/$/,'')!=='/creator/analytics/work/all')return null;
        const visible=e=>!!e.getClientRects().length;
        const periods=['近7日','近14日','近30日','累计数据'];
        const candidates=[...document.querySelectorAll('button,a,div,span')].filter(e=>
            e.childElementCount===0 && visible(e) && periods.includes(e.innerText?.trim()));
        const unique=label=>{const matches=candidates.filter(e=>e.innerText.trim()===label);return matches.length===1?matches[0]:null;};
        const lifetime=unique('累计数据');
        const week=unique('近7日');
        if(!lifetime||!week||!unique('近14日')||!unique('近30日'))return null;
        const selected = e=>e.getAttribute('aria-selected')==='true'||e.getAttribute('aria-pressed')==='true';
        const cumulative=selected(lifetime)||(!selected(week)&&getComputedStyle(lifetime).color==='rgb(23, 114, 246)'&&
             getComputedStyle(week).color!==getComputedStyle(lifetime).color);
    ";
    const string ZhihuTrafficSnapshot = @"
        if(!cumulative)return null;
        const number=/^(?:\d+(?:[,，]\d{3})*|\d+(?:\.\d+)?\s*[万亿])$/;
        function metric(label) {
            const titles=[...document.querySelectorAll('div,span')].filter(e=>e.childElementCount===0&&visible(e)&&e.innerText?.trim()===label);
            for(const title of titles){
                const card=title.parentElement;
                const value=title.nextElementSibling?.innerText.trim();
                if(!number.test(value||'')||!card.innerText.includes('今日'))continue;
                const today=card.innerText.match(/今日\s*([0-9,.，万亿]+)/)?.[1];
                return {value,today:number.test(today||'')?today:null};
            }
            return {};
        }
        const reading=metric('阅读数'),playing=metric('播放数');
        if(reading.value==null && playing.value==null)return null;
        const updated=document.body.innerText.match(/(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(?::\d{2})?)\s*更新/)?.[1];
        return {reads:reading.value,plays:playing.value,readsToday:reading.today,playsToday:playing.today,updated};
    ";

    Task<string> ReadAnalytics(string script) {
        return zhihuAnalyticsPage.ExecuteScriptAsync("(() => {"+ZhihuAnalyticsControls+script+"})()");
    }

    void ClearZhihuMetrics() {
        zhihuMetricsGeneration++;
        lastZhihuMetricsRequest=DateTime.MinValue;
        zhihuMetrics=new ZhihuAccountMetrics { state=previous=="signin"?"signin":"idle" };
        analyticsLastAttempt.Remove("zhihu");
        analyticsCollectedAt.Remove("zhihu");
    }

    async Task RefreshZhihuMetrics(bool requested) {
        if (loadingZhihuMetrics || browserEnvironment==null || IsDisposed) return;
        if (!requested && DateTime.UtcNow-lastZhihuMetricsRequest<TimeSpan.FromSeconds(30)) return;
        if (previous=="signin") {
            ClearZhihuMetrics();
            Render();
            return;
        }
        loadingZhihuMetrics=true;
        var started=BeginAccountAnalytics("zhihu");
        lastZhihuMetricsRequest=DateTime.UtcNow;
        int generation=zhihuMetricsGeneration;
        var prior=zhihuMetrics;
        bool hadData=prior.state=="ready" || prior.state=="stale";
        zhihuMetrics=hadData?prior.Copy():new ZhihuAccountMetrics();
        zhihuMetrics.state="loading";
        Render();
        ZhihuAccountMetrics result = new ZhihuAccountMetrics { state="unavailable" };
        try {
            if (zhihuAnalyticsPage.CoreWebView2==null) {
                platform.Controls.Add(zhihuAnalyticsPage);
                await zhihuAnalyticsPage.EnsureCoreWebView2Async(browserEnvironment);
                zhihuAnalyticsPage.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled=false;
                zhihuAnalyticsPage.CoreWebView2.NewWindowRequested+=(s,e)=>{e.Handled=true;};
            }
            var core=zhihuAnalyticsPage.CoreWebView2;
            var loaded=new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> onLoad=(s,e)=>loaded.TrySetResult(e.IsSuccess);
            core.NavigationCompleted+=onLoad;
            try {
                core.Navigate(ZhihuAnalyticsUrl);
                if(await Task.WhenAny(loaded.Task,Task.Delay(10000))!=loaded.Task) { core.Stop(); return; }
                if(!await loaded.Task)return;
            } finally { core.NavigationCompleted-=onLoad; }
            await Wait(async ()=>{
                if (AnalyticsNeedsLogin()) return true;
                return await ReadAnalytics("return true;")=="true";
            },12,"知乎创作中心未就绪");
            if (AnalyticsNeedsLogin()) { result.state="signin"; return; }
            await ReadAnalytics("if(!cumulative) lifetime.click(); return true;");
            await Task.Delay(1200);
            ZhihuAccountMetrics traffic=null;
            try {
                await Wait(async()=>{
                    traffic=json.Deserialize<ZhihuAccountMetrics>(await ReadAnalytics(ZhihuTrafficSnapshot));
                    return traffic!=null && traffic.reads!=null && traffic.plays!=null;
                },6,"知乎累计数据未就绪");
            } catch(TimeoutException) { }
            if(traffic!=null) result=traffic;
            result.articleCount=await ReadZhihuArticleCount();
            result.state=result.articleCount!=null || result.reads!=null || result.plays!=null?"ready":"unavailable";
        } catch(TimeoutException) { result.state=AnalyticsNeedsLogin()?"signin":"unavailable"; }
        catch(InvalidOperationException) { }
        catch(System.Runtime.InteropServices.COMException) { }
        finally {
            loadingZhihuMetrics=false;
            if (!IsDisposed && generation==zhihuMetricsGeneration) {
                if(result.state=="ready")SaveAccountAnalytics("zhihu",started,result.articleCount,result.readsToday,result.playsToday,result.updated);
                else if(result.state=="unavailable" && hadData) {result=prior;result.state="stale";}
                else if(result.state=="signin")analyticsCollectedAt.Remove("zhihu");
                zhihuMetrics=result;
                Render();
            }
        }
    }

    bool AnalyticsNeedsLogin() {
        var address=zhihuAnalyticsPage.Source;
        return address!=null && address.Scheme=="https" && address.Host=="www.zhihu.com" &&
            address.AbsolutePath.StartsWith("/signin",StringComparison.Ordinal);
    }

    async Task<string> ReadZhihuArticleCount() {
        // Read this account's current published article count separately from delayed aggregate metrics.
        await zhihuAnalyticsPage.ExecuteScriptAsync(@"(() => {
            if(location.origin!=='https://www.zhihu.com'||!location.pathname.startsWith('/creator/analytics/'))return;
            window.__zhigaoStatsArticleCount=null;
            fetch('/api/v4/me').then(r=>r.ok?r.json():null).then(user=>{
                if(Number.isSafeInteger(user?.articles_count) && user.articles_count>=0)
                    window.__zhigaoStatsArticleCount=String(user.articles_count);
            }).catch(()=>{});
        })()");
        string count=null;
        try {
            await Wait(async()=>{
                count=json.Deserialize<string>(await zhihuAnalyticsPage.ExecuteScriptAsync("window.__zhigaoStatsArticleCount||null"));
                return !String.IsNullOrWhiteSpace(count);
            },5,"知乎文章总数未就绪");
            return count;
        } catch(TimeoutException) { return null; }
    }
}
