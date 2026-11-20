using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

partial class LoginWindow {
    async Task<CreatorSubmission> SubmitWeiboCreatorDraft(CreatorSession session, CreatorWord word, Uri draft, Action beforeSubmit) {
        var result=await SubmitWeiboOnce(session,word,draft,beforeSubmit);
        if(result.state!="save-required")return result;
        return await RetryWeiboAfterSaveRequest(session,word,draft,delegate {});
    }

    async Task<CreatorSubmission> RetryWeiboAfterSaveRequest(CreatorSession session,CreatorWord word,Uri draft,Action beforeRetry) {
        var site=session.Site;
        var view=session.Web;
        try {
            var confirmed=await CheckWeiboDraftBeforeRetry(site,word,draft);
            if(confirmed.state!="draft")return confirmed;
            if(view.Source==draft) {
                var loaded=new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> onLoad=(sender,e)=>loaded.TrySetResult(e.IsSuccess);
                view.CoreWebView2.NavigationCompleted+=onLoad;
                try {
                    view.CoreWebView2.Reload();
                    if(await Task.WhenAny(loaded.Task,Task.Delay(10000))!=loaded.Task || !await loaded.Task || view.Source!=draft)
                        throw new InvalidOperationException("微博未能重开同编号编辑页。");
                } finally { view.CoreWebView2.NavigationCompleted-=onLoad; }
            } else await NavigateCreatorPublishEditor(session,draft,false);
            await WaitForCreatorContent(site,view,word,9);
            var expected=await CreatorAction(site,view,"inspect");
            if(CreatorMismatch(word,json.Deserialize<CreatorSnapshot>(expected))!=null)
                throw new InvalidOperationException("原稿核对失败。");
            var saved=await SaveCreatorDraft(session,expected,delegate {});
            if(saved!=draft)throw new InvalidOperationException("微博未返回同编号的保存回执。");
            await ReadCreatorDraftBack(site,draft,word,true);
        } catch(WeiboVerificationRequired) {return WeiboSecurityChallenge();}
        catch(Exception ex) {
            return new CreatorSubmission {state="failed",message="微博明确拒绝了首次发布，重新保存未完成。"+ex.Message};
        }
        var retry=await SubmitWeiboOnce(session,word,draft,beforeRetry);
        if(retry.state=="save-required")return new CreatorSubmission {state="failed",
            message="微博在手动保存并核对原稿后再次拒绝发布（500002）。草稿已保留。"};
        return retry;
    }

    async Task<CreatorSubmission> CheckWeiboDraftBeforeRetry(CreatorSite site,CreatorWord word,Uri draft) {
        var id=CreatorPublicationId(site,draft==null?null:draft.AbsoluteUri);
        if(id!=null) {
            using(var checkView=new Microsoft.Web.WebView2.WinForms.WebView2 {Visible=false, Width=1100, Height=730}) {
                platform.Controls.Add(checkView);
                await InitCreatorView(checkView,site,allowPopups:false);
                var history=await ReadWeiboHistoryList(checkView);
                var published=WeiboPublicationOutcome(id,history,true);
                if(published!=null)return published;
                if(history!=null && history.count==history.list.Length && history.list.All(row=>row!=null && !String.IsNullOrEmpty(row.id)) &&
                    await FindVerifiedWeiboDraft(site,word)==draft)return new CreatorSubmission {state="draft"};
            }
        }
        return new CreatorSubmission {state="uncertain",message="微博未能同时核实本稿仍在草稿箱、且未进入发布历史。"};
    }

    sealed class WeiboVerificationRequired : InvalidOperationException {
        public WeiboVerificationRequired():base(WeiboSecurityChallenge().message) {}
    }

