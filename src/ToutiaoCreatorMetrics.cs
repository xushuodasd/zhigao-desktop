using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string ToutiaoCreatorOrigin="https://mp.toutiao.com";
    const string ToutiaoArticleManagement="/profile_v4/graphic/articles";
    const string ToutiaoTrafficBase="/profile_v4/analysis/works-overall/";
    const string ToutiaoYesterdayPending="pending-yesterday";

    sealed class ToutiaoArticleFeed {
        public int? errno {get;set;}
        public object[] data {get;set;}
        public ToutiaoArticleFeedInfo api_base_info {get;set;}
    }
    sealed class ToutiaoArticleFeedInfo {
        public string app_extra_params {get;set;}
    }
    sealed class ToutiaoArticleFeedCount {
        public long? total_count {get;set;}
    }
    sealed class ToutiaoArticleFeedFilter {
        public string category {get;set;}
        public string page_index {get;set;}
        public string status {get;set;}
        public string source {get;set;}
        public string start_cursor_ms {get;set;}
        public string end_cursor_ms {get;set;}
    }
    sealed class ToutiaoTrafficResponse {
        public int? code {get;set;}
        public ToutiaoAuthorStat author_stat {get;set;}
    }
    sealed class ToutiaoAuthorStat {
        public ToutiaoConsumption consume_data {get;set;}
    }
    sealed class ToutiaoConsumption {
        public long? go_detail_count {get;set;}
        public long? play_count {get;set;}
    }

    async Task<CreatorMetrics> ReadToutiaoArticleCount(WebView2 view) {
        return new CreatorMetrics {articleCount=await ReadToutiaoMetric(view,ToutiaoArticleManagement,null,"articleCount")};
    }

    Task<CreatorMetrics> ReadToutiaoReading(WebView2 view) { return ReadToutiaoTraffic(view,"article","1","reads"); }
    Task<CreatorMetrics> ReadToutiaoPlaying(WebView2 view) { return ReadToutiaoTraffic(view,"video","2","plays"); }

    async Task<CreatorMetrics> ReadToutiaoTraffic(WebView2 view,string section,string type,string metric) {
        var value=await ReadToutiaoMetric(view,ToutiaoTrafficBase+section,type,metric);
        var status=value==ToutiaoYesterdayPending?"平台昨日数据待更新（14:00 前）":null;
        return metric=="reads"?new CreatorMetrics {reads=CreatorMetricNumber(value),readsStatus=status}
            :new CreatorMetrics {plays=CreatorMetricNumber(value),playsStatus=status};
    }

    async Task<string> ReadToutiaoMetric(WebView2 view,string path,string trafficType,string metric) {
        var core=view.CoreWebView2;
        var loaded=new TaskCompletionSource<bool>();
        var initialCount=new TaskCompletionSource<string>();
        var received=new TaskCompletionSource<string>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> onLoad=(sender,e)=>
            loaded.TrySetResult(e.IsSuccess && e.HttpStatusCode>=200 && e.HttpStatusCode<300);
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> onResponse=async (sender,e)=>{
            Uri address;
            if(e.Request.Method!="GET" || e.Response.StatusCode!=200 || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out address))return;
            bool initialArticleFeed=trafficType==null && IsToutiaoMetricResponse(address,null,"0");
            if(!initialArticleFeed && !IsToutiaoMetricResponse(address,trafficType))return;
            try {
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null)return;
                    using(var reader=new StreamReader(stream)) {
                        var buffer=new char[524289];
                        int size=reader.ReadBlock(buffer,0,buffer.Length);
                        if(size>=buffer.Length)return;
                        var value=ReadToutiaoMetricResponse(new string(buffer,0,size),trafficType);
                        if(value!=null) {
                            if(initialArticleFeed)initialCount.TrySetResult(value);
                            else received.TrySetResult(value);
                        }
                    }
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.NavigationCompleted+=onLoad;
        core.WebResourceResponseReceived+=onResponse;
        try {
            core.Navigate(ToutiaoCreatorOrigin+path);
            if(await Task.WhenAny(loaded.Task,Task.Delay(8500))!=loaded.Task) {core.Stop();return null;}
            if(!await loaded.Task || view.Source==null || view.Source.GetLeftPart(UriPartial.Path)!=ToutiaoCreatorOrigin+path)
                return null;
            if(trafficType==null) {
                await Task.WhenAny(initialCount.Task,received.Task,Task.Delay(6000));
                // The platform hides status filters when the unfiltered article list is empty.
                if(initialCount.Task.IsCompleted && initialCount.Task.Result=="0")return "0";
                await Wait(async()=>await view.ExecuteScriptAsync(ToutiaoPublishedArticleFilter)=="true",6,"头条号已发布文章筛选未就绪。");
            }
            if(await Task.WhenAny(received.Task,Task.Delay(6000))!=received.Task)return null;
            if(trafficType==null)return await received.Task;
            string script=ToutiaoTrafficCard+"("+json.Serialize(path)+","+json.Serialize(metric)+")";
            string number=null;
            await Wait(async()=>{
                var reported=json.Deserialize<string>(await view.ExecuteScriptAsync(script));
                number=reported==ToutiaoYesterdayPending?reported:CreatorMetricNumber(reported);
                return number!=null;
            },5,"头条号昨日数据卡片未就绪。");
            return number;
        } catch(TimeoutException) {return null;}
        finally {
            core.NavigationCompleted-=onLoad;
            core.WebResourceResponseReceived-=onResponse;
        }
    }

    bool IsToutiaoMetricResponse(Uri address,string trafficType,string articleStatus="2",string keyword="") {
        if(address.Scheme!=Uri.UriSchemeHttps || address.Host!="mp.toutiao.com" || !address.IsDefaultPort || address.UserInfo!="")return false;
        if(trafficType!=null)
            return address.AbsolutePath=="/mp/agw/statistic/v2/content_stat" && ToutiaoQueryValue(address,"type")==trafficType;
        if(address.AbsolutePath!="/api/feed/mp_provider/v1/" || ToutiaoQueryValue(address,"provider_type")!="mp_provider" ||
            ToutiaoQueryValue(address,"category")!="mp_article" || ToutiaoQueryValue(address,"offset")!="0" ||
            ToutiaoQueryValue(address,"keyword")!=keyword)return false;
        try {
            string values=ToutiaoQueryValue(address,"client_extra_params");
            var filter=values==null?null:json.Deserialize<ToutiaoArticleFeedFilter>(values);
            return filter!=null && filter.category=="mp_article" && filter.page_index=="1" && filter.status==articleStatus && filter.source=="0" &&
                String.IsNullOrEmpty(filter.start_cursor_ms) && String.IsNullOrEmpty(filter.end_cursor_ms);
        } catch(ArgumentException) {return false;}
        catch(InvalidOperationException) {return false;}
    }

    static string ToutiaoQueryValue(Uri address,string key) {
        var matches=Regex.Matches(address.Query,@"(?:^\?|&)"+Regex.Escape(key)+@"=([^&]*)");
        return matches.Count==1?Uri.UnescapeDataString(matches[0].Groups[1].Value.Replace("+"," ")):null;
    }

    string ReadToutiaoMetricResponse(string response,string trafficType) {
        try {
            if(trafficType==null) {
                var page=json.Deserialize<ToutiaoArticleFeed>(response);
                if(page==null || page.errno!=20100 || page.data==null || page.api_base_info==null ||
                    String.IsNullOrWhiteSpace(page.api_base_info.app_extra_params))return null;
                var count=json.Deserialize<ToutiaoArticleFeedCount>(page.api_base_info.app_extra_params);
                return count!=null && count.total_count.HasValue && count.total_count.Value>=page.data.Length?
                    count.total_count.Value.ToString(CultureInfo.InvariantCulture):null;
            }
            var traffic=json.Deserialize<ToutiaoTrafficResponse>(response);
            if(traffic==null || traffic.code!=0 || traffic.author_stat==null || traffic.author_stat.consume_data==null)return null;
            long? value=trafficType=="1"?traffic.author_stat.consume_data.go_detail_count:traffic.author_stat.consume_data.play_count;
            return value.HasValue && value.Value>=0?"ready":null;
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
        catch(FormatException) {return null;}
        catch(OverflowException) {return null;}
    }

    const string ToutiaoPublishedArticleFilter=@"(() => {
        if(location.origin!=='https://mp.toutiao.com'||location.pathname!=='/profile_v4/graphic/articles'||location.search||location.hash)return false;
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]');
        const active=[...document.querySelectorAll('.byte-tabs-header-title.active')].filter(e=>visible(e)&&e.textContent.trim()==='文章');
        const published=[...document.querySelectorAll('.articles-wrapper .filter-field .filter-item')].filter(e=>visible(e)&&e.textContent.trim()==='已发布');
        if(active.length!==1||published.length!==1)return false;
        if(published[0].classList.contains('selected')&&!published[0].classList.contains('unselected'))return true;
        if(!window.__zhigaoToutiaoPublishedFilterClicked) {
            window.__zhigaoToutiaoPublishedFilterClicked=true;
            published[0].click();
        }
        return false;
    })()";

    const string ToutiaoTrafficCard=@"((path,kind) => {
        if(location.origin!=='https://mp.toutiao.com'||location.pathname!==path||location.search||location.hash)return null;
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]');
        const isReading=kind==='reads';
        const active=[...document.querySelectorAll('.byte-tabs-header-title.active')].filter(e=>visible(e)&&e.textContent.trim()===(isReading?'文章':'视频'));
        if(active.length!==1)return null;
        const lines=(document.body?.innerText||'').split(/\r?\n/);
        if(lines.some(line=>/^核心数据当前为前日数据[，,]\s*14:00\s*前更新昨日数据$/.test(line.trim())))return 'pending-yesterday';
        const cards=[...document.querySelectorAll('.board-item.data-board-item > .data-board-item-title')].filter(e=>
            visible(e)&&e.textContent.trim()===(isReading?'昨日阅读量':'昨日播放量'));
        if(cards.length!==1||!cards[0].nextElementSibling||!visible(cards[0].nextElementSibling))return null;
        return cards[0].nextElementSibling.textContent.trim();
    })";
}
