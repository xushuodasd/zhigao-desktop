using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string BaijiahaoWorksUrl="https://baijiahao.baidu.com/builder/rc/content";
    const string BaijiahaoAnalyticsUrl="https://baijiahao.baidu.com/builder/rc/analysiscontent";

    sealed class BaijiahaoWorksReply {
        public int? errno { get; set; }
        public BaijiahaoWorksData data { get; set; }
    }

    sealed class BaijiahaoWorksData {
        public BaijiahaoWorksPage page { get; set; }
    }

    sealed class BaijiahaoWorksPage {
        public long? totalCount { get; set; }
    }

    sealed class BaijiahaoTrafficReply {
        public int? errno { get; set; }
        public BaijiahaoTrafficData data { get; set; }
    }

    sealed class BaijiahaoTrafficData {
        public object total_info { get; set; }
        public BaijiahaoTrafficExport export_conf { get; set; }
    }

    sealed class BaijiahaoTrafficExport {
        public Dictionary<string,string> map_conf { get; set; }
    }

    async Task<CreatorMetrics> ReadBaijiahaoArticleCount(WebView2 view) {
        return new CreatorMetrics {articleCount=await ReadBaijiahaoPublishedCount(view)};
    }

    Task<CreatorMetrics> ReadBaijiahaoReading(WebView2 view) { return ReadBaijiahaoTraffic(view,"news","图文"); }
    Task<CreatorMetrics> ReadBaijiahaoPlaying(WebView2 view) { return ReadBaijiahaoTraffic(view,"video","视频"); }

    async Task<CreatorMetrics> ReadBaijiahaoTraffic(WebView2 view,string kind,string tab) {
        var core=view.CoreWebView2;
        var received=new TaskCompletionSource<string>();
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> receive=async(sender,e)=>{
            Uri request;
            if(received.Task.IsCompleted || e.Request.Method!="GET" || e.Response.StatusCode!=200 ||
                !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out request))return;
            if(BaijiahaoTrafficResponseKind(request)!=kind)return;
            try {
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null)return;
                    using(var reader=new StreamReader(stream)) {
                        var buffer=new char[524289];
                        int size=reader.ReadBlock(buffer,0,buffer.Length);
                        if(size>=buffer.Length)return;
                        string count;
                        if(TryReadBaijiahaoTraffic(new string(buffer,0,size),kind,out count))received.TrySetResult(count);
                    }
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.WebResourceResponseReceived+=receive;
        try {
            core.Navigate(BaijiahaoAnalyticsUrl);
            await Wait(async()=>{
                var address=view.Source;
                if(address==null || address.Host!="baijiahao.baidu.com" || address.AbsolutePath!="/builder/rc/analysiscontent")return false;
                return await view.ExecuteScriptAsync(BaijiahaoTrafficTab+"("+json.Serialize(tab)+")")=="true" && received.Task.IsCompleted;
            },7,"百家号整体数据未就绪。");
            var count=await received.Task;
            return kind=="news"?new CreatorMetrics {reads=count,readsStatus=count==null?"平台未提供汇总":null}
                :new CreatorMetrics {plays=count,playsStatus=count==null?"平台未提供汇总":null};
        } catch(TimeoutException) {return null;}
        finally {core.WebResourceResponseReceived-=receive;}
    }

    async Task<string> ReadBaijiahaoPublishedCount(WebView2 view) {
        var core=view.CoreWebView2;
        var result=new TaskCompletionSource<string>();
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> receive=async(sender,e)=>{
            Uri request;
            if(e.Request.Method!="GET" || e.Response.StatusCode!=200 || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out request) ||
                !IsBaijiahaoPublishedCountResponse(request))return;
            try {
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null)return;
                    using(var reader=new StreamReader(stream)) {
                        var buffer=new char[524289];
                        int size=reader.ReadBlock(buffer,0,buffer.Length);
                        if(size>=buffer.Length)return;
                        var count=ParseBaijiahaoPublishedCount(new string(buffer,0,size));
                        if(count!=null)result.TrySetResult(count);
                    }
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.WebResourceResponseReceived+=receive;
        try {
            core.Navigate(BaijiahaoWorksUrl);
            await Wait(async()=>{
                var source=view.Source;
                if(source==null || source.Host!="baijiahao.baidu.com" || source.AbsolutePath!="/builder/rc/content")return false;
                return await view.ExecuteScriptAsync(BaijiahaoPublishedFilters)=="true" && result.Task.IsCompleted;
            },9,"百家号已发布图文列表未就绪。");
            return await result.Task;
        } catch(TimeoutException) { return null; }
        finally { core.WebResourceResponseReceived-=receive; }
    }

    const string BaijiahaoPublishedFilters=@"(() => {
        if(location.origin!=='https://baijiahao.baidu.com' || location.pathname!=='/builder/rc/content')return false;
        const labels=[...document.querySelectorAll('.cheetah-tabs-tab-btn')].filter(e=>e.getClientRects().length && !e.closest('[hidden],[aria-hidden=true]'));
        const art=labels.filter(e=>e.innerText.trim()==='图文');
        const live=labels.filter(e=>e.innerText.trim()==='已发布');
        if(art.length!==1 || live.length!==1)return false;
        const selected=e=>!!e.closest('.cheetah-tabs-tab-active');
        if(!selected(art[0])) {art[0].click();return false;}
        if(!selected(live[0])) {live[0].click();return false;}
        return true;
    })()";

    const string BaijiahaoTrafficTab=@"(kind => {
        if(location.origin!=='https://baijiahao.baidu.com'||location.pathname!=='/builder/rc/analysiscontent')return false;
        const visible=e=>e.getClientRects().length && !e.closest('[hidden],[aria-hidden=true]');
        const labels=[...document.querySelectorAll('.cheetah-tabs-tab-btn')].filter(visible);
        const tabs=labels.filter(e=>e.innerText.trim()===kind);
        const active=e=>!!e.closest('.cheetah-tabs-tab-active');
        if(tabs.length!==1 || !labels.some(e=>active(e)&&e.innerText.trim()==='整体数据'))return false;
        if(!active(tabs[0])) {tabs[0].click();return false;}
        const pages=[...document.querySelectorAll('.cheetah-picker-suffix')].filter(visible);
        return pages.length>0 && pages.every(e=>e.innerText.trim()==='近7天');
    })";

    static bool IsBaijiahaoPublishedCountResponse(Uri url) {
        return url.Scheme=="https" && url.Host=="baijiahao.baidu.com" && url.IsDefaultPort && url.UserInfo.Length==0 &&
            url.AbsolutePath=="/pcui/article/lists" && BaijiahaoMetricQuery(url,"type")=="news" &&
            BaijiahaoMetricQuery(url,"collection")=="publish" && BaijiahaoMetricQuery(url,"currentPage")=="1" &&
            BaijiahaoMetricQuery(url,"search")=="" && BaijiahaoMetricQuery(url,"startDate")=="" && BaijiahaoMetricQuery(url,"endDate")=="";
    }

    static string BaijiahaoMetricQuery(Uri url,string key) {
        var matches=Regex.Matches(url.Query,@"(?:^\?|&)"+Regex.Escape(key)+@"=([^&]*)");
        return matches.Count==1?Uri.UnescapeDataString(matches[0].Groups[1].Value.Replace("+"," ")):null;
    }

    static string BaijiahaoTrafficResponseKind(Uri url) {
        if(url.Scheme!="https" || url.Host!="baijiahao.baidu.com" || !url.IsDefaultPort ||
            url.UserInfo.Length!=0 || url.AbsolutePath!="/author/eco/statistics/appStatisticV3" ||
            BaijiahaoMetricQuery(url,"stat")!="0" || BaijiahaoMetricQuery(url,"special_filter_days")!="7")return null;
        string kind=BaijiahaoMetricQuery(url,"type");
        return kind=="news" || kind=="video"?kind:null;
    }

    bool TryReadBaijiahaoTraffic(string payload,string kind,out string value) {
        value=null;
        try {
            var parsed=json.Deserialize<BaijiahaoTrafficReply>(payload);
            if(parsed==null || parsed.errno!=0 || parsed.data==null)return false;
            var summary=parsed.data.total_info;
            var fields=summary as Dictionary<string,object>;
            if(fields==null) {
                var empty=summary as IList;
                return summary==null || (empty!=null && empty.Count==0);
            }
            var map=parsed.data.export_conf==null?null:parsed.data.export_conf.map_conf;
            string key;
            if(map==null || !map.TryGetValue(kind=="news"?"阅读量":"播放量",out key) || String.IsNullOrEmpty(key))
                key="view_count";
            object raw;
            long amount;
            if(fields.TryGetValue(key,out raw) && (raw is string || raw is int || raw is long) &&
                Int64.TryParse(Convert.ToString(raw,CultureInfo.InvariantCulture),NumberStyles.None,CultureInfo.InvariantCulture,out amount))
                value=amount.ToString(CultureInfo.InvariantCulture);
            return true;
        } catch(ArgumentException) { return false; }
        catch(InvalidOperationException) { return false; }
        catch(FormatException) { return false; }
        catch(OverflowException) { return false; }
    }

    string ParseBaijiahaoPublishedCount(string payload) {
        try {
            var parsed=json.Deserialize<BaijiahaoWorksReply>(payload);
            return parsed!=null && parsed.errno==0 && parsed.data!=null && parsed.data.page!=null &&
                parsed.data.page.totalCount>=0?parsed.data.page.totalCount.Value.ToString(CultureInfo.InvariantCulture):null;
        } catch(ArgumentException) { return null; }
        catch(InvalidOperationException) { return null; }
        catch(FormatException) { return null; }
        catch(OverflowException) { return null; }
    }
}