    async Task<CreatorSubmission> SubmitWeiboOnce(CreatorSession session, CreatorWord word, Uri draft, Action beforeSubmit) {
        var view=session.Web;
        var match=Regex.Match(draft==null?"":draft.Fragment,@"\A#/draft/([0-9]{6,32})\z");
        if(!match.Success || GetCreatorDraftAddress(session.Site,draft)==null ||
            view.Source==null || view.Source.AbsoluteUri!=draft.AbsoluteUri)
            throw new InvalidOperationException("微博未打开刚核对的同编号草稿。");

        var snapshot=await ReadCreatorSnapshot(session.Site,view);
        string difference=CreatorMismatch(word,snapshot);
        if(difference!=null)throw new InvalidOperationException(difference);
        string draftId=match.Groups[1].Value;
        string prefix=WeiboPublishControls+"("+json.Serialize(draftId)+",";
        var saved=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result=new TaskCompletionSource<CreatorSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool committed=false;
        using(var watch=new CreatorBrowserReplies(view.CoreWebView2,json,
            address=>address.Scheme=="https" && address.Host=="card.weibo.com" && address.IsDefaultPort && address.UserInfo.Length==0,
            (address,method)=>method=="POST" && (IsWeiboNextSave(address,draftId) || committed && IsWeiboPublishReceipt(address,draftId)),
            (address,form)=>!IsWeiboNextSave(address,draftId) || IsWeiboNextForm(form),
            (address,body)=>{
                if(IsWeiboNextSave(address,draftId))saved.TrySetResult(WeiboNextSaveState(body));
                else result.TrySetResult(WeiboSubmitOutcome(body));
            })) {
            await watch.Start();
            if(await view.ExecuteScriptAsync(prefix+"'next')")!="true")
                throw new InvalidOperationException("微博未找到唯一的「下一步」，或新浪新闻同步仍处于开启状态。");
            bool continued=false;
            await Wait(async()=>{
                if(saved.Task.IsCompleted)return true;
                if(!continued && await view.ExecuteScriptAsync(prefix+"'continue-cover')")=="true")continued=true;
                return false;
            },15,"微博未确认同编号的发布前保存。草稿已保留。");
            var state=await saved.Task;
            if(state=="verification")return WeiboSecurityChallenge();
            if(state!="saved")return new CreatorSubmission {state="blocked",message="微博未确认发布前保存成功，草稿已保留。"};
            await Wait(async()=>await view.ExecuteScriptAsync(prefix+"'prepare')")=="true",12,"微博未进入公开发布确认框。");
            if(await view.ExecuteScriptAsync(prefix+"'ready')")!="true")
                throw new InvalidOperationException("微博发布设置已变化，请在工作台重新检查。");
            beforeSubmit();
            committed=true;
            if(await view.ExecuteScriptAsync(prefix+"'publish')")!="true")
                return new CreatorSubmission {state="uncertain",message="微博最终发布控件发生变化，请核实草稿状态。"};
            if(await Task.WhenAny(result.Task,Task.Delay(15000))==result.Task)return await result.Task;
            return new CreatorSubmission {state="uncertain",message="微博未返回本稿的明确成功回执，请从文章管理页核实。"};
        }
    }

    static CreatorSubmission WeiboSecurityChallenge() {
        return new CreatorSubmission {state="weibo-verification",message="微博要求本人完成安全验证。打开本稿，点「下一步」完成官网验证后，可回工作台继续发布。"};
    }

    static bool IsWeiboNextSave(Uri address,string draftId) {
        return !String.IsNullOrEmpty(draftId) && address.AbsolutePath=="/article/v5/aj/editor/draft/save" &&
            Regex.IsMatch(address.Query,@"(?:^\?|&)id="+Regex.Escape(draftId)+@"(?:&|$)") &&
            Regex.Matches(address.Query,@"(?:^\?|&)id=").Count==1;
    }

    static bool IsWeiboNextForm(string form) {
        return Regex.Matches(form??"", @"(?:^|&)action=").Count==1 && Regex.IsMatch(form??"", @"(?:^|&)action=2(?:&|$)");
    }

    string WeiboNextSaveState(string body) {
        var receipt=json.Deserialize<WeiboPublishReceipt>(body);
        var data=receipt==null?null:receipt.data as System.Collections.Generic.Dictionary<string,object>;
        if(receipt==null || receipt.code!="100000" || data==null)return "unconfirmed";
        object challenge;
        if(data.TryGetValue("geetest",out challenge) && challenge is System.Collections.Generic.Dictionary<string,object>) {
            object target;
            if(((System.Collections.Generic.Dictionary<string,object>)challenge).TryGetValue("verifyUrl",out target) &&
                !String.IsNullOrWhiteSpace(target as string))return "verification";
        }
        object length;
        int count;
        return data.TryGetValue("content_length",out length) && Int32.TryParse(Convert.ToString(length),out count) && count>0?"saved":"unconfirmed";
    }

