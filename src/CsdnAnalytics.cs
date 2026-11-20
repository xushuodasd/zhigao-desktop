using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class CsdnAccountMetrics {
        public string state { get; set; }
        public string articleCount { get; set; }
        public string reads { get; set; }
        public CsdnAccountMetrics Copy() { return (CsdnAccountMetrics)MemberwiseClone(); }
    }

    readonly WebView2 csdnAnalyticsPage=new WebView2 { Visible=false };
    const string CsdnWorksAnalyticsUrl="https://mp.csdn.net/mp_blog/analysis/article/all";
    CsdnAccountMetrics csdnMetrics=new CsdnAccountMetrics { state="idle" };
    DateTime lastCsdnMetricsRequest;
    bool loadingCsdnMetrics;
    int csdnMetricsGeneration;

    const string CsdnPublishedCountScript=@"(() => {
        if(location.origin!=='https://mp.csdn.net'||location.pathname.replace(/\/$/,'')!=='/mp_blog/manage/article')return null;
        const tabs=[...document.querySelectorAll('.nav-link[role=tab]')].filter(e=>e.getClientRects().length);
        for(const tab of tabs){
            const count=tab.textContent.trim().match(/^已发布\s*[（(]\s*(\d+(?:[,，]\d{3})*)\s*[)）]$/);
            if(count)return count[1];
        }
        return null;
    })()";
    const string CsdnReadsScript=@"(() => {
        if(location.origin!=='https://mp.csdn.net'||location.pathname.replace(/\/$/,'')!=='/mp_blog/analysis/article/all')return null;
        const card=[...document.querySelectorAll('.metric-card')].find(e=>
            e.getClientRects().length && e.querySelector('.metric-card__title')?.textContent.trim()==='阅读量');
        const value=card?.querySelector('.metric-card__num')?.textContent.trim();
        return /^(?:\d+(?:[,，]\d{3})*|\d+(?:\.\d+)?\s*[万亿])$/.test(value||'')?value:null;
    })()";

    void ClearCsdnMetrics() {
        csdnMetricsGeneration++;
        lastCsdnMetricsRequest=DateTime.MinValue;
        csdnMetrics=new CsdnAccountMetrics { state=csdnState=="signin"?"signin":"idle" };
        analyticsLastAttempt.Remove("csdn");
        analyticsCollectedAt.Remove("csdn");
    }

    async Task RefreshCsdnMetrics(bool requested) {
        if(loadingCsdnMetrics || browserEnvironment==null || IsDisposed)return;
        if(!requested && DateTime.UtcNow-lastCsdnMetricsRequest<TimeSpan.FromSeconds(30))return;
        if(csdnState=="signin" || csdnState=="idle") {
            csdnMetrics=new CsdnAccountMetrics { state="signin" };
            Render();
            return;
        }
        loadingCsdnMetrics=true;
        var started=BeginAccountAnalytics("csdn");
        lastCsdnMetricsRequest=DateTime.UtcNow;
        int generation=csdnMetricsGeneration;
        var prior=csdnMetrics;
        bool hadData=prior.state=="ready" || prior.state=="stale";
        csdnMetrics=hadData?prior.Copy():new CsdnAccountMetrics();
        csdnMetrics.state="loading";
        Render();
        var result=new CsdnAccountMetrics { state="unavailable" };
        try {
            if(csdnAnalyticsPage.CoreWebView2==null) {
                platform.Controls.Add(csdnAnalyticsPage);
                var options=browserEnvironment.CreateCoreWebView2ControllerOptions();
                options.ProfileName="Zhigao-CSDN";
                await csdnAnalyticsPage.EnsureCoreWebView2Async(browserEnvironment,options);
                csdnAnalyticsPage.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled=false;
                csdnAnalyticsPage.CoreWebView2.NewWindowRequested+=(s,e)=>{e.Handled=true;};
            }
            result.articleCount=await ReadCsdnMetric(CreatorListUrl,CsdnPublishedCountScript);
            if(IsDisposed || generation!=csdnMetricsGeneration)return;
            if(csdnAnalyticsPage.Source!=null && csdnAnalyticsPage.Source.Host=="passport.csdn.net") {
                result.state="signin";
                return;
            }
            result.reads=await ReadCsdnMetric(CsdnWorksAnalyticsUrl,CsdnReadsScript);
            if(result.articleCount!=null || result.reads!=null)result.state="ready";
        } catch(InvalidOperationException) { }
        catch(System.Runtime.InteropServices.COMException) { }
        finally {
            loadingCsdnMetrics=false;
            if(!IsDisposed && generation==csdnMetricsGeneration) {
                if(result.state=="ready")SaveAccountAnalytics("csdn",started,result.articleCount,result.reads,null);
                else if(result.state=="unavailable" && hadData) {result=prior;result.state="stale";}
                else if(result.state=="signin")analyticsCollectedAt.Remove("csdn");
                csdnMetrics=result;
                Render();
            }
        }
    }

    async Task<string> ReadCsdnMetric(string url,string script) {
        var core=csdnAnalyticsPage.CoreWebView2;
        var loaded=new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> onLoad=(s,e)=>
            loaded.TrySetResult(e.IsSuccess && e.HttpStatusCode>=200 && e.HttpStatusCode<300);
        core.NavigationCompleted+=onLoad;
        try {
            core.Navigate(url);
            if(await Task.WhenAny(loaded.Task,Task.Delay(8000))!=loaded.Task) { core.Stop(); return null; }
            if(!await loaded.Task)return null;
            string value=null;
            await Wait(async()=>{
                value=json.Deserialize<string>(await csdnAnalyticsPage.ExecuteScriptAsync(script));
                return value!=null;
            },5,"CSDN 指标未就绪");
            return value;
        } catch(TimeoutException) { return null; }
        finally { core.NavigationCompleted-=onLoad; }
    }
}
