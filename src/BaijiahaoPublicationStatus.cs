using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class BaijiahaoPublicationReply {
        public int? errno { get; set; }
        public BaijiahaoPublicationData data { get; set; }
    }
    sealed class BaijiahaoPublicationData {
        public BaijiahaoPublicationRow[] list { get; set; }
        public BaijiahaoWorksPage page { get; set; }
    }
    sealed class BaijiahaoPublicationRow {
        public string article_id { get; set; }
        public string type { get; set; }
        public string status { get; set; }
        public string url { get; set; }
    }

    async Task<CreatorSubmission> ReadBaijiahaoPublication(WebView2 view,string id) {
        if(!await NavigateCreatorPublicationList(view,BaijiahaoWorksUrl))return null;
        const string list="https://baijiahao.baidu.com/pcui/article/lists?currentPage=";
        const string filters="&pageSize=10&search=&type=news&collection=&startDate=&endDate=&clearBeforeFetch=false&dynamic=1";
        for(int page=1;page<=5;page++) {
            long? total=null;
            var matched=await ReadCreatorPublicationResponse(view,new Uri(list+page+filters),body=>{
                var reply=ReadBaijiahaoPublicationList(body);
                if(reply!=null && reply.page!=null)total=reply.page.totalCount;
                return BaijiahaoPublicationOutcome(id,reply);
            });
            if(matched!=null)return matched;
            if(!total.HasValue || total<=page*10)break;
        }
        return null;
    }

    BaijiahaoPublicationData ReadBaijiahaoPublicationList(string payload) {
        try {
            var reply=json.Deserialize<BaijiahaoPublicationReply>(payload);
            return reply!=null && reply.errno==0 && reply.data!=null && reply.data.list!=null?reply.data:null;
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }

    CreatorSubmission BaijiahaoPublicationOutcome(string id,BaijiahaoPublicationData reply) {
        if(reply==null)return null;
        var rows=reply.list.Where(row=>row!=null && row.type=="news" && row.article_id==id).Take(2).ToArray();
        if(rows.Length!=1)return null;
        var article=rows[0];
        switch(article.status) {
            case "draft": return new CreatorSubmission {state="draft"};
            case "publish":
                string address=BaijiahaoPublicAddress(article.url,id);
                return address!=null?new CreatorSubmission {state="published",url=address}:
                    new CreatorSubmission {state="submitted",message="百家号后台显示已发布，暂未提供可核实的文章链接。"};
            case "audit": return new CreatorSubmission {state="review",message="百家号作品管理显示该稿件正在审核。"};
            case "rejected": return new CreatorSubmission {state="failed",message="百家号作品管理显示该稿件未通过审核。"};
            case "withdraw": return new CreatorSubmission {state="blocked",message="百家号作品管理显示该稿件已撤回。"};
            case "pre_publish": return new CreatorSubmission {state="submitted",message="百家号作品管理显示该稿件待发布。"};
            default: return null;
        }
    }

    static string BaijiahaoPublicAddress(string address,string id) {
        Uri url;
        if(!Uri.TryCreate(address,UriKind.Absolute,out url) || (url.Scheme!="http" && url.Scheme!="https") ||
            url.Host!="baijiahao.baidu.com" || !url.IsDefaultPort || url.UserInfo.Length!=0 || url.Fragment.Length!=0 ||
            url.AbsolutePath!="/s" || url.Query!="?id="+id)return null;
        return "https://baijiahao.baidu.com/s?id="+id;
    }
}
