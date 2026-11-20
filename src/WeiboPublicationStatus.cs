using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class WeiboPublicationReply {
        public string ok { get; set; }
        public string code { get; set; }
        public WeiboPublicationData data { get; set; }
    }
    sealed class WeiboPublicationData {
        public WeiboPublicationRow[] list { get; set; }
        public long? totalnum { get; set; }
        public long? count { get; set; }
    }
    sealed class WeiboPublicationRow {
        public string id { get; set; }
        public string status { get; set; }
        public string title { get; set; }
    }

    async Task<CreatorSubmission> ReadWeiboPublication(WebView2 view,string id) {
        if(!await NavigateCreatorPublicationList(view,WeiboCreatorCenter+"/content/article?id=1"))return null;
        for(int page=1;page<=5;page++) {
            long? total=null;
            int rows=0;
            var observed=await ReadCreatorPublicationResponse(view,new Uri(WeiboCreatorCenter+"/api/article/draftlist?page="+page),body=>{
                var reply=ReadWeiboPublicationList(body,false);
                if(reply!=null) {total=reply.count ?? reply.totalnum;rows=reply.list.Length;}
                return WeiboPublicationOutcome(id,reply,false);
            });
            if(observed!=null)return observed;
            if(rows==0 || !total.HasValue || total<=rows*page)break;
        }
        return await ReadWeiboPublishedHistory(view,id);
    }

    async Task<CreatorSubmission> ReadWeiboPublishedHistory(WebView2 view,string id) {
        return WeiboPublicationOutcome(id,await ReadWeiboHistoryList(view),true);
    }

    async Task<WeiboPublicationData> ReadWeiboHistoryList(WebView2 view) {
        var core=view.CoreWebView2;
        var result=new TaskCompletionSource<WeiboPublicationData>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> received=async(sender,e)=>{
            Uri address;
            if(e.Request.Method!="GET" || e.Response.StatusCode!=200 || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out address) ||
                address.Scheme!="https" || address.Host!="card.weibo.com" || !address.IsDefaultPort || address.UserInfo.Length!=0 ||
                address.AbsolutePath!="/article/v5/aj/editor/draft/historylist")return;
            try {
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null)return;
                    using(var reader=new StreamReader(stream)) {
                        var chars=new char[1048577];
                        int length=reader.ReadBlock(chars,0,chars.Length);
                        if(length<chars.Length)result.TrySetResult(ReadWeiboPublicationList(new string(chars,0,length),true));
                    }
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.WebResourceResponseReceived+=received;
        try {
            if(!await NavigateCreatorPublicationList(view,"https://card.weibo.com/article/v5/editor#/history"))return null;
            return await Task.WhenAny(result.Task,Task.Delay(5000))==result.Task?await result.Task:null;
        } finally {core.WebResourceResponseReceived-=received;}
    }

    WeiboPublicationData ReadWeiboPublicationList(string payload,bool history) {
        try {
            var reply=json.Deserialize<WeiboPublicationReply>(payload);
            return reply!=null && (history?reply.code=="100000":reply.ok=="1") && reply.data!=null && reply.data.list!=null?reply.data:null;
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }

    static CreatorSubmission WeiboPublicationOutcome(string id,WeiboPublicationData reply,bool history) {
        if(reply==null)return null;
        var rows=reply.list.Where(row=>row!=null && row.id==id).Take(2).ToArray();
        if(rows.Length!=1)return null;
        if(history)return new CreatorSubmission {state="submitted",message="微博已将同编号草稿列入发布历史，暂未返回关联的公开文章链接。"};
        if(rows[0].status=="0")return new CreatorSubmission {state="draft"};
        if(rows[0].status=="3")return new CreatorSubmission {state="failed",message="微博显示该稿件定时发布失败。"};
        return null;
    }
}
