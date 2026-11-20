using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string SohuArticleDraftPrefix="https://mp.sohu.com/mpfe/v4/contentManagement/news/addarticle?contentStatus=2&id=";
    enum SohuDraftAccess { Unknown, Allowed, IdentityRequired }
    SohuDraftAccess sohuDraftAccess;
    bool checkingSohuDraftAccess;

    async Task<bool> ReadSohuDraftAccess(CreatorSite site,WebView2 view) {
        var address=view.Source;
        if(!site.IsEditor(address))return false;
        var response=await CreatorAction(site,view,"draft-readiness");
        if(IsDisposed || address!=view.Source || creatorSignInRequired.Contains(site.Id))return false;
        var access=response=="true"?SohuDraftAccess.Allowed:
            response=="\"sohu-verification-required\""?SohuDraftAccess.IdentityRequired:SohuDraftAccess.Unknown;
        if(access==SohuDraftAccess.Unknown)return false;
        if(sohuDraftAccess!=access) {
            sohuDraftAccess=access;
            Render();
        }
        return true;
    }

    async Task RefreshSohuDraftAccess(CreatorSite site,WebView2 view) {
        checkingSohuDraftAccess=true;
        var proof=CreatorConnectionProof(site.Id);
        Render();
        try {
            view.CoreWebView2.Navigate(site.Editor);
            await Wait(async()=>{
                if(IsDisposed || CreatorConnectionProof(site.Id)!=proof)return true;
                if(IsCreatorLoginPage(site,view.Source)) {
                    MarkCreatorSignInRequired(site);
                    return true;
                }
                return await ReadSohuDraftAccess(site,view);
            },8,"搜狐号文章权限未就绪。");
        } catch(TimeoutException) { }
        finally {
            checkingSohuDraftAccess=false;
            if(!IsDisposed)Render();
        }
    }

    sealed class SohuPublishReply {
        public int? code { get; set; }
        public bool success { get; set; }
        public object data { get; set; }
    }

    async Task<CreatorSubmission> SubmitSohuCreatorDraft(CreatorSession session,CreatorWord word,Uri draft,Action beforeSubmit) {
        var view=session.Web;
        var address=draft!=null && draft.IsAbsoluteUri?draft.AbsoluteUri:"";
        var match=Regex.Match(address,@"\A"+Regex.Escape(SohuArticleDraftPrefix)+@"([0-9]{6,32})\z");
        if(!match.Success || view.Source!=draft)
            return new CreatorSubmission {state="blocked",message="搜狐号未打开指定编号的草稿。"};
        if(word.Text.Length<200)
            return new CreatorSubmission {state="blocked",message="搜狐号的短文可能需要另行确认，请在官方编辑器处理。"};
        var mismatch=CreatorMismatch(word,await ReadCreatorSnapshot(session.Site,view));
        if(mismatch!=null)return new CreatorSubmission {state="blocked",message=mismatch};
        string action=SohuPublishControls+"("+json.Serialize(address)+",";
        string ready=json.Deserialize<string>(await view.ExecuteScriptAsync(action+"false)"));
        if(ready!="ready")return new CreatorSubmission {state="blocked",message=SohuPublishBlockReason(ready)};

        var core=view.CoreWebView2;
        var receipt=new TaskCompletionSource<CreatorSubmission>();
        bool clicked=false;
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> onResponse=async(sender,e)=>{
            Uri request;
            if(!clicked || e.Request.Method!="POST" || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out request) ||
                request.Scheme!="https" || request.Host!="mp.sohu.com" || !request.IsDefaultPort || request.UserInfo.Length!=0 ||
                request.AbsolutePath!="/mpbp/bp/news/v4/news/publish/v2")return;
            if(e.Response.StatusCode<200 || e.Response.StatusCode>=300) {
                receipt.TrySetResult(new CreatorSubmission {state="uncertain",message="搜狐号未返回成功响应，请在内容管理核实。"});
                return;
            }
            try {
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null)return;
                    using(var reader=new StreamReader(stream)) {
                        var buffer=new char[16385];
                        int size=await Task.Run(()=>reader.ReadBlock(buffer,0,buffer.Length));
                        if(size<buffer.Length)receipt.TrySetResult(ParseSohuPublishReceipt(match.Groups[1].Value,new string(buffer,0,size)));
                    }
                }
            } catch(InvalidOperationException) { }
            catch(IOException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.WebResourceResponseReceived+=onResponse;
        try {
            beforeSubmit();
            clicked=true;
            if(json.Deserialize<string>(await view.ExecuteScriptAsync(action+"true)"))!="clicked")
                return new CreatorSubmission {state="uncertain",message="搜狐号发布控件已变化，请在内容管理核实。"};
            var outcome=await Task.WhenAny(receipt.Task,Task.Delay(15000));
            if(outcome==receipt.Task)return await receipt.Task;
            return new CreatorSubmission {state="uncertain",message="搜狐号尚未返回同编号回执，请在官方页面检查发布提示。"};
        } finally {
            core.WebResourceResponseReceived-=onResponse;
        }
    }

    static string SohuPublishBlockReason(string reason) {
        switch(reason) {
            case "verification": return "搜狐号要求完成实名认证，发布按钮目前不可用。";
            case "declaration": return "请先在搜狐号选择真实适用的必选创作声明，再保存草稿。";
            case "category": return "请先在搜狐号选择频道分类，再保存草稿。";
            case "dialog": return "请先在搜狐号处理当前发布对话框。";
            default: return "搜狐号未显示唯一可用的「发布」按钮，请检查编辑器。";
        }
    }

    CreatorSubmission ParseSohuPublishReceipt(string expectedId,string payload) {
        SohuPublishReply reply=null;
        try { reply=json.Deserialize<SohuPublishReply>(payload); }
        catch(ArgumentException) { }
        catch(InvalidOperationException) { }
        catch(FormatException) { }
        catch(OverflowException) { }
        if(reply!=null && reply.success && reply.code==2000000) {
            if(reply.data is string || reply.data is int || reply.data is long) {
                if(Convert.ToString(reply.data,CultureInfo.InvariantCulture)==expectedId)
                    return new CreatorSubmission {state="submitted",message="搜狐号已接收同编号文章，审核和公开状态以内容管理为准。"};
            } else if(reply.data is System.Collections.IDictionary) {
                return new CreatorSubmission {state="uncertain",message="搜狐号返回需处理的原创校验信息，请在官方页面查看。"};
            }
        }
        return new CreatorSubmission {state="uncertain",message="搜狐号未提供同编号的成功回执，请在内容管理核实。"};
    }

    const string SohuPublishControls=@"((address,click) => {
        if(location.href!==address || location.origin!=='https://mp.sohu.com' ||
            location.pathname!=='/mpfe/v4/contentManagement/news/addarticle')return 'route';
        const visible=e=>e.getClientRects().length && getComputedStyle(e).visibility!=='hidden' &&
            !e.closest('[hidden],[aria-hidden=true]');
        const select=(root,selector)=>[...root.querySelectorAll(selector)].filter(visible);
        if(select(document,'ul.button-list > li.rest-number')
            .some(e=>e.innerText.includes('未实名暂无法发布文章')))return 'verification';
        if(select(document,'.win-mask,.el-dialog,[role=dialog]').length)return 'dialog';
        const declarations=select(document,'#info-source-signature .option-item:not(.additional-item) .info-radio');
        if(declarations.length!==1 || declarations[0].querySelectorAll('input[type=radio]:checked').length!==1)
            return 'declaration';
        const categories=select(document,'#channel-select');
        if(categories.length>1)return 'category';
        if(categories.length===1) {
            const choices=select(categories[0],'.plugin-dropdown .select-main > .select-text');
            if(choices.length!==2 || choices.some(e=>e.classList.contains('placeholder') || !e.textContent.trim()))return 'category';
        }
        const buttons=select(document,'ul.button-list > li.publish-report-btn.active.positive-button').filter(e=>{
            if(e.classList.contains('disable-button') || e.getAttribute('aria-disabled')==='true' || e.innerText.trim()!=='发布')return false;
            try {
                const report=JSON.parse(e.getAttribute('report-attr'));
                return report?.type==='button' && report.code==='content-button-commit';
            } catch { return false; }
        });
        if(buttons.length!==1)return 'button';
        if(!click)return 'ready';
        buttons[0].click();
        return 'clicked';
    })";
}
