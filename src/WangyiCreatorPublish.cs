using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

partial class LoginWindow {
    const string WangyiPublishPath="/wemedia/article/status/api/publishV2.do";

    sealed class WangyiPublishReply {
        public int? code { get; set; }
    }

    async Task<CreatorSubmission> SubmitWangyiCreatorDraft(CreatorSession session,CreatorWord word,Uri draft,Action beforeSubmit) {
        string id=WangyiPublishDraftId(draft);
        var view=session.Web;
        if(id==null || view.Source==null || view.Source.AbsoluteUri!=draft.AbsoluteUri)
            return new CreatorSubmission {state="blocked",message="网易号没有打开指定编号的草稿。"};
        string difference=CreatorMismatch(word,await ReadCreatorSnapshot(session.Site,view));
        if(difference!=null)return new CreatorSubmission {state="blocked",message=difference};
        var controls=WangyiPublishControls+"("+json.Serialize(draft.AbsoluteUri)+",";
        if(await view.ExecuteScriptAsync(controls+"false)")!="true")
            return new CreatorSubmission {state="blocked",message="网易号发布按钮未就绪，请检查稿件设置和账号权限。"};

        var accepted=new TaskCompletionSource<bool>();
        bool committed=false;
        using(var watch=new CreatorBrowserReplies(view.CoreWebView2,json,IsWangyiCreatorOrigin,
            (address,method)=>committed && address.AbsolutePath==WangyiPublishPath && method=="POST",
            (address,form)=>IsWangyiImmediatePublish(form,id),
            (address,body)=>accepted.TrySetResult(IsWangyiPublishSuccess(body)))) {
            await watch.Start();
            beforeSubmit();
            committed=true;
            if(await view.ExecuteScriptAsync(controls+"true)")!="true")
                return new CreatorSubmission {state="uncertain",message="网易号发布按钮已变化，未能确认点击。"};
            if(await Task.WhenAny(accepted.Task,Task.Delay(15000))==accepted.Task && await accepted.Task)
                return new CreatorSubmission {state="submitted",message="网易号已接受同编号文章，公开状态以内容管理为准。"};
            return new CreatorSubmission {state="uncertain",message="网易号未确认同编号文章已提交，请在内容管理核实。"};
        }
    }

    static string WangyiPublishDraftId(Uri draft) {
        if(!IsWangyiCreatorOrigin(draft) || draft.AbsolutePath!="/subscribe_v4/index.html")return null;
        var match=Regex.Match(draft.Fragment,@"\A#/article-publish/([a-zA-Z0-9_-]{6,80})\?([^#]+)\z");
        return match.Success && WangyiRequestField(match.Groups[2].Value,"option")=="editDraft"?match.Groups[1].Value:null;
    }

    static bool IsWangyiImmediatePublish(string form,string id) {
        return WangyiRequestField(form,"operation")=="publish" &&
            WangyiRequestField(form,"articleId")==id && WangyiRequestField(form,"scheduled")=="0";
    }

    bool IsWangyiPublishSuccess(string body) {
        try {
            var reply=json.Deserialize<WangyiPublishReply>(body);
            return reply!=null && reply.code==1;
        } catch(ArgumentException) {return false;}
        catch(InvalidOperationException) {return false;}
        catch(FormatException) {return false;}
        catch(OverflowException) {return false;}
    }

    const string WangyiPublishControls=@"((address,click) => {
        if(location.href!==address||location.origin!=='https://mp.163.com'||location.pathname!=='/subscribe_v4/index.html')return false;
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]')&&getComputedStyle(e).visibility!=='hidden';
        if([...document.querySelectorAll('[role=dialog],.ne-modal')].some(visible))return false;
        const toolbars=[...document.querySelectorAll('.post-footer__container-right')].filter(visible);
        if(toolbars.length!==1)return false;
        const buttons=[...toolbars[0].querySelectorAll('button.netease-button')].filter(visible);
        if(!buttons.some(e=>e.textContent.trim()==='定时发布'))return false;
        const immediate=buttons.filter(e=>e.classList.contains('primary_button')&&!e.disabled&&e.getAttribute('aria-disabled')!=='true'&&
            e.textContent.trim()==='发布');
        if(immediate.length!==1)return false;
        if(click)immediate[0].click();
        return true;
    })";
}
