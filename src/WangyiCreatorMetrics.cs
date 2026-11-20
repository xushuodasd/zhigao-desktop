using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string WangyiCreatorOrigin="https://mp.163.com";
    const string WangyiCreatorUi=WangyiCreatorOrigin+"/subscribe_v4/index.html#";
    const string WangyiContentListPath="/wemedia/content/manage/list.do";
    const string WangyiArticleCountPath="/wemedia/data/content/article/count.do";
    const string WangyiVideoCountPath="/wemedia/data/content/video/count.do";

    sealed class WangyiMetricsReply {
        public int? code { get; set; }
        public WangyiMetricsData data { get; set; }
    }

    sealed class WangyiMetricsData {
        public long? total { get; set; }
        public long? totalCount { get; set; }
    }

    async Task<CreatorMetrics> ReadWangyiCreatorMetrics(WebView2 view) {
        var metrics=new CreatorMetrics {scope="已发布图文数 · 图文总阅读和视频总播放取自网易号内容数据"};
        var core=view.CoreWebView2;
        using(var watch=new CreatorBrowserReplies(core,json,IsWangyiCreatorOrigin,
            (address,method)=>address.AbsolutePath==WangyiContentListPath?method=="POST":
                method=="GET" && (address.AbsolutePath==WangyiArticleCountPath || address.AbsolutePath==WangyiVideoCountPath),
            (address,form)=>address.AbsolutePath!=WangyiContentListPath || IsWangyiPublishedArticleFilter(form),
            (address,payload)=>{
                bool isPublishedCount=address.AbsolutePath==WangyiContentListPath;
                var value=ParseWangyiMetric(payload,isPublishedCount);
                if(value==null)return;
                if(isPublishedCount)metrics.articleCount=value;
                else if(address.AbsolutePath==WangyiArticleCountPath)metrics.reads=value;
                else metrics.plays=value;
            })) {
            await watch.Start();
            core.Navigate(WangyiCreatorUi+"/content-manage");
            try {
                await Wait(async()=>{
                    if(IsWangyiLoginPage(view.Source))return true;
                    if(metrics.articleCount!=null)return true;
                    if(IsWangyiMetricsPage(view.Source,"/content-manage"))
                        await view.ExecuteScriptAsync(WangyiPublishedArticlesControls);
                    return false;
                },8,"网易号已发布图文数未就绪。");
            } catch(TimeoutException) { }

            if(!IsWangyiLoginPage(view.Source)) {
                core.Navigate(WangyiCreatorUi+"/content-data");
                var clock=System.Diagnostics.Stopwatch.StartNew();
                bool videoSelected=false;
                try {
                    await Wait(async()=>{
                        if(IsWangyiLoginPage(view.Source))return true;
                        if(metrics.reads!=null && metrics.plays!=null)return true;
                        if(!videoSelected && (metrics.reads!=null || clock.Elapsed.TotalSeconds>=4) &&
                            IsWangyiMetricsPage(view.Source,"/content-data"))
                            videoSelected=await view.ExecuteScriptAsync(WangyiVideoMetricsControls)=="true";
                        return false;
                    },10,"网易号阅读和播放指标未就绪。");
                } catch(TimeoutException) { }
            }
        }
        return HasCreatorMetrics(metrics)?metrics:null;
    }

    static bool IsWangyiCreatorOrigin(Uri address) {
        return address!=null && address.Scheme==Uri.UriSchemeHttps && address.Host=="mp.163.com" &&
            address.IsDefaultPort && address.UserInfo.Length==0;
    }

    static bool IsWangyiLoginPage(Uri address) {
        return IsWangyiCreatorOrigin(address) && address.AbsolutePath=="/login.html";
    }

    static bool IsWangyiMetricsPage(Uri address,string route) {
        return IsWangyiCreatorOrigin(address) && address.AbsolutePath=="/subscribe_v4/index.html" &&
            address.Fragment=="#"+route;
    }

    static string WangyiRequestField(string form,string key) {
        var matches=Regex.Matches(form??"",@"(?:^|&)"+Regex.Escape(key)+@"=([^&]*)");
        return matches.Count==1?Uri.UnescapeDataString(matches[0].Groups[1].Value.Replace("+"," ")):null;
    }

    static bool IsWangyiPublishedArticleFilter(string form) {
        return WangyiRequestField(form,"contentType")=="4" && WangyiRequestField(form,"contentState")=="3" &&
            WangyiRequestField(form,"filterState")=="0" && WangyiRequestField(form,"pageNo")=="1" &&
            !Regex.IsMatch(form??"",@"(?:^|&)(?:word|start|end)=[^&]+");
    }

    string ParseWangyiMetric(string payload,bool isPublishedCount) {
        try {
            var reply=json.Deserialize<WangyiMetricsReply>(payload);
            if(reply==null || reply.data==null || (reply.code!=1 && reply.code!=200))return null;
            var value=isPublishedCount?reply.data.total:reply.data.totalCount;
            return value>=0?value.Value.ToString(CultureInfo.InvariantCulture):null;
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
        catch(FormatException) {return null;}
        catch(OverflowException) {return null;}
    }

    const string WangyiPublishedArticlesControls=@"(() => {
        if(location.origin!=='https://mp.163.com'||location.pathname!=='/subscribe_v4/index.html'||location.hash!=='#/content-manage')return false;
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]')&&getComputedStyle(e).visibility!=='hidden';
        const unique=(selector,label)=>{
            const matches=[...document.querySelectorAll(selector)].filter(e=>visible(e)&&e.innerText.trim()===label);
            return matches.length===1?matches[0]:null;
        };
        const articles=unique('.src-main-page-ContentManage-index-module-content [role=tab]','图文');
        if(!articles||articles.classList.contains('ne-tab-disabled'))return false;
        if(articles.getAttribute('aria-selected')!=='true'){articles.click();return false;}
        const published=unique('.src-main-page-ContentManage-index-module-content [tabindex=""3""]','已发布');
        if(!published)return false;
        if(!published.classList.contains('active'))published.click();
        return true;
    })()";

    const string WangyiVideoMetricsControls=@"(() => {
        if(location.origin!=='https://mp.163.com'||location.pathname!=='/subscribe_v4/index.html'||location.hash!=='#/content-data')return false;
        const tabs=[...document.querySelectorAll('[role=tab]')].filter(e=>e.getClientRects().length&&
            !e.closest('[hidden],[aria-hidden=true]')&&getComputedStyle(e).visibility!=='hidden');
        const videos=tabs.filter(e=>e.innerText.trim()==='视频');
        if(videos.length!==1||!tabs.some(e=>e.innerText.trim()==='图文')||videos[0].classList.contains('ne-tab-disabled'))return false;
        if(videos[0].getAttribute('aria-selected')!=='true')videos[0].click();
        return true;
    })()";

}
