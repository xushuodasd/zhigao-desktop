using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    sealed class BaijiahaoPublishReply {
        public int? errno { get; set; }
        public BaijiahaoPublishArticle ret { get; set; }
    }

    sealed class BaijiahaoPublishArticle {
        public string article_id { get; set; }
        public string nid { get; set; }
    }

    async Task<CreatorSubmission> SubmitBaijiahaoCreatorDraft(CreatorSession session,CreatorWord word,Uri draft,Action beforeSubmit) {
        var view=session.Web;
        var id=draft==null || !draft.IsAbsoluteUri?null:BaijiahaoMetricQuery(draft,"article_id");
        if(!Regex.IsMatch(id??"", @"\A[0-9]{6,32}\z") || GetCreatorDraftAddress(session.Site,draft)==null ||
            BaijiahaoMetricQuery(draft,"type")!="news" || view.Source==null || view.Source.AbsoluteUri!=draft.AbsoluteUri)
            return new CreatorSubmission {state="blocked",message="百家号没有打开指定编号的图文草稿。"};
        var snapshot=await ReadCreatorSnapshot(session.Site,view);
        var mismatch=CreatorMismatch(word,snapshot);
        if(mismatch!=null)return new CreatorSubmission {state="blocked",message=mismatch};

        string controls=BaijiahaoPublishControls+"("+json.Serialize(draft.AbsoluteUri)+",";
        string prepare=json.Deserialize<string>(await view.ExecuteScriptAsync(controls+"'prepare')"));
        bool coverPrepared=false;
        if(prepare=="cover") {
            coverPrepared=snapshot.images.Length>0?
                await PrepareBaijiahaoBodyCover(view,draft,snapshot.images[0].url):
                await PrepareBaijiahaoTitleCover(view,draft,word.Title);
        }
        if(coverPrepared) {
            var after=await ReadCreatorSnapshot(session.Site,view);
            mismatch=CreatorMismatch(word,after);
            if(mismatch!=null || (snapshot.images.Length>0 && after.images[0].url!=snapshot.images[0].url))
                return new CreatorSubmission {state="blocked",message=mismatch??"百家号封面设置期间正文图片发生变化。"};
            prepare=json.Deserialize<string>(await view.ExecuteScriptAsync(controls+"'prepare')"));
        }
        if(prepare=="cover")return new CreatorSubmission {state="blocked",message="百家号未确认草稿的单图封面上传完成，已保留草稿。"};
        if(prepare=="settling") {
            try {
                await Wait(async()=>{
                    prepare=json.Deserialize<string>(await view.ExecuteScriptAsync(controls+"'observe')"));
                    return prepare=="ready";
                },5,"百家号发布设置未同步。");
            } catch(TimeoutException) {return new CreatorSubmission {state="blocked",message="百家号未确认关闭自动生成播客，已保留草稿。"};}
        }
        if(prepare!="ready")return new CreatorSubmission {state="blocked",message="百家号发布设置或「发布」按钮未能确认，请查看草稿。"};

        var core=view.CoreWebView2;
        var receipt=new TaskCompletionSource<bool>();
        bool committed=false;
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> onResponse=async(sender,e)=>{
            Uri address;
            if(!committed || e.Request.Method!="POST" || e.Response.StatusCode<200 || e.Response.StatusCode>=300 ||
                !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out address) || !IsBaijiahaoPublishReceiptRequest(address))return;
            var requestId=BaijiahaoMetricQuery(address,"article_id");
            if(requestId==null) {
                try { requestId=ReadBaijiahaoPublishRequestId(e.Request.Content); }
                catch(IOException) { }
                catch(InvalidOperationException) { }
                catch(NotSupportedException) { }
                catch(System.Runtime.InteropServices.COMException) { }
            }
            try {
                using(var stream=await e.Response.GetContentAsync()) {
                    if(stream==null || !committed)return;
                    using(var reader=new StreamReader(stream)) {
                        var buffer=new char[16385];
                        int length=reader.ReadBlock(buffer,0,buffer.Length);
                        if(length<buffer.Length && IsBaijiahaoPublishReceipt(id,requestId,new string(buffer,0,length)))
                            receipt.TrySetResult(true);
                    }
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        };
        core.WebResourceResponseReceived+=onResponse;
        try {
            beforeSubmit();
            committed=true;
            if(await view.ExecuteScriptAsync(controls+"'publish')")!="\"clicked\"")
                return new CreatorSubmission {state="uncertain",message="百家号发布控件或设置已变化，请到平台核实。"};
            await Task.WhenAny(receipt.Task,Task.Delay(6500));
            // The editor can return a larger or newer receipt. Verify the original article
            // number against works management in a separate view while the editor stays open.
            var confirmed=await CheckBaijiahaoAfterSubmit(session.Site,id);
            if(confirmed!=null)return confirmed;
            if(receipt.Task.IsCompleted)
                return new CreatorSubmission {state="submitted",message="百家号已接收同编号稿件，审核和公开状态以作品管理页为准。"};
            return new CreatorSubmission {state="uncertain",message="百家号未返回同编号的明确发布回执，请到作品管理页核实。"};
        } finally {
            committed=false;
            core.WebResourceResponseReceived-=onResponse;
        }
    }

    async Task<CreatorSubmission> CheckBaijiahaoAfterSubmit(CreatorSite site,string id) {
        using(var verifier=new WebView2 {Visible=false,Width=1100,Height=730}) {
            try {
                platform.Controls.Add(verifier);
                verifier.SendToBack();
                await InitCreatorView(verifier,site,allowPopups:false);
                ConfigureCreatorVerificationView(verifier);
                var result=await ReadBaijiahaoPublication(verifier,id);
                if(result!=null && result.state!="draft")return result;
            } catch(InvalidOperationException) { }
            catch(TimeoutException) { }
            catch(System.Runtime.InteropServices.COMException) { }
        }
        return null;
    }

    async Task<bool> PrepareBaijiahaoBodyCover(WebView2 view,Uri draft,string image) {
        Uri source;
        if(!Uri.TryCreate(image,UriKind.Absolute,out source) || source.Scheme!="https" ||
            !source.IsDefaultPort || source.UserInfo.Length!=0)return false;
        string step=BaijiahaoBodyCoverControls+"("+json.Serialize(draft.AbsoluteUri)+","+json.Serialize(source.AbsoluteUri)+",";
        if(await view.ExecuteScriptAsync(step+"'open')")!="\"opened\"")return false;
        return await ConfirmBaijiahaoCover(view,step);
    }

    async Task<bool> PrepareBaijiahaoTitleCover(WebView2 view,Uri draft,string title) {
        string step=BaijiahaoBodyCoverControls+"("+json.Serialize(draft.AbsoluteUri)+",null,";
        if(await view.ExecuteScriptAsync(step+"'open')")!="\"opened\"")return false;
        string file=null;
        try {
            await Wait(async()=>await view.ExecuteScriptAsync(step+"'upload-control')")=="\"upload-ready\"",5,"百家号封面上传入口未就绪。");
            if(view.Source==null || view.Source.AbsoluteUri!=draft.AbsoluteUri)return false;
            var core=view.CoreWebView2;
            dynamic dom=json.DeserializeObject(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument","{}"));
            dynamic nodes=json.DeserializeObject(await core.CallDevToolsProtocolMethodAsync("DOM.querySelectorAll",
                json.Serialize(new {nodeId=dom["root"]["nodeId"],selector="input[type=file][data-zhigao-baijiahao-title-cover]"})));
            object[] ids=nodes["nodeIds"];
            if(ids.Length!=1 || view.Source==null || view.Source.AbsoluteUri!=draft.AbsoluteUri)return false;
            file=Path.Combine(Path.GetTempPath(),"zhigao-title-cover-"+Guid.NewGuid().ToString("N")+".png");
            File.WriteAllBytes(file,await Task.Run(()=>DrawCreatorTitleCover(title)));
            if(view.Source==null || view.Source.AbsoluteUri!=draft.AbsoluteUri ||
                await view.ExecuteScriptAsync(step+"'upload-control')")!="\"upload-ready\"")return false;
            await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",json.Serialize(new {nodeId=ids[0],files=new[]{file}}));
            return await ConfirmBaijiahaoCover(view,step,18);
        } catch(TimeoutException) {return false;}
        catch(IOException) {return false;}
        catch(UnauthorizedAccessException) {return false;}
        catch(System.Runtime.InteropServices.COMException) {return false;}
        finally {
            if(file!=null)try {File.Delete(file);} catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }

    async Task<bool> ConfirmBaijiahaoCover(WebView2 view,string step,int selectionTimeout=5) {
        try {
            string state=null;
            await Wait(async()=>{
                state=json.Deserialize<string>(await view.ExecuteScriptAsync(step+"'select')"));
                return state=="selected" || state=="conflict";
            },selectionTimeout,"百家号选图面板未就绪。");
            if(state!="selected")return false;
            await Wait(async()=>await view.ExecuteScriptAsync(step+"'confirm')")=="\"confirmed\"",5,"百家号封面未能确认。");
            await Wait(async()=>await view.ExecuteScriptAsync(step+"'ready')")=="\"ready\"",8,"百家号封面未取得回执。");
            return true;
        } catch(TimeoutException) {return false;}
    }

    static bool IsBaijiahaoPublishReceiptRequest(Uri url) {
        return url.Scheme=="https" && url.Host=="baijiahao.baidu.com" && url.IsDefaultPort && url.UserInfo.Length==0 &&
            url.AbsolutePath=="/pcui/article/publish";
    }

    string ReadBaijiahaoPublishRequestId(Stream stream) {
        if(stream==null || !stream.CanSeek || stream.Length>2097152)return null;
        long before=stream.Position;
        try {
            string raw;
            stream.Position=0;
            using(var reader=new StreamReader(stream,Encoding.UTF8,true,2048,true))raw=reader.ReadToEnd().Trim();
            if(raw.StartsWith("{",StringComparison.Ordinal)) {
                var article=json.Deserialize<BaijiahaoPublishArticle>(raw);
                return article==null?null:article.article_id;
            }
            var ids=Regex.Matches(raw,@"(?:\A|&)article_id=([0-9]{6,32})(?=&|\z)");
            return ids.Count==1?ids[0].Groups[1].Value:null;
        } catch(ArgumentException) { return null; }
        catch(InvalidOperationException) { return null; }
        finally { stream.Position=before; }
    }

    bool IsBaijiahaoPublishReceipt(string id,string requestId,string payload) {
        if(String.IsNullOrEmpty(id) || (requestId!=null && requestId!=id))return false;
        payload=payload.Trim();
        if(!payload.StartsWith("{",StringComparison.Ordinal)) {
            var callback=Regex.Match(payload,@"\A[A-Za-z_$][A-Za-z0-9_.$]*\(\s*(\{[\s\S]*\})\s*\)\s*;?\z");
            if(!callback.Success)return false;
            payload=callback.Groups[1].Value;
        }
        try {
            var reply=json.Deserialize<BaijiahaoPublishReply>(payload);
            return reply!=null && reply.errno==0 && reply.ret!=null &&
                (reply.ret.article_id!=null?reply.ret.article_id==id:requestId==id) &&
                Regex.IsMatch(reply.ret.nid??"", @"\A[A-Za-z0-9_-]{6,80}\z");
        } catch(ArgumentException) { return false; }
        catch(InvalidOperationException) { return false; }
        catch(FormatException) { return false; }
        catch(OverflowException) { return false; }
    }

    const string BaijiahaoPublishControls=@"((address,operation) => {
        if(location.href!==address || location.origin!=='https://baijiahao.baidu.com' ||
            location.pathname!=='/builder/rc/edit' || new URLSearchParams(location.search).get('type')!=='news')return null;
        const visible=e=>e.getClientRects().length && !e.closest('[hidden],[aria-hidden=true]') && getComputedStyle(e).visibility!=='hidden';
        const list=(root,selector)=>[...root.querySelectorAll(selector)].filter(visible);
        if(list(document,'.cheetah-modal,[role=dialog]').length)return null;
        const groups=list(document,'.cover-radio-group');
        if(groups.length!==1)return 'cover';
        const selected=list(groups[0],'label').filter(e=>e.querySelector('input[type=radio]:checked'));
        if(selected.length!==1)return 'cover';
        const mode=selected[0].innerText.trim();
        const required=mode==='单图'?1:mode==='三图'?3:0;
        const field=groups[0].closest('.cheetah-form-item');
        if(!required || !field)return 'cover';
        const covers=list(field,'img').filter(e=>[...e.classList].some(c=>c.endsWith('-coverImg')));
        if(covers.length!==required || covers.some(e=>!e.complete || !e.naturalWidth || !e.currentSrc.startsWith('https://')))return 'cover';
        const toggles=list(document,'label').filter(e=>e.innerText.trim()==='自动生成播客');
        if(toggles.length>1)return null;
        if(toggles.length===1) {
            const option=toggles[0].querySelector('input[type=checkbox]');
            if(!option)return null;
            if(operation==='prepare' && option.checked && !option.disabled) {
                option.click();
                if(option.checked)return 'settling';
            }
            if(option.checked)return null;
        }
        const toolbars=list(document,'.editor-component-operator .op-list-wrap-news > .op-list-right');
        if(toolbars.length!==1)return null;
        const buttons=list(toolbars[0],'.op-btn-outter-content button.cheetah-btn');
        if(!['存草稿','预览','定时发布'].every(label=>buttons.some(e=>e.innerText.trim()===label)))return null;
        const candidates=buttons.filter(e=>e.classList.contains('cheetah-btn-primary') && !e.disabled &&
            e.getAttribute('aria-disabled')!=='true' && e.innerText.trim()==='发布');
        if(candidates.length!==1)return null;
        if(operation==='publish') {candidates[0].click();return 'clicked';}
        return 'ready';
    })";

    const string BaijiahaoBodyCoverControls=@"((address,source,step) => {
        if(location.href!==address || location.origin!=='https://baijiahao.baidu.com' || location.pathname!=='/builder/rc/edit')return null;
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]')&&getComputedStyle(e).visibility!=='hidden';
        const all=(root,selector)=>[...root.querySelectorAll(selector)].filter(visible);
        const usable=e=>!e.disabled&&!e.matches('[aria-disabled=true],.cheetah-btn-disabled,.cheetah-btn-loading');
        const groups=all(document,'.cover-radio-group');
        if(groups.length!==1)return null;
        const field=groups[0].closest('.cheetah-form-item');
        const mode=all(groups[0],'label').filter(label=>label.querySelector('input[type=radio]:checked'));
        if(!field||mode.length!==1||mode[0].innerText.trim()!=='单图')return null;
        const covers=all(field,'img').filter(e=>[...e.classList].some(c=>c.endsWith('-coverImg')));
        const dialogs=all(document,'.cheetah-modal,[role=dialog]');
        if(step==='ready')return !dialogs.length&&covers.length===1&&covers[0].complete&&covers[0].naturalWidth&&
            covers[0].currentSrc.startsWith('https://')?'ready':null;
        if(covers.length)return 'conflict';
        if(step==='open') {
            if(dialogs.length)return null;
            const triggers=all(field,'div.FeEditorApp-_73a3a52aab7e3a36-text').filter(e=>e.innerText.trim()==='选择封面'&&
                e.parentElement.matches('.FeEditorApp-_73a3a52aab7e3a36-content')&&usable(e.parentElement));
            if(triggers.length!==1)return null;
            triggers[0].parentElement.click();return 'opened';
        }
        const css=part=>'.FeEditorApp-e8c90bfac9d4eab4-'+part;
        const modals=dialogs.filter(e=>e.matches('.cheetah-modal.FeEditorApp-f3b5c632d7f1821c-tabsModal[role=dialog]'));
        if(modals.length!==1||dialogs.some(e=>e!==modals[0]&&!modals[0].contains(e)))return null;
        const tabs=all(modals[0],'.cheetah-tabs-tab-btn[role=tab][aria-selected=true]');
        if(tabs.length!==1||!/^正文\/本地上传(?:\s*\(1\))?$/.test(tabs[0].innerText.trim()))return null;
        const panels=all(modals[0],'.cheetah-tabs-tabpane-active[role=tabpanel] '+css('wrap'));
        if(panels.length!==1)return null;
        const panel=panels[0];
        const cards=all(panel,css('imgItem'));
        const chosen=cards.filter(card=>card.querySelector(css('checkbox')+css('selected')));
        const imageUrl=card=>{
            const pictures=all(card,css('imgWrapper')+' > img'+css('img'));
            if(pictures.length!==1)return null;
            const url=new URL(pictures[0].src,location.href);
            if(url.protocol==='https:')return url.href;
            if(source===null&&url.protocol==='blob:'&&url.origin===location.origin)return url.href;
            return null;
        };
        if(step==='upload-control') {
            if(source!==null || chosen.length || cards.some(card=>!imageUrl(card)))return null;
            const inputs=[...panel.querySelectorAll('.cheetah-upload input[type=file][accept=""image/*""]')]
                .filter(input=>usable(input)&&!input.multiple&&visible(input.parentElement));
            if(inputs.length!==1)return null;
            const stored=window.__zhigaoBaijiahaoTitleCover;
            if(stored && stored.input!==inputs[0])return null;
            if(!stored)window.__zhigaoBaijiahaoTitleCover={input:inputs[0],initial:new Set(cards.map(imageUrl))};
            inputs[0].setAttribute('data-zhigao-baijiahao-title-cover','');
            return 'upload-ready';
        }
        const upload=window.__zhigaoBaijiahaoTitleCover;
        if(source===null && !upload)return null;
        const candidates=cards.filter(card=>{
            const url=imageUrl(card);
            if(source===null) {
                if(!url || upload.initial.has(url))return false;
                if(url.startsWith('blob:'))return chosen.length===1 && chosen[0]===card && !!card.querySelector(css('deleteBtn'));
                return !!card.querySelector(css('deleteBtn'));
            }
            return url===source&&!card.querySelector(css('deleteBtn'));
        });
        if(candidates.length!==1)return candidates.length?'conflict':null;
        const target=candidates[0];
        if(chosen.some(card=>card!==target)||chosen.length>1)return 'conflict';
        if(step==='select') {
            if(chosen.length===1)return 'selected';
            const spots=all(target,css('hotArea')).filter(usable);
            if(spots.length!==1)return 'conflict';
            spots[0].click();return 'selected';
        }
        if(source===null && imageUrl(target).startsWith('blob:')) {
            // The picker selects the local preview before its crop canvas has been sized.
            const crops=all(panel,css('cropper')+' canvas[class$=""-imgCanvas""]');
            if(crops.length!==1 || crops[0].width<=1 || crops[0].height<=1 ||
                crops[0].getBoundingClientRect().width<=1 || crops[0].getBoundingClientRect().height<=1)return null;
            const canvas=crops[0], bounds=canvas.getBoundingClientRect();
            const geometry=[imageUrl(target),canvas.width,canvas.height,Math.round(bounds.width),Math.round(bounds.height)].join('|');
            if(upload.geometry!==geometry) {
                upload.geometry=geometry;
                upload.stableSince=performance.now();
                return null;
            }
            if(performance.now()-upload.stableSince<700)return null;
            try {
                const rgba=canvas.getContext('2d').getImageData(Math.floor(canvas.width/2),Math.floor(canvas.height/2),1,1).data;
                if(!rgba[3])return null;
            } catch(e) {return null;}
        }
        const buttons=all(panel,'button'+css('confirmBtn')).filter(e=>usable(e)&&/^确定\s*\(1\)$/.test(e.innerText.trim()));
        if(step!=='confirm'||chosen.length!==1||buttons.length!==1)return null;
        buttons[0].click();return 'confirmed';
    })";
}
