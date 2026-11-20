using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string QiehaoManagementUrl="https://om.qq.com/main/management/articleManage";
    const string QiehaoAnalyticsBase="https://om.qq.com/main/statistic/";

    sealed class QiehaoManagementResponse {
        public QiehaoDraftStatus response { get; set; }
        public QiehaoManagementData data { get; set; }
    }

    sealed class QiehaoManagementData {
        public int? totalNumber { get; set; }
    }

    async Task<CreatorMetrics> ReadQiehaoCreatorMetrics(WebView2 view) {
        var articleCount=await ReadQiehaoPublishedArticleCount(view);
        var reading=await ReadCreatorMetricsPage(view,QiehaoAnalyticsBase+"article",QiehaoMetricScript("article","昨日阅读量","reads"));
        var playing=await ReadCreatorMetricsPage(view,QiehaoAnalyticsBase+"video",QiehaoMetricScript("video","昨日播放量","plays"));
        var result=new CreatorMetrics {
            articleCount=articleCount, reads=reading==null?null:reading.reads, plays=playing==null?null:playing.plays,
            scope="文章数为已发布文章，阅读和播放为昨日整体数据"
        };
        if((reading!=null && reading.state=="unavailable") || (playing!=null && playing.state=="unavailable"))
            result.scope+=" · 平台昨日数据暂未更新";
        return HasCreatorMetrics(result)?result:null;
    }

    string QiehaoMetricScript(string tab,string label,string key) {
        return QiehaoTrafficScript+"("+json.Serialize(tab)+","+json.Serialize(label)+","+json.Serialize(key)+")";
    }

    const string QiehaoTrafficScript=@"((tab,label,key) => {
        if (location.origin!=='https://om.qq.com' || location.pathname!=='/main/statistic/'+tab) return null;
        const visible=e=>e.getClientRects().length && !e.closest('[hidden],[aria-hidden=true]');
        const channel=[...document.querySelectorAll('li[class*=""channelNavItemActive-""] > a')].filter(visible);
        if (channel.length!==1 || channel[0].textContent.trim()!=='整体数据') return null;
        const notices=document.body.innerText.split(/\r?\n/).map(line=>line.trim());
        if(notices.includes('昨日数据暂未更新'))return {state:'unavailable'};
        if(!notices.includes('昨日数据已更新'))return null;
        const cards=[...document.querySelectorAll('.omui-total__item')].filter(card=>
            visible(card) && card.querySelector('.omui-total__title-text')?.textContent.trim()===label);
        if (cards.length!==1) return null;
        const value=cards[0].querySelector('.omui-total__num')?.textContent.trim();
        return value && /^(?:\d+(?:[,，]\d{3})*|\d+(?:\.\d+)?\s*[万亿])$/.test(value)?{[key]:value}:null;
    })";

    async Task<string> ReadQiehaoPublishedArticleCount(WebView2 view) {
        var core=view.CoreWebView2;
        var result=new TaskCompletionSource<string>();
        var loaded=new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> onLoad=(sender,e)=>
            loaded.TrySetResult(e.IsSuccess && e.HttpStatusCode>=200 && e.HttpStatusCode<300);
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> onResponse=async (sender,e)=>{
            Uri request;
            if(e.Request.Method!="GET" || e.Response.StatusCode!=200 || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out request) ||
                !IsQiehaoPublishedCountResponse(request))return;
            try {
                using(var content=await e.Response.GetContentAsync()) {
                    if(content==null)return;
                    using(var reader=new StreamReader(content)) {
                        var chars=new char[524289];
                        int length=reader.ReadBlock(chars,0,chars.Length);
                        if(length>=chars.Length)return;
                        var value=ParseQiehaoPublishedArticleCount(new string(chars,0,length));
                        if(value!=null)result.TrySetResult(value);
                    }
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.NavigationCompleted+=onLoad;
        core.WebResourceResponseReceived+=onResponse;
        try {
            core.Navigate(QiehaoManagementUrl);
            if(await Task.WhenAny(loaded.Task,Task.Delay(8500))!=loaded.Task) {core.Stop();return null;}
            if(!await loaded.Task)return null;
            try {
                await Wait(async()=>await view.ExecuteScriptAsync(QiehaoPublishedFilters)=="true",6,"企鹅号文章筛选未就绪。");
            } catch(TimeoutException) {return null;}
            if(await Task.WhenAny(result.Task,Task.Delay(5000))!=result.Task)return null;
            return await result.Task;
        } finally {
            core.NavigationCompleted-=onLoad;
            core.WebResourceResponseReceived-=onResponse;
        }
    }

    const string QiehaoPublishedFilters=@"(() => {
        if (location.origin!=='https://om.qq.com' || location.pathname!=='/main/management/articleManage') return false;
        const control=(selector,label)=>[...document.querySelectorAll(selector)].filter(e=>
            e.getClientRects().length && e.textContent.trim()===label);
        const articles=control('[class*=""filterBar__tab-cls""]','文章');
        const published=control('[class*=""filterBar__pill-cls""]','已发布');
        if(articles.length!==1 || published.length!==1)return false;
        if(![...articles[0].classList].some(c=>c.startsWith('filterBar__tab_active-'))) {
            articles[0].click();
            return false;
        }
        if(![...published[0].classList].some(c=>c.startsWith('filterBar__pill_active-'))) {
            published[0].click();
            return false;
        }
        return true;
    })()";

    static bool IsQiehaoPublishedCountResponse(Uri url) {
        return url.Scheme=="https" && url.Host=="om.qq.com" && url.IsDefaultPort && String.IsNullOrEmpty(url.UserInfo) &&
            url.AbsolutePath=="/marticle/article/list" &&
            QiehaoQueryValue(url,"category")=="published" && QiehaoQueryValue(url,"ftype")=="0" &&
            QiehaoQueryValue(url,"index")=="1" && QiehaoQueryValue(url,"ignoreTotal")=="0" &&
            QiehaoQueryValue(url,"search")=="" && QiehaoQueryValue(url,"startDate")=="" && QiehaoQueryValue(url,"endDate")=="";
    }

    static string QiehaoQueryValue(Uri url,string key) {
        var matches=Regex.Matches(url.Query,@"(?:^\?|&)"+Regex.Escape(key)+@"=([^&]*)");
        return matches.Count==1?Uri.UnescapeDataString(matches[0].Groups[1].Value.Replace("+"," ")):null;
    }

    string ParseQiehaoPublishedArticleCount(string payload) {
        try {
            var result=json.Deserialize<QiehaoManagementResponse>(payload);
            return result!=null && result.response!=null && result.response.code=="0" &&
                result.data!=null && result.data.totalNumber.HasValue && result.data.totalNumber.Value>=0?
                result.data.totalNumber.Value.ToString(CultureInfo.InvariantCulture):null;
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
        catch(FormatException) {return null;}
        catch(OverflowException) {return null;}
    }
}