    CreatorSubmission WeiboSubmitOutcome(string body) {
        var receipt=json.Deserialize<WeiboPublishReceipt>(body);
        if(receipt!=null && receipt.code=="100000")
            return new CreatorSubmission {state="submitted",message="微博已返回本稿提交成功，公开状态以文章管理页为准。"};
        if(receipt!=null && receipt.code=="500002" && Regex.IsMatch(receipt.msg??"", @"发布失败[，,]\s*请手动保存后重新提交"))
            return new CreatorSubmission {state="save-required"};
        return new CreatorSubmission {state="uncertain",message="微博未返回本稿的明确成功回执，请从文章管理页核实。"};
    }
    sealed class WeiboPublishReceipt {
        public string code {get;set;}
        public string msg {get;set;}
        public object data {get;set;}
    }

    static bool IsWeiboPublishReceipt(Uri request,string expectedDraft) {
        if(request.Scheme!=Uri.UriSchemeHttps || !request.IsDefaultPort || request.UserInfo!="" ||
            request.Host!="card.weibo.com" || request.AbsolutePath!="/article/v5/aj/editor/draft/publish")return false;
        var ids=Regex.Matches(request.Query,@"(?:^\?|&)id=([^&]*)");
        return ids.Count==1 && ids[0].Groups[1].Value==expectedDraft;
    }

    const string WeiboPublishControls=@"((id,action) => {
        if(location.origin!=='https://card.weibo.com'||location.pathname!=='/article/v5/editor'||location.hash!=='#/draft/'+id)return false;
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]')&&getComputedStyle(e).visibility!=='hidden';
        const controls=(root,selector)=>[...root.querySelectorAll(selector)].filter(visible);
        const text=e=>e.innerText.trim().replace(/\s+/g,' ');
        const actionable=e=>!e.disabled&&e.getAttribute('aria-disabled')!=='true';
        const button=(root,label)=>{
            const matches=controls(root,'button').filter(e=>actionable(e)&&text(e)===label);
            return matches.length===1?matches[0]:null;
        };
        const toggleState=e=>e.matches('input[type=checkbox]')?String(e.checked):e.getAttribute('aria-checked');
        const namedToggle=(root,label)=>{
            const labels=controls(root,'span,label').filter(e=>!e.childElementCount&&text(e)===label);
            if(labels.length===0)return {present:false};
            if(labels.length!==1)return {present:true};
            const selector='[role=checkbox],[role=switch],input[type=checkbox]';
            const item=labels[0].closest(selector)||labels[0].parentElement.querySelector(selector);
            return {present:true,item};
        };
        const news=namedToggle(document,'同步至新浪新闻');
        if(news.present&&(!news.item||toggleState(news.item)!=='false'))return false;
        if(action==='next') {
            const next=button(document,'下一步');
            if(!next)return false;
            next.click();
            return true;
        }
        const dialogs=controls(document,'.publish-modal');
        if(action==='continue-cover') {
            if(dialogs.length||controls(document,'[role=dialog],.n-dialog').length)return false;
            const notices=controls(document,'.n-message,[role=status],[role=alert]');
            if(!notices.some(e=>/自动为您选择了上一篇文章的封面图/.test(text(e))))return false;
            const previews=controls(document,'.cover-preview img.cover-img');
            if(previews.length!==1||!previews[0].complete||!(previews[0].naturalWidth>0)||
               !/^https:\/\//.test(previews[0].currentSrc))return false;
            const next=button(document,'下一步');
            if(!next)return false;
            next.click();
            return true;
        }
        if(dialogs.length!==1)return false;
        const dialog=dialogs[0];
        const boxes=controls(dialog,'textarea');
        if(boxes.length!==1||!boxes[0].value.trim())return false;
        if(controls(dialog,'.tools-icon.active').length ||
            controls(dialog,'span,div').filter(e=>!e.childElementCount&&text(e)==='公开').length!==1)return false;
        for(const label of ['同步到微博','关注@头条文章 第一时间获取官方通知']) {
            const choice=namedToggle(dialog,label);
            if(!choice.present)continue;
            if(!choice.item)return false;
            if(action==='prepare'&&toggleState(choice.item)==='true'&&actionable(choice.item))choice.item.click();
            if(toggleState(choice.item)!=='false')return false;
        }
        const send=button(dialog,'发布');
        if(!send)return false;
        if(action==='publish')send.click();
        return true;
    })";
}
