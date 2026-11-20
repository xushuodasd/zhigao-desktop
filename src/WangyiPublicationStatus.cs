using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class WangyiPublicationReply {
        public int? code { get; set; }
        public WangyiPublicationData data { get; set; }
    }
    sealed class WangyiPublicationData {
        public WangyiPublicationRow[] list { get; set; }
    }
    sealed class WangyiPublicationRow {
        public string articleId { get; set; }
        public int? contentType { get; set; }
        public int? contentState { get; set; }
        public string unrecomReason { get; set; }
    }

    async Task<CreatorSubmission> ReadWangyiPublication(WebView2 view,string id,string file) {
        if(!await NavigateCreatorPublicationList(view,WangyiCreatorUi+"/content-manage") ||
            !IsWangyiMetricsPage(view.Source,"/content-manage"))return null;
        string title=Path.GetFileNameWithoutExtension(file).Trim();
        string form="pageNo=1&size=10&contentType=4";
        if(title.Length>0)form+="&word="+Uri.EscapeDataString(title);
        // The creator center uses POST for its filtered, read-only work list.
        var found=await ReadCreatorPublicationResponse(view,WangyiPublicationListRequest(),body=>ParseWangyiPublication(id,body),
            form+"&contentState=-1&mergeUnPassed=false&filterState=0");
        if(found!=null || !IsWangyiMetricsPage(view.Source,"/content-manage"))return found;
        return await ReadCreatorPublicationResponse(view,WangyiPublicationListRequest(),body=>ParseWangyiPublication(id,body),form+"&contentState=0");
    }

    static Uri WangyiPublicationListRequest() {
        return new Uri(WangyiCreatorOrigin+WangyiContentListPath+"?_="+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    CreatorSubmission ParseWangyiPublication(string id,string payload) {
        try {
            var reply=json.Deserialize<WangyiPublicationReply>(payload);
            if(reply==null || (reply.code!=1 && reply.code!=200) || reply.data==null || reply.data.list==null)return null;
            var rows=reply.data.list.Where(row=>row!=null && row.articleId==id && row.contentType==1).Take(2).ToArray();
            if(rows.Length!=1)return null;
            var article=rows[0];
            switch(article.contentState) {
                case 0: return new CreatorSubmission {state="draft"};
                case 1: return new CreatorSubmission {state="review",message="网易号内容管理显示该稿件正在审核。"};
                case 2:
                case 6: return new CreatorSubmission {state="failed",message="网易号内容管理显示该稿件未通过审核。"};
                case 3:
                    if(article.unrecomReason=="该内容分发受限" || article.unrecomReason=="内容不符合社区规范，仅自己可见")
                        return new CreatorSubmission {state="blocked",message="网易号显示该稿件受分发限制，请在内容管理查看。"};
                    if(!Regex.IsMatch(id,@"\A[A-Za-z0-9]{6,80}\z"))return null;
                    return new CreatorSubmission {state="published",url="https://www.163.com/dy/article/"+id+".html"};
                case 5: return new CreatorSubmission {state="failed",message="网易号内容管理显示该稿件处理失败。"};
                case 8: return new CreatorSubmission {state="submitted",message="网易号内容管理显示该稿件待发布。"};
                default: return null;
            }
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }
}
