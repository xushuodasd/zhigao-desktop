using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class ToutiaoPublicationFeed {
        public int? errno { get; set; }
        public ToutiaoPublicationRow[] data { get; set; }
    }
    sealed class ToutiaoPublicationRow {
        public string content { get; set; }
        public ToutiaoPublicationAttributes article_attr { get; set; }
        public ToutiaoPublicationAttributes articleAttr { get; set; }
        public ToutiaoAssembledRow assembleCell { get; set; }
    }
    sealed class ToutiaoAssembledRow {
        public ToutiaoAssembledItem itemCell { get; set; }
    }
    sealed class ToutiaoAssembledItem {
        public ToutiaoAssembledExtra extra { get; set; }
    }
    sealed class ToutiaoAssembledExtra {
        public string origin_content { get; set; }
    }
    sealed class ToutiaoPublicationAttributes {
        public string gid { get; set; }
        public string status { get; set; }
        public string pgc_cell { get; set; }
        public string pgcCell { get; set; }
    }
    sealed class ToutiaoPublicationCell {
        public string pgc_id { get; set; }
        public string article_url { get; set; }
    }
    sealed class ToutiaoDraftState {
        public int? code { get; set; }
        public int? err_no { get; set; }
        public string message { get; set; }
        public string reason { get; set; }
        public string pgc_id { get; set; }
        public bool? is_draft { get; set; }
    }

    async Task<CreatorSubmission> ReadToutiaoPublication(WebView2 view,Uri draft,string file) {
        string id=ToutiaoQueryValue(draft,"pgc_id");
        string keyword=Path.GetFileNameWithoutExtension(file).Trim();
        if(!Regex.IsMatch(id??"",@"\A[0-9]{6,32}\z") || keyword.Length==0)return null;
        var initial=new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result=new TaskCompletionSource<CreatorSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        var core=view.CoreWebView2;
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> handler=async (sender,e)=>{
            Uri request;
            if(e.Request.Method!="GET" || e.Response.StatusCode!=200 || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out request) ||
                !IsToutiaoMetricResponse(request,null,"0"))return;
            try {
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null)return;
                    using(var reader=new StreamReader(stream)) {
                        var chars=new char[1048577];
                        int length=reader.ReadBlock(chars,0,chars.Length);
                        if(length>=chars.Length)return;
                        result.TrySetResult(ParseToutiaoPublication(id,new string(chars,0,length)));
                        initial.TrySetResult(request);
                    }
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.WebResourceResponseReceived+=handler;
        try {
            if(!await NavigateCreatorPublicationList(view,ToutiaoCreatorOrigin+ToutiaoArticleManagement))return null;
            await Task.WhenAny(initial.Task,Task.Delay(5000));
            if(result.Task.IsCompleted && await result.Task!=null)return await result.Task;
        } finally { core.WebResourceResponseReceived-=handler; }
        if(initial.Task.IsCompleted) {
            var observed=await initial.Task;
            var search=new UriBuilder(observed) {Query=Regex.Replace(observed.Query.Substring(1),@"(^|&)keyword=(?=&|$)",
                match=>match.Value+Uri.EscapeDataString(keyword))};
            if(IsToutiaoMetricResponse(search.Uri,null,"0",keyword)) {
                var found=await ReadCreatorPublicationResponse(view,search.Uri,body=>ParseToutiaoPublication(id,body));
                if(found!=null)return found;
            }
        }
        var address=new Uri(ToutiaoCreatorOrigin+"/mp/agw/article/edit?pgc_id="+id+"&wxstyle=0&format=json");
        return await ReadCreatorPublicationResponse(view,address,body=>ParseToutiaoDraftState(id,body));
    }

    CreatorSubmission ParseToutiaoPublication(string expectedId,string payload) {
        try {
            var feed=json.Deserialize<ToutiaoPublicationFeed>(payload);
            if(feed==null || feed.errno!=20100 || feed.data==null)return null;
            ToutiaoPublicationAttributes article=null;
            ToutiaoPublicationCell cell=null;
            foreach(var row in feed.data) {
                var attributes=ReadToutiaoPublicationAttributes(row);
                if(attributes==null)continue;
                var content=ReadToutiaoPublicationCell(attributes.pgc_cell??attributes.pgcCell);
                if(attributes.gid!=expectedId && (content==null || content.pgc_id!=expectedId))continue;
                if(article!=null)return null;
                article=attributes;
                cell=content;
            }
            if(article==null)return null;
            switch(article.status) {
                case "1": return new CreatorSubmission {state="review",message="头条号内容管理显示该稿件正在审核。"};
                case "2":
                    return cell!=null && IsCreatorPublicArticle(CreatorSites.Single(s=>s.Id=="toutiao"),cell.article_url)?
                        new CreatorSubmission {state="published",url=cell.article_url}:
                        new CreatorSubmission {state="submitted",message="头条号后台显示已发布，暂未提供可核实的文章链接。"};
                case "3": return new CreatorSubmission {state="failed",message="头条号内容管理显示该稿件审核未通过。"};
                case "4": return new CreatorSubmission {state="blocked",message="头条号显示该稿件仅自己可见。"};
                default: return null;
            }
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }

    ToutiaoPublicationAttributes ReadToutiaoPublicationAttributes(ToutiaoPublicationRow row) {
        if(row==null)return null;
        var attributes=row.article_attr??row.articleAttr;
        if(attributes!=null)return attributes;
        string content=row.content;
        if(String.IsNullOrEmpty(content) && row.assembleCell!=null && row.assembleCell.itemCell!=null && row.assembleCell.itemCell.extra!=null)
            content=row.assembleCell.itemCell.extra.origin_content;
        if(String.IsNullOrEmpty(content))return null;
        try {
            var decoded=json.Deserialize<ToutiaoPublicationRow>(content);
            return decoded==null?null:decoded.article_attr??decoded.articleAttr;
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }

    ToutiaoPublicationCell ReadToutiaoPublicationCell(string payload) {
        if(String.IsNullOrEmpty(payload))return null;
        try { return json.Deserialize<ToutiaoPublicationCell>(payload); }
        catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }

    CreatorSubmission ParseToutiaoDraftState(string expectedId,string payload) {
        try {
            var state=json.Deserialize<ToutiaoDraftState>(payload);
            // This response is requested without redirects for one exact pgc_id.
            if(state!=null && (state.pgc_id==null || state.pgc_id==expectedId) && state.code==100001 && state.err_no==100001 &&
                state.message=="审核中，无法修改" && state.reason==state.message)
                return new CreatorSubmission {state="review",message="头条号确认该稿件正在审核。"};
            return state!=null && state.pgc_id==expectedId && state.is_draft==true?
                new CreatorSubmission {state="draft"}:null;
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }

}
