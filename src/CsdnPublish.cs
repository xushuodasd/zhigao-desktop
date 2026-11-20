using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

partial class LoginWindow {
    sealed class CsdnSettingsNeeded : Exception {
        public CsdnSettingsNeeded(string message) : base(message) {}
    }

    async Task PublishCsdn(string file) {
        string folder = Path.Combine(Path.GetTempPath(),"Zhigao-CSDN-" + Guid.NewGuid().ToString("N"));
        string articleId = "", attemptPath = null;
        bool submitted = false;
        string result;
        try {
            string attempts = Path.Combine(dataFolder,"Attempts-CSDN");
            Directory.CreateDirectory(attempts);
            string key;
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(file))
                key = BitConverter.ToString(hash.ComputeHash(stream));
            attemptPath = Path.Combine(attempts,key+".txt");
            FileStream guard;
            try { guard = File.Open(attemptPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch (IOException) { throw new PriorAttempt("待核实 · 另一窗口正在处理这份稿件，请等待该次回执。"); }
            using (guard) {
                status.Text = "CSDN · 正在读取 Word";
                Directory.CreateDirectory(folder);
                var doc = await Task.Run(() => ReadCsdnDocument(file,folder));
                await OpenCsdn(false);
                if (File.Exists(attemptPath)) {
                    string prior = File.ReadAllText(attemptPath).Trim();
                    if (prior.Length > 0 && !prior.StartsWith("待核实 · ") && !prior.StartsWith("需处理 · ") &&
                        !prior.StartsWith("审核中 · ") && !prior.StartsWith("已提交 · ")) throw new PriorAttempt(prior);
                    status.Text = "CSDN · 正在创作中心核对上次结果";
                    string previousId = Regex.Match(prior,@"草稿编号 (\d+)").Groups[1].Value;
                    DateTime last = File.GetLastWriteTime(attemptPath);
                    var listing = await ReadCsdnListingWithRetry(doc.Title,previousId.Length==0 ? null : previousId,last,false);
                    string confirmed = CsdnListingOutcome(listing);
                    if (confirmed != null) {
                        articleId=listing.Id;
                        WriteCsdnAttempt(attemptPath,confirmed);
                        throw new PriorAttempt(confirmed);
                    }
                    // An acknowledged submission can still be absent from the creator list while it propagates.
                    if (prior.StartsWith("审核中 · ") || prior.StartsWith("已提交 · ")) throw new PriorAttempt(prior);
                    if (previousId.Length>0 && prior.StartsWith("待核实 · ")) throw new PriorAttempt(prior+" 正在等待明确回执，已防止重复提交。");
                    if (listing == null || listing.State != "draft" || !Regex.IsMatch(listing.Id ?? "",@"^\d+$") ||
                        (previousId.Length > 0 && previousId != listing.Id) ||
                        (!prior.StartsWith("需处理 · ") && DateTime.Now-last < TimeSpan.FromMinutes(2)))
                        throw new PriorAttempt("待核实 · 尚未确认上次提交的最终状态，已暂停再次发送。");
                    articleId = listing.Id;
                    status.Text = "CSDN · 已找到原草稿，正在核对 Word 图文与表格";
                    await VerifyExistingCsdnDraft(doc,articleId);
                } else {
                    await PrepareCsdnDraft(doc,folder);
                    articleId = CsdnArticleId.Match(csdn.Source.Query).Groups[1].Value;
                    if (articleId.Length == 0) throw new InvalidOperationException("缺少已核对的 CSDN 草稿编号。");
                }
                try {
                    result = await SubmitCsdnDraft(doc,articleId,attemptPath,()=>submitted=true);
                } catch (CsdnSettingsNeeded ex) {
                    result = "需处理 · 草稿编号 "+articleId+"，"+ex.Message;
                    WriteCsdnAttempt(attemptPath,result);
                }
            }
        } catch (PriorAttempt e) { result = e.Message; }
        catch (Exception e) { result = (submitted?"待核实 · ":"未发出 · ") + (articleId.Length>0 ? "草稿编号 "+articleId+"，" : "") + e.Message; }
        finally {
            try { if (Directory.Exists(folder)) Directory.Delete(folder,true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if ((result.StartsWith("已发布 · ") || result.StartsWith("审核中 · ") || result.StartsWith("已提交 · ")) &&
            articleId.Length>0 && csdn.CoreWebView2!=null && ShouldResetCsdnEditor(csdn.Source,articleId)) {
            try { csdn.CoreWebView2.Navigate(CsdnEditorUrl); }
            catch (InvalidOperationException) {} catch (System.Runtime.InteropServices.COMException) {}
        }
        status.Text = "CSDN · " + result;
        Record(Path.GetFileName(file) + "    CSDN · " + result);
    }

    static bool ShouldResetCsdnEditor(Uri page,string id) {
        return page!=null && (IsCsdnReceipt(page.AbsoluteUri,id) ||
            (IsCsdnEditor(page) && CsdnArticleId.Match(page.Query).Groups[1].Value==id));
    }
    async Task<CsdnListing> ReadCsdnListingWithRetry(string titleText,string id,DateTime? timestamp,bool afterClick) {
        CsdnListing last=null;
        int checks = afterClick ? 3 : 2;
        for(int n=0;n<checks;n++) {
            if(n>0) {
                status.Text="CSDN · 正在创作中心复核（"+(n+1)+"/"+checks+"）";
                await Task.Delay(n==1?1400:3500);
            }
            last=await FindCsdnArticle(titleText,id,timestamp);
            if(last!=null && last.State!="unknown" && (!afterClick || last.State!="draft")) return last;
        }
        return last;
    }

    async Task<string> SubmitCsdnDraft(CsdnDocument doc,string id,string attemptPath,Action beforeClick) {
        for(int n=0;n<2;n++) {
            bool retrySetup=false;
            try {
                status.Text="CSDN · 正在核对标题、摘要并匹配标签、活动和话题";
                await OpenCsdnPublishPanel();
                await PrepareCsdnSettings(doc);
            } catch (TimeoutException) { if(n>0)throw; retrySetup=true; }
            if(retrySetup) {
                status.Text="CSDN · 设置页加载超时，正在重新读取原草稿（第 2 次）";
                await VerifyExistingCsdnDraft(doc,id);
                continue;
            }
            WriteCsdnAttempt(attemptPath,"待核实 · 草稿编号 "+id+"，正在核对提交回执。");
            beforeClick();
            status.Text="CSDN · 正在提交并核对平台回执";
            string clicked=null, signal=null;
            try {
                clicked=await CsdnScript("const id="+json.Serialize(id)+";"+CsdnFinalClick);
                if(clicked=="true")signal=await WaitForCsdnReceipt(id,doc.Title);
            } catch (TimeoutException) { } catch (InvalidOperationException) { } catch (System.Runtime.InteropServices.COMException) { }
            if(signal!=null && signal.StartsWith("需处理 · "))throw new CsdnSettingsNeeded(signal.Substring("需处理 · ".Length));
            bool transient=signal=="retry-rejected";
            if(signal==null || !signal.StartsWith("已发布 · ")) {
                status.Text="CSDN · 正在创作中心核实文章状态";
                var listing=await ReadCsdnListingWithRetry(doc.Title,id,null,signal==null && clicked!="false");
                string confirmed=CsdnListingOutcome(listing);
                if(confirmed!=null)signal=confirmed;
                else if(CanRetryCsdnSubmission(clicked,signal,listing,id)) {
                    if(n==0) {
                        status.Text="CSDN · 平台仍为原草稿，正在重试（第 2 次）";
                        await Task.Delay(1000);
                        await VerifyExistingCsdnDraft(doc,id);
                        continue;
                    }
                    throw new CsdnSettingsNeeded(transient?"平台明确拒绝本次提交，两次尝试后仍为草稿。":"最终按钮两次未就绪，尚未送出。");
                }
            }
            if(signal==null || signal=="retry-rejected")throw new InvalidOperationException("尚未收到可确认的发布回执，已保留本次提交记录。");
            WriteCsdnAttempt(attemptPath,signal);
            return signal;
        }
        throw new InvalidOperationException("发布设置仍未就绪。");
    }
    static bool CanRetryCsdnSubmission(string clicked,string signal,CsdnListing listing,string id) {
        return (clicked=="false" || signal=="retry-rejected") && listing!=null && listing.State=="draft" && listing.Id==id;
    }
    static string CsdnListingOutcome(CsdnListing listing) {
        if (listing == null || !Regex.IsMatch(listing.Id ?? "",@"^\d+$")) return null;
        if (listing.State == "published" && IsCsdnReceipt(listing.Url,listing.Id)) return "已发布 · "+listing.Url;
        if (listing.State == "review") return "审核中 · 草稿编号 "+listing.Id+"，CSDN 创作中心显示审核中。";
        if (listing.State == "rejected") return "需处理 · 草稿编号 "+listing.Id+"，平台审核未通过，请查看审核意见。";
        return null;
    }

    static void WriteCsdnAttempt(string file,string value) {
        string temporary = file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using (var stream = File.Open(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                var bytes = Encoding.UTF8.GetBytes(value);
                stream.Write(bytes,0,bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(file)) File.Replace(temporary,file,null);
            else File.Move(temporary,file);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    async Task VerifyExistingCsdnDraft(CsdnDocument doc,string id) {
        await Wait(()=>Task.FromResult(csdn.CoreWebView2!=null),15,"CSDN 后台浏览器启动超时。");
        csdn.CoreWebView2.Navigate(CsdnEditorUrl+"?articleId="+id);
        await Wait(async ()=> IsCsdnEditor(csdn.Source) && CsdnArticleId.Match(csdn.Source.Query).Groups[1].Value==id &&
            await CsdnScript("return title.value==="+json.Serialize(doc.Title)+" && !!body.textContent.trim();")=="true",
            18,"无法重新打开并核对原 CSDN 草稿，请检查账号。");
        string existing = await ReadCsdnBody();
        var links = CsdnImageLink.Matches(existing).Cast<Match>().Select(m=>m.Groups[1].Value).ToArray();
        if (links.Length != doc.Images.Count) throw new InvalidOperationException("原草稿的配图数量已改变。");
        string markdown = Regex.Replace(doc.Markdown,"\u0001([0-9]+)\u0002",m=>"![配图]("+links[Int32.Parse(m.Groups[1].Value)]+")");
        if (!await CsdnMatches(doc.Title,markdown)) throw new InvalidOperationException("原草稿的正文或表格与本次 Word 不一致。");
        if (links.Length > 0) await Wait(async ()=>await CsdnScript("const links="+json.Serialize(links)+";return links.every(u=>[...document.images].some(i=>i.complete&&i.naturalWidth>0&&i.src.split('#')[0]===u.split('#')[0]));")=="true",12,"原草稿的配图未加载完成。");
    }

    const string CsdnPanel = @"const shown=e=>!!(e && e.getClientRects().length);
        const panel=[...document.querySelectorAll('.modal__publish-article')].find(shown);
        if(!panel)return null;
        const tags=()=>[...panel.querySelectorAll('.mark_selection[typename=tags] .mark_selection_title_el_tag .el-tag')].map(e=>e.textContent.trim()).filter(Boolean);
        const button=()=>[...panel.querySelectorAll('button.btn-b-red')].filter(e=>shown(e)&&!e.disabled&&e.innerText.trim()==='发布文章');
        const safe=()=>{const r=[...panel.querySelectorAll('input[type=radio]:checked')].map(e=>e.parentElement.innerText.trim());
            return r.includes('原创')&&r.includes('全部可见')&&r.includes('否')&&![...panel.querySelectorAll('input[type=checkbox]:checked')].some(e=>/备份/.test(e.parentElement.innerText));};
        ";

    async Task OpenCsdnPublishPanel() {
        var timer=Stopwatch.StartNew();
        int opened=0;
        await Wait(async ()=>{
            if(await CsdnScript(CsdnPanel+"return true;")=="true")return true;
            // The editor can expose its toolbar before hydration. Only the outside settings
            // opener is safe to repeat; the terminal publish button is scoped separately.
            if(opened>=2 || (opened==1 && timer.Elapsed.TotalSeconds<3))return false;
            string click=await CsdnScript(@"if(document.readyState!=='complete')return false;
                const open=[...document.querySelectorAll('button')].filter(b=>b.getClientRects().length &&
                    !b.closest('.modal__publish-article') && b.innerText.trim()==='发布文章');
                if(open.length!==1 || open[0].disabled)return false;open[0].click();return true;");
            if(click=="true"){opened++;timer.Restart();}
            return false;
        },10,"CSDN 未打开发布设置，已保留草稿。");
        if (await CsdnScript(CsdnPanel+"return safe();")!="true")
            throw new CsdnSettingsNeeded("原创、公开可见或跨平台同步设置与工作台选择不符。");
    }

    static string CsdnSummary(string markdown) {
        var paragraphs = Regex.Split(markdown,@"\n\s*\n").Select(p=>p.Trim()).ToArray();
        int start = Array.FindIndex(paragraphs,p=>Regex.IsMatch(p,@"^#*\s*(TL[;；:]?DR|摘要|内容摘要)\s*[:：]?\s*$",RegexOptions.IgnoreCase));
        var text = paragraphs.Skip(start+1).Where(p=>p.Length>0 && !Regex.IsMatch(p,@"^[#|\u0001]|^!\[")).ToArray();
        var prose = text.FirstOrDefault(p=>p.Length>=24) ?? text.FirstOrDefault();
        if (prose == null) return "";
        prose = Regex.Replace(prose,@"\s+"," ").Trim();
        if (prose.Length<=256) return prose;
        int cut = Char.IsHighSurrogate(prose[255]) ? 255 : 256;
        int stop = prose.LastIndexOfAny(new[]{'。','！','？','!','?'},cut-1,cut);
        return prose.Substring(0,stop>=60 ? stop+1 : cut);
    }

    // Score only names the platform actually offers. An unsupported subject in a
    // compound name (e.g. AI + painting) disqualifies the whole option.
    const string CsdnSubjectMatcher = @"
        const canonical=v=>(v||'').normalize('NFKC').toLowerCase().replace(/人工智能/g,' ai ').replace(/程序员/g,' programmer ');
        const segmenter=new Intl.Segmenter('zh-CN',{granularity:'word'});
        const generic=new Set('的 和 与 在 用 之 年 下 大会 大赛 项目 技术 开发 时代 分享 活动 话题 创作 交流 专题 征稿 原创 探讨 大家 一起 聊聊 实战 经验 秋季 春季 夏季 冬季 好礼 有奖 it'.split(' '));
        const heading=canonical(title.value), article=canonical(body.textContent);
        const lead=article.slice(0,380);
        const hits=(s,k)=>{
            if(/^[a-z0-9]+$/.test(k))return [...s.matchAll(new RegExp('(?:^|[^a-z0-9])'+k+'(?=$|[^a-z0-9])','g'))].length;
            return s.split(k).length-1;
        };
        function score(label){
            if(/专属邀请码|指定产品|付费|独家授权|同意.{0,6}(协议|条款)|报名费/.test(label))return 0;
            const name=canonical(label).replace(/【(?:多重好礼|有奖征文|征稿|活动|话题)】/g,' ');
            let terms=[], pending='';
            const flush=()=>{if(pending){terms.push(pending);pending='';}};
            for(const token of segmenter.segment(name)){
                const k=token.segment;
                if(!token.isWordLike || generic.has(k)) {flush();continue;}
                if(k.length===1 && /[\u3400-\u9fff]/.test(k))pending+=k;
                else {flush();terms.push(k);}
            }
            flush();terms=[...new Set(terms.filter(k=>!/^\d+$/.test(k)))];
            if(!terms.length || terms.every(k=>k.length<2))return 0;
            let strength=0, central=false;
            for(const k of terms){
                const h=hits(heading,k), l=hits(lead,k), count=hits(article,k);
                if(!h&&!count)return 0;
                if(k.length>1 && (h||l||count>=3))central=true;
                strength+=h?12:l?6:Math.min(count,3);
            }
            return central?strength:0;
        }
        const strongest=(labels,allowEquallyRelevant=false)=>{
            const ranked=[...new Set(labels)].map(name=>({name,points:score(name)})).filter(v=>v.points>0).sort((a,b)=>b.points-a.points);
            if(!ranked.length || (!allowEquallyRelevant && ranked.length>1 && ranked[0].points-ranked[1].points<3))return '';
            return ranked[0].name;
        };";

    const string CsdnPicker = @"
        const field=[...panel.querySelectorAll('input')].find(i=>i.placeholder===placeholder),select=field?.closest('.el-select,[role=combobox]');
        const visible=e=>shown(e)&&getComputedStyle(e).visibility!=='hidden';
        const lists=()=>[...document.querySelectorAll('.el-select-dropdown,[role=listbox]')].filter(visible);
        const getList=w=>{
            const owned=field.getAttribute('aria-controls')||field.getAttribute('aria-owns')||select.getAttribute('aria-controls')||select.getAttribute('aria-owns');
            const direct=owned?document.getElementById(owned):select.querySelector('.el-select-dropdown,[role=listbox]');
            if(visible(direct))return direct;
            const added=lists().filter(e=>!w.before.has(e));
            return added.length===1?added[0]:null;
        };";

    async Task ChooseCsdnSubject(string placeholder) {
        string picker=CsdnPanel+"const placeholder="+json.Serialize(placeholder)+";"+CsdnPicker;
        string opened=await CsdnScript(picker+CsdnSubjectMatcher+@"if(!field||!select||!visible(field)||field.disabled)return false;
            if(field.value.trim())return score(field.value)>0?false:'mismatch';
            window.__zhigaoCsdnPicker={field,before:new Set(lists())};field.click();return true;");
        if(opened=="\"mismatch\"")throw new CsdnSettingsNeeded("已有"+placeholder.Substring(3)+"与稿件未能匹配，请核对原选项。");
        if(opened!="true")return;
        await Wait(async ()=>await CsdnScript(picker+@"
            const w=window.__zhigaoCsdnPicker;if(!w||w.field!==field)return false;
            const list=getList(w);if(!list)return false;
            if(!list.querySelector('.el-select-dropdown__item,[role=option]') && !/暂无数据|无数据|无匹配数据|暂无可选/.test(list.innerText))return false;
            w.list=list;return true;")=="true",6,"CSDN "+placeholder.Substring(3)+"候选列表未就绪。");
        string name=json.Deserialize<string>(await CsdnScript(picker+CsdnSubjectMatcher+@"
            const w=window.__zhigaoCsdnPicker;
            if(w?.field!==field||!visible(w.list))return null;
            const options=[...w.list.querySelectorAll('.el-select-dropdown__item,[role=option]')].filter(e=>
                visible(e)&&!e.classList.contains('is-disabled')&&e.getAttribute('aria-disabled')!=='true');
            const label=e=>e.innerText.trim();
            const target=strongest(options.map(label));if(!target)return '';
            const eligible=options.filter(e=>label(e)===target);
            if(eligible.length!==1)return '';
            eligible[0].click();return target;"));
        if(name==null) throw new CsdnSettingsNeeded("CSDN 候选列表发生变化，请核对稿件设置。");
        if(name.Length>0) {
            await Wait(async ()=>await CsdnScript(picker+"return field?.value.trim()==="+json.Serialize(name)+";")=="true",
                4,"CSDN 未确认选中的"+placeholder.Substring(3)+"。");
        }
        await CsdnScript(picker+@"const w=window.__zhigaoCsdnPicker;
            if(w && w.field===field && select && visible(w.list||getList(w)))field.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',keyCode:27,bubbles:true}));
            delete window.__zhigaoCsdnPicker;");
    }

    async Task PrepareCsdnSettings(CsdnDocument doc) {
        if(await CsdnScript("return title.value.trim()==="+json.Serialize(doc.Title)+";")!="true")
            throw new CsdnSettingsNeeded("发布标题与稿件标题不一致，草稿已保留。");
        // The platform populates its recommended tag after the modal becomes visible.
        string hasTag = CsdnPanel+CsdnSubjectMatcher+"return tags().some(t=>score(t)>0);";
        bool needsTag = false;
        try { await Wait(async ()=>await CsdnScript(hasTag)=="true",4,"未生成标签"); }
        catch (TimeoutException) { needsTag = true; }
        if (needsTag) {
            await CsdnScript(CsdnPanel+@"const box=panel.querySelector('.mark_selection[typename=tags]');
                const entry=box?.querySelector('.tag__btn-tag');
                if(entry&&!shown(box.querySelector('.mark_selection_box')))entry.click();");
            string choose = CsdnPanel+CsdnSubjectMatcher+@"if(tags().some(t=>score(t)>0))return true;
                const box=panel.querySelector('.mark_selection[typename=tags] .mark_add_tag_right');
                if(!box)return false;
                const choices=[...box.querySelectorAll('.el-tag')].filter(shown);
                const best=strongest(choices.map(e=>e.textContent.trim()),true);
                const match=choices.find(e=>e.textContent.trim()===best && best);
                if(match)match.click();return tags().some(t=>score(t)>0);";
            try { await Wait(async ()=>await CsdnScript(choose)=="true",5,"标签缺失"); }
            catch (TimeoutException) { throw new CsdnSettingsNeeded("缺少必填文章标签，CSDN 推荐项与稿件未能匹配。"); }
        }
        await ChooseCsdnSubject("请选择创作活动");
        await ChooseCsdnSubject("请选择创作话题");
        string summary = CsdnSummary(doc.Markdown);
        string issue = json.Deserialize<string>(await CsdnScript(CsdnPanel+"const summary="+json.Serialize(summary)+", expectedTitle="+json.Serialize(doc.Title)+";"+@"
            if(title.value.trim()!==expectedTitle)return '发布标题与稿件标题不一致，草稿已保留。';
            const area=[...panel.querySelectorAll('textarea')].find(e=>shown(e) && /展现列表|摘要/.test(e.placeholder));
            const opened=panel.querySelector('.mark_selection[typename=tags] .mark_selection_box');
            if(shown(opened)) panel.querySelector('.modal__content')?.click();
            if(area&&!area.value.trim()&&summary){area.value=summary;area.dispatchEvent(new Event('input',{bubbles:true}));area.dispatchEvent(new Event('change',{bubbles:true}));}
            const activityConsent=[...document.querySelectorAll('[role=dialog],.el-dialog,.form-entry')].some(node=>{
                if(!shown(node) || node===panel || node.contains(panel))return false;
                const text=node.innerText;
                return text.length<700 && /活动|话题|报名/.test(text) && /同意.{0,20}(条款|协议|授权)|须授权|付费报名/.test(text);
            });
            if(activityConsent)return '参加该活动需要确认额外条款，草稿已保留。';
            for(const row of panel.querySelectorAll('.form-entry.required')){
                if(!shown(row))continue;
                const label=row.querySelector('label,.form-entry__label')?.innerText?.trim()||row.innerText.split('\n')[0].trim();
                if(/文章标签/.test(label) && tags().length)continue;
                if(/文章摘要/.test(label) && area?.value.trim())continue;
                if(/封面/.test(label) && [...row.querySelectorAll('img')].some(i=>shown(i)&&i.complete&&i.naturalWidth>0))continue;
                if(row.querySelector('input:checked')||[...row.querySelectorAll('input:not([type=hidden]),textarea')].some(i=>shown(i)&&i.value.trim()))continue;
                return '平台要求填写'+label.slice(0,32)+'，草稿已保留。';
            }
            if(!safe()||button().length!==1)return '最终提交按钮或发布范围尚未就绪。';
            const fields=()=>[document.querySelector('input.article-bar__title')?.value,tags().join('\n'),
                [...panel.querySelectorAll('textarea')].find(e=>shown(e)&&/展现列表|摘要/.test(e.placeholder))?.value||'',
                ...['请选择创作活动','请选择创作话题'].map(ph=>[...panel.querySelectorAll('input')].find(i=>i.placeholder===ph)?.value||'')];
            const signature=JSON.stringify(fields());
            window.__zhigaoCsdnFieldsReady=livePanel=>livePanel===panel && panel.isConnected && JSON.stringify(fields())===signature;
            return '';"));
        if (issue==null || issue.Length>0) throw new CsdnSettingsNeeded(issue ?? "未能读取发布设置。");
    }

    const string CsdnFinalClick = CsdnPanel+@"
        if(new URLSearchParams(location.search).get('articleId')!==id || !safe() || !tags().length || button().length!==1 ||
            !window.__zhigaoCsdnFieldsReady?.(panel))return false;
        const signal=()=>{
            const notifications=[...document.querySelectorAll('.el-message,.el-notification,[role=alert],.modal__inner-1:not(.modal__publish-article),.publish-success')].filter(shown);
            const feedback=notifications.map(e=>e.innerText.trim()).filter(t=>t.length<650).join('\n');
            const errors=[...panel.querySelectorAll('.el-form-item__error,.error-msg,.error-message,.el-form-item.is-error,.form-entry.error')].filter(shown).map(e=>e.innerText.trim()).filter(Boolean);
            if(/已提交审核|进入审核|等待审核|审核中/.test(feedback))return 'review';
            if(/发布成功|已成功发布|成功发布|文章提交成功/.test(feedback))return 'accepted';
            if(shown(panel) && /(?:本次)?(?:发布|提交)失败.{0,30}(?:系统繁忙|服务繁忙|服务器繁忙|请稍后重试)|(?:系统繁忙|服务器繁忙).{0,20}(?:发布|提交)失败/.test(feedback))return 'transient-rejection';
            const tips=[...errors,...notifications.map(e=>e.innerText.trim())];
            const err=tips.find(t=>/请.{0,12}(选择|填写|添加|上传|输入).{0,24}(标签|分类|专栏|声明|封面|摘要|活动|话题)|发文额度不足|已达.{0,10}上限/.test(t));
            return shown(panel)&&err ? 'required:'+err.replace(/\s+/g,' ').slice(0,100) : '';
        };
        const watch={id,value:'',before:signal()};
        window.__zhigaoCsdnSubmit?.observer?.disconnect();
        watch.observer=new MutationObserver(()=>{const s=signal();if(s&&s!==watch.before)watch.value=s;});
        window.__zhigaoCsdnSubmit=watch;
        watch.observer.observe(document.body,{subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:['class','style']});
        button()[0].click();return true;";

    async Task<string> WaitForCsdnReceipt(string id,string titleText) {
        var clock=Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds<18) {
            Uri address = csdn.Source;
            if (address!=null && IsCsdnReceipt(address.AbsoluteUri,id)) {
                string titleCheck=await csdn.ExecuteScriptAsync("location.origin==='https://blog.csdn.net' && document.querySelector('h1.title-article,#articleContentId')?.textContent.trim()==="+json.Serialize(titleText));
                if (titleCheck=="true") return "已发布 · "+address.GetLeftPart(UriPartial.Path);
            }
            if (IsCsdnEditor(address) && CsdnArticleId.Match(address.Query).Groups[1].Value==id) {
                string sig = json.Deserialize<string>(await CsdnScript("const s=window.__zhigaoCsdnSubmit;return s?.id==="+json.Serialize(id)+"?s.value:'';"));
                if (sig=="transient-rejection") return "retry-rejected";
                if (sig=="review") return "审核中 · 草稿编号 "+id+"，CSDN 已接收文章。";
                if (sig=="accepted") return "已提交 · 草稿编号 "+id+"，平台已接收，正在等待公开链接。";
                if (sig!=null && sig.StartsWith("required:")) return "需处理 · "+sig.Substring(9);
            }
            await Task.Delay(250);
        }
        return null;
    }

    static bool IsCsdnReceipt(string url,string articleId) {
        Uri address;
        if (!Uri.TryCreate(url,UriKind.Absolute,out address) || address.Scheme!="https" || address.Host!="blog.csdn.net") return false;
        var match = Regex.Match(address.AbsolutePath,@"^/[\w-]+/article/details/(\d+)/?$");
        return match.Success && match.Groups[1].Value==articleId;
    }
}





