using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    // All interactions are scoped to the current titled draft and its article-topic controls.
    const string ZhihuTopicControls = @"
        if(location.origin!=='https://zhuanlan.zhihu.com' || !/^\/p\/\d+\/edit\/?$/.test(location.pathname))return null;
        const headline=document.querySelector('textarea[placeholder*=标题],input[placeholder*=标题]');
        if(!headline || !expectedTitle || headline.value!==expectedTitle)return null;
        const shown=e=>!!(e&&e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden');
        const clean=s=>(s||'').replace(/[\u200b\ufeff]/g,'').trim();
        const labels=[...document.querySelectorAll('label')].filter(e=>clean(e.textContent)==='文章话题');
        const row=labels.length===1 ? labels[0].parentElement : null;
        const button=(scope,label)=>[...scope?.querySelectorAll('button')||[]].find(b=>shown(b)&&clean(b.innerText)===label);
        const search=()=>row?.querySelector('input[aria-label=搜索话题],input[placeholder*=搜索话题]');
        const selected=()=>row ? [...row.querySelectorAll('button')].filter(b=>b.querySelector('svg.ZDI--CircleClose24') &&
            b.previousElementSibling?.tagName==='DIV' && !b.previousElementSibling.children.length)
            .map(b=>b.previousElementSibling.textContent.trim()).filter(Boolean) : null;
    ";

    const string ZhihuSearchPhrases = @"
        const text=document.querySelector('.public-DraftEditor-content')?.textContent||'';
        const heading=headline.value;
        const stop=new Set('一个 如何 怎么 为何 什么 时代 出路 思考 分析 总结 介绍 今天 经验 技巧 实战 实践 指南 案例 方法 问题 探讨 分享 浅谈 有关 影响 以及 你的 我们 使用 进行 真的 关于 为什么'.split(' '));
        const tokens=typeof Intl.Segmenter==='function'
           ? [...new Intl.Segmenter('zh-CN',{granularity:'word'}).segment(heading)].filter(t=>t.isWordLike)
           : [...heading.matchAll(/[a-zA-Z][a-zA-Z0-9+#.-]*|[\u4e00-\u9fff]{2,8}/g)].map(t=>({segment:t[0],index:t.index}));
        const candidates=[];
        function add(term,start,compound){
            if(term.length<2||term.length>24||stop.has(term)||/^\d+$/.test(term))return;
            const alias=/^AI$/i.test(term)?'人工智能':term;
            const occurrences=text.toLowerCase().split(term.toLowerCase()).length-1;
            if(!occurrences || (compound && occurrences<2))return;
            const score=Math.min(occurrences,5)+(compound ? 2 : 0)+(alias!==term ? 5 : 0);
            candidates.push({term:alias,start,score});
        }
        for(let i=0;i<tokens.length;i++){
            const t=tokens[i], next=tokens[i+1];
            add(t.segment,t.index,false);
            if(next && t.index+t.segment.length===next.index && !stop.has(t.segment) && !stop.has(next.segment) &&
                /^[\u4e00-\u9fff]{2,5}$/.test(t.segment) && /^[\u4e00-\u9fff]{1,4}$/.test(next.segment) &&
                !/^(的|和|与|是|了|在|及|或|等|跟|将|有|用|该|这|那|会|能)$/.test(next.segment))
                add(t.segment+next.segment,t.index,true);
        }
        const ordered=candidates.sort((a,b)=>b.score-a.score || a.start-b.start || b.term.length-a.term.length);
        const phrases=[];
        for(const c of ordered)if(!phrases.some(t=>t.includes(c.term)||c.term.includes(t)))phrases.push(c.term);
        return phrases.slice(0,5);";

    Task<string> TopicScript(WebView2 view,string script,string term="") {
        return view.ExecuteScriptAsync("(()=>{const expectedTitle="+json.Serialize(title)+",term="+json.Serialize(term)+";"+
            ZhihuTopicControls+script+"})()");
    }

    async Task<string[]> SetZhihuDraftTopics() {
        var phrases=json.Deserialize<string[]>(await TopicScript(page,ZhihuSearchPhrases));
        if (phrases==null) throw new InvalidOperationException("话题设置未能确认当前知乎草稿的标题。");
        const string openSettings=@"if(row && shown(button(row,'添加话题')))return true;
            const settings=button(document,'发布设置');if(settings)settings.click();return !!row;";
        await TopicScript(page,openSettings);
        string[] names=null;
        try {
            await Wait(async ()=>{
                names=json.Deserialize<string[]>(await TopicScript(page,"return selected();"));
                return names!=null;
            },6,"知乎草稿的「文章话题」区域尚未就绪。");
        } catch (TimeoutException) { names=null; }
        if(names==null) {
            if(await TopicScript(page,"return !row;")=="true")return new string[0];
            throw new InvalidOperationException("知乎草稿的「文章话题」区域尚未就绪。");
        }
        foreach(string term in phrases) {
            if (names.Length>=2) break;
            if (names.Any(n=>String.Equals(n,term,StringComparison.OrdinalIgnoreCase))) continue;
            string started=await TopicScript(page,@"if(!row)return false;
                if(!shown(search())){const add=button(row,'添加话题');if(!add)return false;add.click();}
                const input=search();if(!shown(input))return false;
                input.focus();
                Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,term);
                input.dispatchEvent(new Event('input',{bubbles:true}));return true;",term);
            if (started!="true") break;
            bool chosen=false;
            for(int i=0;i<12 && !chosen;i++) {
                await Task.Delay(200);
                chosen=await TopicScript(page,@"const input=search();
                    if(!shown(input)||input.value!==term||document.activeElement!==input)return false;
                    const options=[...document.querySelectorAll('.Popover-content button')].filter(b=>shown(b)&&!b.disabled &&
                       b.textContent.trim().toLowerCase()===term.toLowerCase());
                    if(options.length!==1)return false;options[0].click();return true;",term)=="true";
            }
            if (!chosen) continue;
            await Wait(async ()=>{
                names=json.Deserialize<string[]>(await TopicScript(page,"return selected();"));
                return names!=null && names.Any(n=>String.Equals(n,term,StringComparison.OrdinalIgnoreCase));
            },4,"知乎未确认选中的文章话题。");
        }
        return names;
    }

    async Task<bool> ZhihuDraftTopicsMatch(WebView2 view,string[] expected) {
        var names=json.Deserialize<string[]>(await TopicScript(view,"return selected()||[];"));
        return names!=null && expected!=null && expected.All(t=>names.Contains(t,StringComparer.Ordinal));
    }
}
