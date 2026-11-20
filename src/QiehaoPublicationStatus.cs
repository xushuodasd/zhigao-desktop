using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class QiehaoPublicationList {
        public QiehaoDraftStatus response { get; set; }
        public QiehaoPublicationRows data { get; set; }
    }

    sealed class QiehaoPublicationRows {
        public QiehaoPublicationRow[] articles { get; set; }
    }

    sealed class QiehaoPublicationRow {
        public string article_id { get; set; }
        public string status { get; set; }
        public string url { get; set; }
        public string publishFailedShow { get; set; }
    }

    async Task<CreatorSubmission> ReadQiehaoPublication(WebView2 view,Uri draft) {
        string id=QiehaoQueryValue(draft,"articleId");
        if(!Regex.IsMatch(id??"",@"\A[A-Za-z0-9_-]{6,80}\z") ||
            !await NavigateCreatorPublicationList(view,QiehaoManagementUrl))return null;
        var request=new Uri("https://om.qq.com/marticle/article/list?articleIds="+Uri.EscapeDataString(id));
        return await ReadCreatorPublicationResponse(view,request,
            bytes=>ParseQiehaoPublication(id,bytes));
    }

    CreatorSubmission ParseQiehaoPublication(string id,string payload) {
        try {
            var reply=json.Deserialize<QiehaoPublicationList>(payload);
            if(reply==null || reply.response==null || reply.response.code!="0" || reply.data==null || reply.data.articles==null)return null;
            if(reply.data.articles.Length!=1 || reply.data.articles[0]==null || reply.data.articles[0].article_id!=id)return null;
            var article=reply.data.articles[0];
            switch(article.status) {
                case "0": return new CreatorSubmission {state="draft"};
                case "1":
                    return IsCreatorPublicArticle(Array.Find(CreatorSites,s=>s.Id=="qiehao"),article.url)?
                        new CreatorSubmission {state="published",url=article.url}:
                        new CreatorSubmission {state="submitted",message="企鹅号后台显示已发布，暂未提供可核实的文章链接。"};
                case "2": return new CreatorSubmission {state="review",message="企鹅号内容管理显示该稿件正在审核。"};
                case "3": return new CreatorSubmission {state="failed",message="企鹅号内容管理显示该稿件未通过审核。"};
                case "4":
                case "12": return article.publishFailedShow=="1"?
                    new CreatorSubmission {state="failed",message="企鹅号内容管理显示该稿件发布失败。"}:null;
                case "13": return new CreatorSubmission {state="failed",message="企鹅号内容管理显示该稿件发布失败。"};
                default: return null;
            }
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }
}
