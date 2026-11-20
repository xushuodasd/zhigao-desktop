using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string SohuMetricsHome = "https://mp.sohu.com/mpfe/v4/contentManagement/first/page";
    const string SohuMetricsScope = "已发布文章 · 全部内容累计。阅读为平台曝光口径，播放为访问（播放）口径。";
    const string SohuMetricsNavigation = @"(() => {
        if(location.origin!=='https://mp.sohu.com'||location.pathname!=='/mpfe/v4/contentManagement/first/page')return false;
        const links=[...document.querySelectorAll('.menu-item .title.child-level')].filter(e=>
            e.getClientRects().length && e.innerText.trim()==='内容分析');
        if(links.length!==1)return false;
        links[0].click();
        return true;
    })()";

    sealed class SohuMetricsReply<T> {
        public bool success { get; set; }
        public int? code { get; set; }
        public T data { get; set; }
    }

    sealed class SohuPublishedArticleCount {
        public long? text_pass_cnt { get; set; }
    }

    sealed class SohuTrafficTotal {
        public string type { get; set; }
        public long? all { get; set; }
    }

    async Task<CreatorMetrics> ReadSohuCreatorMetrics(WebView2 view) {
        var core=view.CoreWebView2;
        var metrics=new CreatorMetrics { scope=SohuMetricsScope };
        bool closed=false;
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> received=async (sender,e)=>{
            Uri address;
            if(closed || e.Request.Method!="GET" || e.Response.StatusCode!=200 ||
                !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out address) || SohuMetricsResponseKind(address)==null)
                return;
            try {
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null || closed)return;
                    using(var reader=new StreamReader(stream)) {
                        var buffer=new char[16385];
                        int size=await Task.Run(()=>reader.ReadBlock(buffer,0,buffer.Length));
                        if(!closed && size<buffer.Length)ApplySohuMetricsReply(metrics,address,new string(buffer,0,size));
                    }
                }
            } catch(InvalidOperationException) { }
            catch(IOException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };

        core.WebResourceResponseReceived+=received;
        try {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            bool navigationStarted=false;
            core.Navigate(SohuMetricsHome);
            await Wait(async()=>{
                var address=view.Source;
                if(address==null || address.Scheme!="https" || address.Host!="mp.sohu.com")return false;
                if(address.AbsolutePath.StartsWith("/mpfe/v4/login",StringComparison.Ordinal))return true;
                if(!navigationStarted && (metrics.articleCount!=null || timer.Elapsed.TotalSeconds>=4))
                    navigationStarted=await view.ExecuteScriptAsync(SohuMetricsNavigation)=="true";
                return metrics.articleCount!=null && metrics.reads!=null && metrics.plays!=null;
            },13,"搜狐号账号数据未就绪。");
        } catch(TimeoutException) {
        } finally {
            closed=true;
            core.WebResourceResponseReceived-=received;
        }
        return HasCreatorMetrics(metrics)?metrics:null;
    }

    static string SohuMetricsResponseKind(Uri address) {
        if(address.Scheme!="https" || address.Host!="mp.sohu.com" || !address.IsDefaultPort ||
            address.UserInfo.Length!=0)return null;
        switch(address.AbsolutePath) {
            case "/mpbp/bp/news/v4/users/newsInfo":
                return Regex.IsMatch(address.Query,@"(?:^\?|&)adFlag=")?null:"count";
            case "/mpbp/bp/news/v4/users/stat/overall/detail":
                return Regex.IsMatch(address.Query,@"(?:^\?|&)type=0(?:&|$)")?"traffic":null;
            default:
                return null;
        }
    }

    void ApplySohuMetricsReply(CreatorMetrics metrics,Uri address,string payload) {
        try {
            switch(SohuMetricsResponseKind(address)) {
                case "count":
                    var publication=json.Deserialize<SohuMetricsReply<SohuPublishedArticleCount>>(payload);
                    if(publication!=null && publication.success && publication.code==2000000 &&
                        publication.data!=null && publication.data.text_pass_cnt>=0)
                        metrics.articleCount=publication.data.text_pass_cnt.Value.ToString(CultureInfo.InvariantCulture);
                    break;
                case "traffic":
                    var traffic=json.Deserialize<SohuMetricsReply<SohuTrafficTotal[]>>(payload);
                    if(traffic==null || !traffic.success || traffic.code!=2000000 || traffic.data==null)break;
                    foreach(var item in traffic.data) {
                        if(item==null || !(item.all>=0))continue;
                        var count=item.all.Value.ToString(CultureInfo.InvariantCulture);
                        if(item.type=="pv")metrics.reads=count;
                        if(item.type=="pvValid")metrics.plays=count;
                    }
                    break;
            }
        } catch(ArgumentException) { }
        catch(InvalidOperationException) { }
        catch(FormatException) { }
        catch(OverflowException) { }
    }
}
