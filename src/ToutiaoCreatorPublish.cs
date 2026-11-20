using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string ToutiaoPublishDraftUrl=ToutiaoCreatorOrigin+"/profile_v4/graphic/publish?pgc_id=";

    async Task<CreatorSubmission> SubmitToutiaoCreatorDraft(CreatorSession session,CreatorWord word,Uri draft,Action beforeSubmit) {
        var view=session.Web;
        var id=draft!=null && draft.IsAbsoluteUri?ToutiaoQueryValue(draft,"pgc_id"):null;
        if(!Regex.IsMatch(id??"",@"\A[0-9]{6,32}\z") || draft.AbsoluteUri!=ToutiaoPublishDraftUrl+id || view.Source!=draft)
            return new CreatorSubmission {state="blocked",message="头条号没有打开指定编号的草稿。"};

        var address=json.Serialize(draft.AbsoluteUri);
        var settings=ToutiaoPublishSettings+"("+address+",";
        string issue;
        try {
            issue=await PrepareToutiaoPublishSettings(view,settings);
        } catch(TimeoutException ex) {return new CreatorSubmission {state="blocked",message=ex.Message};}
        if(issue!="ready" && issue!="ready-no-micro")return new CreatorSubmission {state="blocked",message=issue};

        var difference=CreatorMismatch(word,await ReadCreatorSnapshot(session.Site,view));
        if(difference!=null)return new CreatorSubmission {state="blocked",message=difference};
        var button=ToutiaoPublishButton+"("+address+",";
        var initial=json.Deserialize<string>(await view.ExecuteScriptAsync(button+"null,false)"));
        if(initial==null)return new CreatorSubmission {state="blocked",message="头条号未提供唯一可用的图文发布按钮。"};

        var core=view.CoreWebView2;
        using(var replies=new ToutiaoPublishReplies(core,json,id,issue=="ready-no-micro")) {
            await replies.Start();
            beforeSubmit();
            if(json.Deserialize<string>(await view.ExecuteScriptAsync(button+json.Serialize(initial)+",true)"))!=initial)
                return ToutiaoPublishUncertain("头条号发布按钮已变化，请在内容管理核实。");
            if(initial=="预览并发布") {
                var stage=await Task.WhenAny(replies.Preview.Task,replies.Public.Task,Task.Delay(12000));
                if(stage==replies.Public.Task)return await replies.Public.Task;
                if(stage!=replies.Preview.Task || !await replies.Preview.Task)
                    return ToutiaoPublishUncertain("头条号未确认预览属于同一篇草稿，请在内容管理核实。");
                bool confirmed=false;
                try {
                    await Wait(async()=>{
                        if(replies.PublicTaskObserved || replies.Public.Task.IsCompleted)return true;
                        if(json.Deserialize<string>(await view.ExecuteScriptAsync(button+"'确认发布',false)"))!="确认发布")return false;
                        var confirmedSettings=json.Deserialize<string>(await view.ExecuteScriptAsync(settings+"false)"));
                        if(confirmedSettings!="ready" && confirmedSettings!="ready-no-micro")return false;
                        if(confirmedSettings=="ready-no-micro")replies.RequireExplicitMicroOff=true;
                        difference=CreatorMismatch(word,await ReadCreatorSnapshot(session.Site,view));
                        if(difference!=null || replies.PublicTaskObserved)return true;
                        confirmed=json.Deserialize<string>(await view.ExecuteScriptAsync(button+"'确认发布',true)"))=="确认发布";
                        return true;
                    },8,"头条号没有打开可核对的发布确认界面。");
                } catch(TimeoutException ex) {return ToutiaoPublishUncertain(ex.Message+"请在内容管理核实。");}
                if(!confirmed && !replies.PublicTaskObserved)
                    return ToutiaoPublishUncertain("头条号预览或设置发生变化，请在内容管理核实。");
            }
            if(await Task.WhenAny(replies.Public.Task,Task.Delay(16000))==replies.Public.Task)return await replies.Public.Task;
            return ToutiaoPublishUncertain("头条号没有返回与该草稿匹配的发布回执，请在内容管理核实。");
        }
    }

    async Task<string> PrepareToutiaoPublishSettings(WebView2 view,string settings) {
        string result=null;
        bool directCoverAttempted=false;
        int coverClicks=0;
        DateTime nextCoverClick=DateTime.MinValue;
        try {
            await Wait(async()=>{
                result=json.Deserialize<string>(await view.ExecuteScriptAsync(settings+
                    (directCoverAttempted?"true,false)":"true,true)")));
                if(result=="changed-cover") {
                    directCoverAttempted=true;
                    nextCoverClick=DateTime.UtcNow.AddMilliseconds(750);
                    return false;
                }
                var point=Regex.Match(result??"",@"\Achoose-no-cover@([0-9]+),([0-9]+)\z");
                if(point.Success) {
                    if(coverClicks<3 && DateTime.UtcNow>=nextCoverClick) {
                        coverClicks++;
                        nextCoverClick=DateTime.UtcNow.AddMilliseconds(750);
                        int x=Int32.Parse(point.Groups[1].Value,CultureInfo.InvariantCulture);
                        int y=Int32.Parse(point.Groups[2].Value,CultureInfo.InvariantCulture);
                        var core=view.CoreWebView2;
                        foreach(var type in new[]{"mousePressed","mouseReleased"})
                            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                                json.Serialize(new {type,x,y,button="left",clickCount=1}));
                    }
                    return false;
                }
                return result!=null && result!="changed" && !result.StartsWith("waiting",StringComparison.Ordinal);
            },8,"头条号发布设置未就绪。请在草稿查看封面和发布选项。");
        } catch(TimeoutException) {
            if(result=="waiting-dialog")return "头条号编辑器有待处理的弹窗，草稿已保留。";
            if(result=="waiting-ad")return "头条号广告选项尚未加载或未选定，草稿已保留。";
            if(result=="waiting-cover")return "头条号封面选项尚未加载或未选定，草稿已保留。";
            if(directCoverAttempted || result=="waiting-cover-control")return "头条号未确认「无封面」已选中，草稿已保留。";
            throw;
        }
        return result;
    }

    static CreatorSubmission ToutiaoPublishUncertain(string message) {
        return new CreatorSubmission {state="uncertain",message=message};
    }

    static bool IsToutiaoPublishRequest(string url,string method) {
        Uri address;
        return method=="POST" && Uri.TryCreate(url,UriKind.Absolute,out address) &&
            address.GetLeftPart(UriPartial.Path)==ToutiaoCreatorOrigin+"/mp/agw/article/publish" && address.Fragment.Length==0 && address.UserInfo.Length==0 &&
            ToutiaoQueryValue(address,"source")=="mp" && ToutiaoQueryValue(address,"type")=="article" && ToutiaoQueryValue(address,"aid")=="1231";
    }

    const string ToutiaoLocallyBlocked="已拦下无法确认稿号或同步设置的请求，请核实草稿后继续。";

    static bool CanRecheckBlockedToutiao(CreatorSite site,CreatorPublishJournal journal) {
        return site.Id=="toutiao" && journal.outcome!=null && journal.outcome.state=="uncertain" &&
            journal.outcome.message==ToutiaoLocallyBlocked && CreatorRecordDraft(site,journal.draft)!=null;
    }

    static string ToutiaoPublishMode(string form,string id) {
        if(String.IsNullOrEmpty(form) || form.Length>4194304)return null;
        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string entry in form.Split('&')) {
            int equals=entry.IndexOf('=');
            if(equals<=0)continue;
            string key=entry.Substring(0,equals);
            if(key!="save" && key!="entrance" && key!="is_app_preview" && key!="modify_gid" && key!="pgc_id" && key!="timer_status")continue;
            if(fields.ContainsKey(key))return null;
            fields.Add(key,Uri.UnescapeDataString(entry.Substring(equals+1).Replace("+"," ")));
        }
        string save, entrance, preview, modifiedId, pgcId, schedule;
        fields.TryGetValue("save",out save);
        fields.TryGetValue("entrance",out entrance);
        fields.TryGetValue("is_app_preview",out preview);
        fields.TryGetValue("modify_gid",out modifiedId);
        fields.TryGetValue("pgc_id",out pgcId);
        if((modifiedId==null && pgcId==null) || (modifiedId!=null && modifiedId!=id) || (pgcId!=null && pgcId!=id) ||
            (fields.TryGetValue("timer_status",out schedule) && schedule!="0" && schedule!=""))return null;
        if(save=="1" && entrance=="main" && String.IsNullOrEmpty(preview))return "public";
        if(save=="0" && entrance=="" && preview=="1")return "preview";
        if(save=="0" && entrance=="" && String.IsNullOrEmpty(preview))return "draft";
        return null;
    }

    sealed class ToutiaoPublishReceipt {
        public int? code {get;set;}
        public ToutiaoPublishReceiptData data {get;set;}
    }
    sealed class ToutiaoPublishReceiptData {
        public string pgc_id {get;set;}
        public string pgcId {get;set;}
    }

    static bool ToutiaoMatchesReceipt(JavaScriptSerializer serializer,string body,string id) {
        var receipt=serializer.Deserialize<ToutiaoPublishReceipt>(body);
        return receipt!=null && receipt.code==0 && receipt.data!=null && (receipt.data.pgc_id!=null || receipt.data.pgcId!=null) &&
            (receipt.data.pgc_id==null || receipt.data.pgc_id==id) && (receipt.data.pgcId==null || receipt.data.pgcId==id);
    }

    static bool ToutiaoMicroTransferOff(JavaScriptSerializer serializer,string form,bool requireExplicit) {
        if(String.IsNullOrEmpty(form) || form.Length>4194304)return false;
        var extras=Regex.Matches(form,@"(?:\A|&)extra=([^&]*)");
        if(extras.Count!=1)return extras.Count==0 && !requireExplicit;
        try {
            var values=serializer.Deserialize<Dictionary<string,object>>(Uri.UnescapeDataString(extras[0].Groups[1].Value.Replace("+"," ")));
            if(values==null)return false;
            object flag;
            return values.TryGetValue("tuwen_wtt_trans_flag",out flag)?(flag as string)=="0" || flag is int && (int)flag==0:!requireExplicit;
        } catch(ArgumentException) {return false;}
        catch(InvalidOperationException) {return false;}
        catch(FormatException) {return false;}
    }

    sealed class ToutiaoPublishReplies : IDisposable {
        readonly CoreWebView2 core;
        readonly JavaScriptSerializer serializer;
        readonly string draft;
        readonly CoreWebView2DevToolsProtocolEventReceiver requested,response,finished,paused;
        readonly Dictionary<string,WireRequest> pending=new Dictionary<string,WireRequest>();
        bool disposed;
        bool publicForwarded;
        public bool RequireExplicitMicroOff {get;set;}
        public bool PublicTaskObserved {get;private set;}
        public readonly TaskCompletionSource<bool> Preview=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<CreatorSubmission> Public=new TaskCompletionSource<CreatorSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);

        sealed class WireEvent {
            public string requestId {get;set;}
            public string networkId {get;set;}
            public WireRequest request {get;set;}
            public WireResponse response {get;set;}
        }
        sealed class WireRequest {
            public string url {get;set;}
            public string method {get;set;}
            public string postData {get;set;}
            public bool Succeeded;
        }
        sealed class WireResponse {
            public string url {get;set;}
            public int status {get;set;}
        }
        sealed class WireBody {
            public string body {get;set;}
            public string postData {get;set;}
            public bool base64Encoded {get;set;}
        }

        public ToutiaoPublishReplies(CoreWebView2 core,JavaScriptSerializer serializer,string draft,bool requireExplicitMicroOff) {
            this.core=core;
            this.serializer=serializer;
            this.draft=draft;
            RequireExplicitMicroOff=requireExplicitMicroOff;
            requested=core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            response=core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
            finished=core.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
            paused=core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
            requested.DevToolsProtocolEventReceived+=OnRequested;
            response.DevToolsProtocolEventReceived+=OnResponse;
            finished.DevToolsProtocolEventReceived+=OnFinished;
            paused.DevToolsProtocolEventReceived+=OnPaused;
        }

        public async Task Start() {
            await core.CallDevToolsProtocolMethodAsync("Network.enable","{\"maxPostDataSize\":4194304}");
            await core.CallDevToolsProtocolMethodAsync("Fetch.enable","{\"patterns\":[{\"urlPattern\":\"https://mp.toutiao.com/mp/agw/article/publish?*\",\"requestStage\":\"Request\"}]}");
        }

        WireEvent Parse(CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            if(disposed)return null;
            try {return serializer.Deserialize<WireEvent>(args.ParameterObjectAsJson);}
            catch(ArgumentException) {return null;}
            catch(InvalidOperationException) {return null;}
        }

        void OnRequested(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            var e=Parse(args);
            if(e==null || String.IsNullOrEmpty(e.requestId))return;
            pending.Remove(e.requestId);
            if(e.request==null || !IsToutiaoPublishRequest(e.request.url,e.request.method))return;
            pending[e.requestId]=e.request;
            if(ToutiaoPublishMode(e.request.postData,draft)=="public")PublicTaskObserved=true;
        }

        async void OnPaused(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            var entry=Parse(args);
            if(entry==null || String.IsNullOrEmpty(entry.requestId))return;
            bool allow=entry.request!=null && entry.request.method!="POST";
            bool publicRequest=false;
            try {
                if(entry.request!=null && IsToutiaoPublishRequest(entry.request.url,entry.request.method)) {
                    string form=entry.request.postData;
                    if(form==null && !String.IsNullOrEmpty(entry.networkId)) {
                        var data=serializer.Deserialize<WireBody>(await core.CallDevToolsProtocolMethodAsync("Network.getRequestPostData",
                            serializer.Serialize(new {requestId=entry.networkId})));
                        if(data!=null)form=data.postData;
                    }
                    var mode=ToutiaoPublishMode(form,draft);
                    publicRequest=mode=="public";
                    allow=mode=="draft" || mode=="preview" || (publicRequest && !publicForwarded && !Public.Task.IsCompleted &&
                        ToutiaoMicroTransferOff(serializer,form,RequireExplicitMicroOff));
                }
            } catch(ArgumentException) { }
            catch(InvalidOperationException) { }
            catch(FormatException) { }
            catch(System.Runtime.InteropServices.COMException) { }
            try {
                if(allow) {
                    if(publicRequest)publicForwarded=true;
                    await core.CallDevToolsProtocolMethodAsync("Fetch.continueRequest",serializer.Serialize(new {requestId=entry.requestId}));
                } else {
                    await core.CallDevToolsProtocolMethodAsync("Fetch.failRequest",serializer.Serialize(new {requestId=entry.requestId,errorReason="BlockedByClient"}));
                    if(!publicForwarded)Public.TrySetResult(ToutiaoPublishUncertain(ToutiaoLocallyBlocked));
                }
            } catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        }

        void OnResponse(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            var e=Parse(args);
            WireRequest request;
            if(e==null || e.requestId==null || !pending.TryGetValue(e.requestId,out request))return;
            request.Succeeded=e.response!=null && e.response.url==request.url && e.response.status==200;
        }

        async void OnFinished(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs args) {
            var e=Parse(args);
            WireRequest request;
            if(e==null || e.requestId==null || !pending.TryGetValue(e.requestId,out request))return;
            pending.Remove(e.requestId);
            if(!request.Succeeded)return;
            try {
                string key=serializer.Serialize(new {requestId=e.requestId});
                string form=request.postData;
                if(form==null) {
                    var data=serializer.Deserialize<WireBody>(await core.CallDevToolsProtocolMethodAsync("Network.getRequestPostData",key));
                    if(data!=null)form=data.postData;
                }
                if(disposed)return;
                var mode=ToutiaoPublishMode(form,draft);
                if(mode==null || mode=="draft")return;
                if(mode=="public")PublicTaskObserved=true;
                var reply=serializer.Deserialize<WireBody>(await core.CallDevToolsProtocolMethodAsync("Network.getResponseBody",key));
                if(disposed || reply==null || reply.body==null || reply.body.Length>262144)return;
                var body=reply.base64Encoded?Encoding.UTF8.GetString(Convert.FromBase64String(reply.body)):reply.body;
                if(!ToutiaoMatchesReceipt(serializer,body,draft))return;
                if(mode=="preview")Preview.TrySetResult(true);
                else Public.TrySetResult(new CreatorSubmission {state="submitted",message="头条号已接收同编号文章，公开状态以内容管理为准。"});
            } catch(ArgumentException) { }
            catch(InvalidOperationException) { }
            catch(FormatException) { }
            catch(OverflowException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        }

        public void Dispose() {
            disposed=true;
            requested.DevToolsProtocolEventReceived-=OnRequested;
            response.DevToolsProtocolEventReceived-=OnResponse;
            finished.DevToolsProtocolEventReceived-=OnFinished;
            paused.DevToolsProtocolEventReceived-=OnPaused;
            // The caller disposes the dedicated publication view. Keep any queued requests paused until it closes.
            pending.Clear();
        }
    }

    const string ToutiaoPublishSettings=@"((address,prepare,directCover=false) => {
        if(location.href!==address || location.origin!=='https://mp.toutiao.com')return '头条号草稿地址发生变化。';
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]');
        const text=e=>(e.innerText||'').replace(/\s+/g,' ').trim();
        const disabled=e=>e.disabled||e.classList.contains('disabled')||e.matches('.byte-checkbox-disabled,.byte-radio-disabled,[aria-disabled=true]');
        if(prepare&&[...document.querySelectorAll('[role=dialog],.byte-modal,.byte-modal-wrapper')].some(visible))
            return 'waiting-dialog';
        const cover=[...document.querySelectorAll('.article-cover-radio-group')].filter(visible);
        const ad=[...document.querySelectorAll('.article-ad-radio')].filter(visible);
        if(cover.length!==1)return 'waiting-cover';
        if(ad.length===0)return 'waiting-ad';
        const radios=[...cover[0].querySelectorAll('label.byte-radio')].filter(visible);
        const checked=e=>!!e.querySelector('.byte-radio-inner.checked');
        const current=radios.filter(checked);
        if(current.length!==1)return 'waiting-cover';
        if(ad.filter(checked).length!==1)return 'waiting-ad';
        const first=[...document.querySelectorAll('.pgc-edit-cell label.byte-checkbox')].filter(e=>visible(e)&&text(e)==='头条首发');
        const micro=[...document.querySelectorAll('.form-tuwen_wtt_trans label.byte-checkbox.item-checkbox')].filter(visible);
        const expectedMicro=[...document.querySelectorAll('.form-tuwen_wtt_trans')].filter(visible).length;
        const expectedFirst=[...document.querySelectorAll('.pgc-edit-cell')].filter(e=>visible(e)&&text(e).startsWith('声明首发')).length;
        if(first.length!==expectedFirst||micro.length!==expectedMicro||first.length>1||micro.length>1)
            return '头条号声明或同步设置需要确认。';
        const off=[...first,...micro];
        if(off.some(e=>!e.querySelector('input[type=checkbox]')))return '头条号声明或同步设置需要确认。';
        const switchedOn=off.filter(e=>e.querySelector('input[type=checkbox]').checked||e.classList.contains('byte-checkbox-checked'));
        const unavailableMicro=micro.length===1&&switchedOn.includes(micro[0])&&micro[0].classList.contains('byte-checkbox-disabled')&&
            micro[0].querySelector('input[type=checkbox]').disabled;
        if(unavailableMicro)switchedOn.splice(switchedOn.indexOf(micro[0]),1);
        const requiresOff=switchedOn.find(e=>!prepare||disabled(e)||disabled(e.querySelector('input')))||switchedOn[0];
        if(requiresOff) {
            const firstOnly=requiresOff===first[0];
            if(!prepare||disabled(requiresOff)||disabled(requiresOff.querySelector('input')))
                return firstOnly?'头条号「头条首发」仍开启，请到草稿关闭后再试。':'头条号「同时发布微头条」仍开启，请到草稿关闭后再试。';
            requiresOff.querySelector('input').click();
            return 'changed';
        }
        const coverRoot=cover[0].closest('.article-cover');
        if(!coverRoot)return '头条号封面状态需要确认。';
        const chosen=text(current[0]);
        if(chosen==='无封面')return micro.length?'ready-no-micro':'ready';
        const frames=[...coverRoot.querySelectorAll('.article-cover-images img')].filter(e=>visible(e)&&e.currentSrc);
        const placeholders=[...coverRoot.querySelectorAll('.article-cover-images .article-cover-add')].filter(visible);
        const required=chosen==='单图'?1:chosen==='三图'?3:0;
        if(required>0&&frames.length===required&&placeholders.length===0)return micro.length?'ready-no-micro':'ready';
        const fallback=radios.filter(e=>text(e)==='无封面');
        if(!prepare||required===0||frames.length!==0||placeholders.length!==required)
            return '头条号封面尚未完整，请到草稿补齐后再试。';
        const input=fallback.length===1?fallback[0].querySelector('input[type=radio]'):null;
        if(!input||disabled(fallback[0])||disabled(input))
            return '头条号本稿无法使用「无封面」，请到草稿选择封面。';
        const control=fallback[0].querySelector('.byte-radio-inner');
        if(!control)return 'waiting-cover-control';
        if(directCover) {
            control.click();
            return 'changed-cover';
        }
        control.scrollIntoView({block:'center',inline:'nearest'});
        const bounds=control.getBoundingClientRect();
        const x=Math.round(bounds.left+bounds.width/2),y=Math.round(bounds.top+bounds.height/2);
        const reached=document.elementFromPoint(x,y);
        if(!reached||!fallback[0].contains(reached))return 'waiting-cover-control';
        return 'choose-no-cover@'+x+','+y;
    })";

    const string ToutiaoPublishButton=@"((address,expected,click) => {
        if(location.href!==address || location.origin!=='https://mp.toutiao.com')return null;
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]');
        if([...document.querySelectorAll('[role=dialog],.byte-modal,.byte-modal-wrapper')].some(visible))return null;
        const preview=[...document.querySelectorAll('.preview-article-wrap')].filter(visible);
        const canConfirm=expected==='确认发布';
        if(canConfirm?(preview.length!==1||!preview[0].querySelector('.preview-article-wrapper')):preview.length!==0)return null;
        const targets=[...document.querySelectorAll('.garr-footer-publish-content.publish-footer-content button.publish-btn.publish-btn-last.byte-btn-primary')]
            .filter(e=>visible(e)&&!e.disabled&&!e.matches('.byte-btn-loading,.byte-btn-disabled,[aria-disabled=true]'));
        if(targets.length!==1)return null;
        const target=targets[0];
        const label=target.textContent.trim();
        if(expected!==null?label!==expected:!['预览并发布','发布'].includes(label))return null;
        if(click)target.click();
        return label;
    })";
}
