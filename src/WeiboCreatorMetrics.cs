using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string WeiboCreatorCenter="https://me.weibo.com";

    Task<CreatorMetrics> ReadWeiboArticleCount(WebView2 view) {
        return ReadCreatorMetricsPage(view,WeiboCreatorCenter+"/content/article",WeiboPublishedCountScript);
    }

    async Task<CreatorMetrics> ReadWeiboReading(WebView2 view) {
        var result=await ReadCreatorMetricsPage(view,WeiboCreatorCenter+"/data/status",WeiboReadingScript);
        return result!=null && result.state=="unavailable" && result.readsStatus==null?
            new CreatorMetrics {readsStatus="平台未提供该账号数据"}:result;
    }

    async Task<CreatorMetrics> ReadWeiboPlaying(WebView2 view) {
        var result=await ReadCreatorMetricsPage(view,WeiboCreatorCenter+"/data/video",WeiboPlayingScript);
        return result!=null && result.state=="unavailable" && result.playsStatus==null?
            new CreatorMetrics {playsStatus="平台未提供该账号数据"}:result;
    }

    // ArticleManage uses totalnum from its own published list. For an empty list
    // Weibo omits totalnum, so require the successful response and the end marker.
    const string WeiboPublishedCountScript=@"(() => {
        if(location.origin!=='https://me.weibo.com'||location.pathname!=='/content/article'||location.search||location.hash)return null;
        const active=[...document.querySelectorAll('.woo-tab-active')].filter(e=>e.getClientRects().length&&e.textContent.trim()==='已发布');
        if(active.length!==1)return null;
        if(!window.__zhigaoWeiboPublished){
            const request={complete:false,ok:false,total:null};
            window.__zhigaoWeiboPublished=request;
            fetch('/api/article/artlist?page=1',{credentials:'same-origin',cache:'no-store'})
                .then(response=>response.ok?response.json():null).then(body=>{
                    if(body?.ok!==1)return;
                    request.ok=true;
                    const total=body.data?.totalnum;
                    if((typeof total==='number'||typeof total==='string')&&/^(?:0|[1-9]\d*)$/.test(String(total))&&
                        Number.isSafeInteger(Number(total)))request.total=String(total);
                }).catch(()=>{}).finally(()=>{request.complete=true;});
        }
        const result=window.__zhigaoWeiboPublished;
        if(!result.complete)return null;
        if(!result.ok)return {state:'unavailable'};
        if(result.total!==null)return {articleCount:result.total};
        const list=document.querySelector('._list_103bb_2');
        if(!list||list.querySelector('._item_103bb_10'))return null;
        const end=[...list.querySelectorAll('div,span,p')].find(e=>
            e.getClientRects().length&&!e.childElementCount&&e.textContent.trim()==='没有更多内容了');
        return end?{articleCount:'0'}:null;
    })()";

    const string WeiboDailyMetricScript=@"((route,key,labels) => {
        if(location.origin!=='https://me.weibo.com'||location.pathname!==route||location.search||location.hash)return null;
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]');
        const leaves=[...document.querySelectorAll('div,span,p,label,h3')].filter(e=>!e.childElementCount&&visible(e));
        if(leaves.some(e=>e.textContent.trim().startsWith('以下为单条博文分析示例')))
            return {state:'unavailable',[key+'Status']:'官方页面仅展示示例'};
        if(leaves.some(e=>/^(?:您未开通视频号|这里还没有内容|暂无数据)/.test(e.textContent.trim())))
            return {state:'unavailable'};
        const number=/^(?:\d+(?:[,，]\d{3})*|\d+(?:\.\d+)?\s*[万亿])$/;
        const cards=leaves.filter(e=>labels.includes(e.textContent.trim()));
        if(cards.length!==1)return null;
        const neighbors=[...cards[0].parentElement.children].filter(e=>e!==cards[0]&&visible(e))
            .map(e=>e.innerText.trim()).filter(value=>number.test(value));
        return neighbors.length===1?{[key]:neighbors[0]}:null;
    })";

    const string WeiboReadingScript=WeiboDailyMetricScript+"('/data/status','reads',['昨日阅读数','昨日阅读量','昨日博文阅读数','昨日博文阅读量'])";
    const string WeiboPlayingScript=WeiboDailyMetricScript+"('/data/video','plays',['昨日播放量','昨日播放数','昨日视频播放量','昨日视频播放数'])";
}
