using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    async Task<Uri> FindVerifiedWeiboDraft(CreatorSite site,CreatorWord word) {
        var address=await FindWeiboDraftByTitle(site,word.Title);
        if(address!=null)await ReadCreatorDraftBack(site,address,word,true);
        return address;
    }

    async Task<Uri> FindWeiboDraftByTitle(CreatorSite site,string title) {
        using(var view=new WebView2 {Visible=false,Width=1100,Height=730}) {
            platform.Controls.Add(view);
            await InitCreatorView(view,site,allowPopups:false);
            if(!await NavigateCreatorPublicationList(view,WeiboCreatorCenter+"/content/article?id=1") ||
                view.Source==null || view.Source.AbsoluteUri!=WeiboCreatorCenter+"/content/article?id=1")
                throw new InvalidOperationException("未能核对微博草稿箱，请检查微博会话。");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            long? total=null;
            Uri found=null;
            for(int page=1;page<=20;page++) {
                var address=new Uri(WeiboCreatorCenter+"/api/article/draftlist?page="+page.ToString(CultureInfo.InvariantCulture));
                WeiboPublicationData reply=null;
                await ReadCreatorPublicationResponse(view,address,body=>{
                    reply=ReadWeiboPublicationList(body,false);
                    return null;
                });
                if(reply==null)throw new InvalidOperationException("微博未返回可核对的草稿箱，未创建新稿。");
                long? newTotal=reply.count ?? reply.totalnum;
                if(newTotal<0 || (total.HasValue && newTotal!=total))
                    throw new InvalidOperationException("微博草稿箱在核对时发生变化，请重试。");
                total=newTotal;
                foreach(var row in reply.list) {
                    if(row==null || CreatorPublicationId(site,site.Editor+"#/draft/"+row.id)!=row.id ||
                        String.IsNullOrEmpty(row.id) || !ids.Add(row.id))
                        throw new InvalidOperationException("微博草稿箱稿件编号不完整或重复，请重试。");
                }
                var candidate=SelectWeiboDraft(site,title,reply.list);
                if(found!=null && candidate!=null)
                    throw new InvalidOperationException("微博有多篇同标题草稿，请先确认所需稿件。");
                if(candidate!=null)found=candidate;
                if(total.HasValue && ids.Count==total || !total.HasValue && reply.list.Length==0)return found;
                if(reply.list.Length==0 || total.HasValue && ids.Count>total)
                    throw new InvalidOperationException("微博草稿箱数量与返回列表不一致，请重试。");
            }
            throw new InvalidOperationException("未能核对微博的全部草稿，未创建新稿。");
        }
    }

    static Uri SelectWeiboDraft(CreatorSite site,string title,WeiboPublicationRow[] rows) {
        var matching=rows.Where(row=>row!=null && String.Equals(row.title,title,StringComparison.Ordinal)).Take(2).ToArray();
        if(matching.Length==0)return null;
        if(matching.Length>1)throw new InvalidOperationException("微博有多篇同标题草稿，请先确认所需稿件。");
        var rowMatch=matching[0];
        var address=site.Editor+"#/draft/"+rowMatch.id;
        if(rowMatch.status!="0" || CreatorPublicationId(site,address)!=rowMatch.id || String.IsNullOrEmpty(rowMatch.id))
            throw new InvalidOperationException("微博同标题稿件缺少可用的草稿编号或状态，请先在平台核实。");
        return new Uri(address);
    }

    async Task PrepareWeiboDraft(CreatorSession session) {
        var view=session.Web;
        var site=session.Site;
        string previousId=CreatorPublicationId(site,view.Source==null?null:view.Source.AbsoluteUri);
        if(previousId!=null && await WeiboDraftIsBlank(session))return;
        if(!site.IsEditor(view.Source))view.CoreWebView2.Navigate(site.Editor);
        await Wait(async()=>!session.Navigating && await view.ExecuteScriptAsync(WeiboNewArticle+"(false,true)")=="true",
            8,"微博未显示可用的「写文章」按钮。");
        if(await view.ExecuteScriptAsync(WeiboNewArticle+"(true,true)")!="true")
            throw new InvalidOperationException("微博的「写文章」入口已发生变化。");
        await Wait(async()=>{
            var address=view.Source;
            var id=CreatorPublicationId(site,address==null?null:address.AbsoluteUri);
            return id!=null && id!=previousId && await WeiboDraftIsBlank(session) &&
                view.Source!=null && address.AbsoluteUri==view.Source.AbsoluteUri;
        },12,"微博尚未打开不同编号的空白稿件。");
    }

    async Task<bool> WeiboDraftIsBlank(CreatorSession session) {
        var view=session.Web;
        var address=view.Source;
        if(session.Navigating || await CreatorAction(session.Site,view,"weibo-draft-ready")!="true")return false;
        var state=await ReadCreatorSnapshot(session.Site,view);
        return address!=null && view.Source!=null && address.AbsoluteUri==view.Source.AbsoluteUri &&
            state!=null && state.state=="editor" && state.title=="" && state.text=="" &&
            state.tables.Length==0 && state.images.Length==0 && state.lists.Length==0;
    }
}
