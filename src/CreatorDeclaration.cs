using System;
using System.Threading.Tasks;

partial class LoginWindow {
    static bool IsCreatorDeclaration(string value) {
        return value=="none" || value=="ai" || value=="repost" || value=="fiction" || value=="opinion";
    }

    async Task SetCreatorDeclaration(CreatorSession session,string declaration) {
        if(!IsCreatorDeclaration(declaration))throw new InvalidOperationException("创作声明选项无效。");
        var site=session.Site;
        var view=session.Web;
        var address=view.Source;
        var action=CreatorDeclarationScript+"("+json.Serialize(site.Id)+","+json.Serialize(declaration)+")";
        string result=null;
        try {
            await Wait(async()=>{
                if(view.Source!=address || !site.IsEditor(address))return true;
                result=json.Deserialize<string>(await view.ExecuteScriptAsync(action));
                return result=="confirmed" || result=="unsupported" || result=="conflict";
            },7,site.Label+"创作声明控件未就绪。");
        } catch(TimeoutException) { }
        if(view.Source==address && result=="confirmed")return;
        throw new InvalidOperationException(result=="conflict"?site.Label+"原草稿的声明与本次选择不同，请核对原草稿。":
            result=="unsupported"?site.Label+"未提供所选的创作声明，请核对平台支持的选项。":site.Label+"未确认所选创作声明。");
    }

    const string CreatorDeclarationScript=@"((site,value) => {
        const visible=e=>e.getClientRects().length && getComputedStyle(e).visibility!=='hidden' && !e.closest('[hidden],[aria-hidden=true]');
        const select=(root,query)=>[...root.querySelectorAll(query)].filter(visible);
        const uniq=items=>items.length===1?items[0]:null;
        const label=e=>e.textContent.trim().replace(/\s+/g,' ');
        if(site==='sohu'&&location.origin==='https://mp.sohu.com'&&location.pathname==='/mpfe/v4/contentManagement/news/addarticle') {
            const forms=select(document,'#info-source-signature .option-item:not(.additional-item) .info-radio');
            if(forms.length!==1)return null;
            const names={none:'无需声明',ai:'含有AI生成内容',repost:'内容为转载',fiction:'含有虚构演绎内容',opinion:'内容为个人观点'};
            const choices=select(forms[0],'label.info-radio-label');
            const choice=uniq(choices.filter(e=>label(e)===names[value]));
            const input=choice?.querySelector('input[type=radio]');
            if(!input)return choices.length?'unsupported':null;
            if(input.checked)return 'confirmed';
            if(input.disabled || choice.classList.contains('is-disabled'))return 'unsupported';
            choice.click();
            return null;
        }
        if(site==='qiehao'&&location.origin==='https://om.qq.com'&&location.pathname==='/main/creation/article') {
            const group=uniq(select(document,'.omui-form__group').filter(e=>label(e.querySelector('label.omui-form__label')||e)==='自主声明'));
            if(!group)return null;
            const names={none:'无需标注',ai:'该文章由AI生成',repost:'内容为转载',fiction:'虚构演绎，仅供娱乐',opinion:'个人观点，仅供参考'};
            const current=uniq(select(group,'.omui-contentbutton__title'));
            const selected=current?label(current).replace(/^作者声明[：:]\s*/,''):null;
            const dialog=uniq(select(document,'.omui-dialog-wrapper.open .omui-dialog').filter(e=>
                label(e.querySelector('.omui-dialog-header')||e)==='发布内容自主声明'));
            if(!dialog) {
                if(selected===names[value])return 'confirmed';
                if(selected)return 'conflict';
                const open=uniq(select(group,'.omui-contentbutton__footer button').filter(e=>label(e)==='添加内容自主声明'&&!e.disabled));
                if(value==='none' && open && window.__zhigaoConfirmedQiehaoDeclaration==='none')return 'confirmed';
                if(open)open.click();
                return null;
            }
            const choices=select(dialog,'.omui-radio');
            const choice=uniq(choices.filter(e=>label(e)===names[value]));
            const input=choice?.querySelector('input[type=radio]');
            if(!input)return choices.length?'unsupported':null;
            if(input.disabled)return 'unsupported';
            if(!input.checked) {choice.click();return null;}
            const confirm=uniq(select(dialog,'button').filter(e=>label(e)==='确认'&&!e.disabled));
            if(confirm) {
                const applied=window.__zhigaoQiehaoDeclarationButtons??=new WeakSet();
                if(!applied.has(confirm)) {
                    applied.add(confirm);
                    window.__zhigaoConfirmedQiehaoDeclaration=value;
                    confirm.click();
                }
            }
        }
        return null;
    })";
}
