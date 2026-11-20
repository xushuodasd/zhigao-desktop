using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string QiehaoArticleEditor="https://om.qq.com/main/creation/article?articleId=";
    const string QiehaoNewEditor="https://om.qq.com/main/creation/article";

    static async Task IsolateQiehaoNewDraftCache(WebView2 view) {
        var core=view.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(QiehaoPrivateEditorCache);
        core.AddWebResourceRequestedFilter("https://om.qq.com/editorCache/*",CoreWebView2WebResourceContext.All);
        core.WebResourceRequested+=(sender,e)=>{
            if(view.Source==null || view.Source.AbsoluteUri!=QiehaoNewEditor)return;
            Uri address;
            if(!Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out address) || address.Scheme!="https" ||
                address.Host!="om.qq.com" || !address.IsDefaultPort || address.UserInfo!="" ||
                !address.AbsolutePath.StartsWith("/editorCache/",StringComparison.Ordinal))return;
            bool restore=e.Request.Method=="GET" && address.AbsolutePath=="/editorCache/get";
            var body=restore?"{\"response\":{\"code\":0,\"msg\":\"\"},\"data\":{\"mediaid\":\"\",\"content\":\"\"}}":"{}";
            e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)),
                restore?200:409,restore?"OK":"Editor cache isolated","Content-Type: application/json\r\nCache-Control: no-store");
        };
    }

    // The unnumbered editor restores a separate local scratch draft. Preserve its
    // original cache while this WebView prepares a new article.
    const string QiehaoPrivateEditorCache=@"(() => {
        if(location.origin!=='https://om.qq.com'||location.pathname!=='/main/creation/article'||location.search||location.hash)return;
        const scratch=new Map();
        const get=Storage.prototype.getItem, set=Storage.prototype.setItem, remove=Storage.prototype.removeItem;
        const isolated=(store,key)=>store===localStorage && !location.search && !location.hash &&
            location.pathname==='/main/creation/article' && /^OM_ARTICLE_CACHE_[0-9]+$/.test(key);
        Storage.prototype.getItem=function(key){return isolated(this,key)?scratch.get(String(key))??null:get.call(this,key);};
        Storage.prototype.setItem=function(key,value){if(isolated(this,key))scratch.set(String(key),String(value));else set.call(this,key,value);};
        Storage.prototype.removeItem=function(key){if(isolated(this,key))scratch.delete(String(key));else remove.call(this,key);};
    })()";

    async Task<CreatorSubmission> SubmitQiehaoCreatorDraft(CreatorSession session,CreatorWord word,Uri draft,Action beforeSubmit) {
        string id=draft!=null && draft.IsAbsoluteUri?QiehaoQueryValue(draft,"articleId"):null;
        var view=session.Web;
        if(!Regex.IsMatch(id??"",@"\A[a-zA-Z0-9_-]{6,80}\z") ||
            draft.AbsoluteUri!=QiehaoArticleEditor+id || view.Source!=draft)
            return new CreatorSubmission {state="blocked",message="企鹅号没有打开指定编号的草稿。"};

        await RequireQiehaoDraftDeclaration(session);
        var mismatch=CreatorMismatch(word,await ReadCreatorSnapshot(session.Site,view));
        if(mismatch!=null)return new CreatorSubmission {state="blocked",message=mismatch};
        string publish=QiehaoPublishScript+"("+json.Serialize(draft.AbsoluteUri)+",";
        if(await view.ExecuteScriptAsync(publish+"false)")!="true")
            return new CreatorSubmission {state="blocked",message="企鹅号未提供唯一可用的「发布」按钮，请检查编辑器。"};

        var core=view.CoreWebView2;
        var receipt=new TaskCompletionSource<CreatorSubmission>();
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> onResponse=async (sender,e)=>{
            Uri request;
            if(e.Request.Method!="POST" || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out request) ||
                !IsQiehaoPublishReceiptRequest(request))return;
            try {
                if(e.Response.StatusCode<200 || e.Response.StatusCode>=300) {
                    receipt.TrySetResult(new CreatorSubmission {state="uncertain",message="企鹅号发布请求未返回成功响应，请在内容管理核实。"});
                    return;
                }
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null)return;
                    using(var reader=new StreamReader(stream)) {
                        var chars=new char[16385];
                        int length=reader.ReadBlock(chars,0,chars.Length);
                        if(length>=chars.Length)return;
                        receipt.TrySetResult(ParseQiehaoPublishReceipt(id,new string(chars,0,length)));
                    }
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.WebResourceResponseReceived+=onResponse;
        try {
            beforeSubmit();
            if(await view.ExecuteScriptAsync(publish+"true)")!="true")
                return new CreatorSubmission {state="uncertain",message="企鹅号发布按钮已变化，未能确认点击。"};
            CreatorSubmission outcome=null;
            try {
                await Wait(async()=>{
                    if(receipt.Task.IsCompleted) {
                        outcome=await receipt.Task;
                        return true;
                    }
                    if(view.Source==draft && await view.ExecuteScriptAsync(QiehaoPublishNeedsAttention)=="true") {
                        outcome=new CreatorSubmission {state="uncertain",message="企鹅号出现发布提醒，请到官方编辑器核实并处理。"};
                        return true;
                    }
                    return false;
                },15,"企鹅号没有返回可核对的发布回执。");
            } catch(TimeoutException ex) {
                outcome=new CreatorSubmission {state="uncertain",message=ex.Message+"请在内容管理核实。"};
            }
            return outcome;
        } finally {
            core.WebResourceResponseReceived-=onResponse;
        }
    }

    static bool IsQiehaoPublishReceiptRequest(Uri url) {
        return url.Scheme=="https" && url.Host=="om.qq.com" && url.IsDefaultPort &&
            String.IsNullOrEmpty(url.UserInfo) && url.AbsolutePath=="/marticlepublish/omPublish";
    }

    CreatorSubmission ParseQiehaoPublishReceipt(string id,string payload) {
        QiehaoDraftReceipt result=null;
        try {result=json.Deserialize<QiehaoDraftReceipt>(payload);}
        catch(ArgumentException) { }
        catch(InvalidOperationException) { }
        if(result!=null && result.response!=null && result.response.code=="0" &&
            result.data!=null && result.data.articleId==id)
            return new CreatorSubmission {state="submitted",message="企鹅号已接受同编号文章，公开状态以内容管理为准。"};
        return new CreatorSubmission {state="uncertain",message="企鹅号未确认同编号文章已提交，请在内容管理核实。"};
    }

    const string QiehaoPublishScript=@"((address,click) => {
        if (location.href!==address || location.origin!=='https://om.qq.com' ||
            location.pathname!=='/main/creation/article') return false;
        const visible=e=>e.getClientRects().length && !e.closest('[hidden],[aria-hidden=true]');
        if ([...document.querySelectorAll('.omui-dialog,[role=dialog]')].some(visible)) return false;
        const toolbar=[...document.querySelectorAll('[class*=""tool_right-cls""]')].filter(visible);
        if (toolbar.length!==1) return false;
        const buttons=[...toolbar[0].querySelectorAll('button.omui-button')].filter(visible);
        if (!['定时发布','存草稿'].every(label=>buttons.some(e=>e.textContent.trim()===label))) return false;
        const targets=buttons.filter(e=>e.classList.contains('omui-button--primary') && !e.disabled &&
            e.getAttribute('aria-disabled')!=='true' && e.textContent.trim()==='发布');
        if (targets.length!==1) return false;
        if (click) targets[0].click();
        return true;
    })";

    const string QiehaoPublishNeedsAttention=@"[...document.querySelectorAll('.omui-dialog,[role=dialog]')].some(e=>
        e.getClientRects().length && !e.closest('[hidden],[aria-hidden=true]') &&
        /发布提醒|继续发布|确定发布|确认发布/.test(e.innerText))";
}
