using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    readonly Dictionary<string,string> creatorProfileNames=new Dictionary<string,string>(StringComparer.Ordinal);
    readonly Dictionary<string,DateTime> nextCreatorProfileRead=new Dictionary<string,DateTime>(StringComparer.Ordinal);
    readonly HashSet<string> readingCreatorProfiles=new HashSet<string>(StringComparer.Ordinal);
    readonly HashSet<string> verifiedCreatorProfiles=new HashSet<string>(StringComparer.Ordinal);
    readonly SemaphoreSlim creatorProfileSlots=new SemaphoreSlim(3);
    string CreatorProfileFile {get{return Path.Combine(dataFolder,"creator-profiles.json");}}

    string CreatorProfileName(string platformId) {
        string name;
        return creatorProfileNames.TryGetValue(platformId,out name)?name:null;
    }

    static string NormalizeCreatorProfileName(string name) {
        name=Regex.Replace((name??"").Trim(),@"\s+"," ");
        if(name.Length==0 || name.Length>100 || name.Any(Char.IsControl) ||
            name=="未登录" || name=="登录" || name=="当前登录账号" || name=="创作者中心")return null;
        return name;
    }

    void LoadCreatorProfiles() {
        try {
            if(!File.Exists(CreatorProfileFile))return;
            var names=json.Deserialize<Dictionary<string,string>>(File.ReadAllText(CreatorProfileFile));
            foreach(var site in CreatorSites) {
                string value;
                if(names!=null && names.TryGetValue(site.Id,out value)) {
                    var name=NormalizeCreatorProfileName(value);
                    if(name!=null)creatorProfileNames[site.Id]=name;
                }
            }
        } catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        catch(ArgumentException) { }
        catch(InvalidOperationException) { }
    }

    void SaveCreatorProfileName(CreatorSite site,string name) {
        name=NormalizeCreatorProfileName(name);
        if(name==null || name==CreatorProfileName(site.Id))return;
        if(site.Id=="sohu")sohuDraftAccess=SohuDraftAccess.Unknown;
        creatorProfileNames[site.Id]=name;
        try {
            Directory.CreateDirectory(dataFolder);
            WriteCsdnAttempt(CreatorProfileFile,json.Serialize(creatorProfileNames));
        } catch(IOException) { }
        catch(UnauthorizedAccessException) { }
    }

    static string CreatorProfileAddress(CreatorSite site) {
        switch(site.Id) {
            case "weibo": return "https://me.weibo.com/content/article";
            case "wangyi": return WangyiCreatorUi+"/content-manage";
            default: return site.Home;
        }
    }

    Task RefreshCreatorProfilesOnStartup() {
        return Task.WhenAll(CreatorSites.OrderByDescending(site=>rememberedCreators.Contains(site.Id)).Select(site=>RefreshCreatorProfile(site,false)));
    }

    async Task RefreshCreatorProfile(CreatorSite site,bool force) {
        DateTime next;
        if(IsDisposed || browserEnvironment==null || readingCreatorProfiles.Contains(site.Id) ||
            (!force && nextCreatorProfileRead.TryGetValue(site.Id,out next) && next>DateTime.UtcNow))return;
        readingCreatorProfiles.Add(site.Id);
        long proof=CreatorConnectionProof(site.Id);
        Render();
        await creatorProfileSlots.WaitAsync();
        try {
            if(IsDisposed)return;
            nextCreatorProfileRead[site.Id]=DateTime.UtcNow.AddMinutes(1);
            using(var view=new WebView2 {Visible=false,Width=1200,Height=850}) {
                platform.Controls.Add(view);
                await InitCreatorView(view,site,allowPopups:false);
                string name=await ReadCreatorProfileName(view,site);
                if(IsDisposed)return;
                if(name!=null && (!creatorSignInRequired.Contains(site.Id) || CreatorConnectionProof(site.Id)==proof)) {
                    RememberCreator(site.Id);
                    verifiedCreatorProfiles.Add(site.Id);
                    nextCreatorProfileRead[site.Id]=DateTime.UtcNow.AddMinutes(15);
                    SaveCreatorProfileName(site,name);
                    if(site.Id=="sohu")await RefreshSohuDraftAccess(site,view);
                } else if(IsCreatorLoginPage(site,view.Source) && CreatorConnectionProof(site.Id)==proof) {
                    MarkCreatorSignInRequired(site);
                }
            }
        } catch(InvalidOperationException) { }
        catch(System.Runtime.InteropServices.COMException) { }
        finally {
            creatorProfileSlots.Release();
            readingCreatorProfiles.Remove(site.Id);
            if(!IsDisposed)Render();
        }
    }

    async Task<string> ReadCreatorProfileName(WebView2 view,CreatorSite site) {
        view.CoreWebView2.Navigate(CreatorProfileAddress(site));
        string name=null;
        try {
            await Wait(async()=>{
                var address=view.Source;
                if(IsCreatorLoginPage(site,address)) {name=null;return true;}
                if(address==null || address.Scheme!="https" || !site.Allows(address.AbsoluteUri))return false;
                var raw=await view.ExecuteScriptAsync(CreatorProfileScript+"("+json.Serialize(site.Id)+")");
                if(address!=view.Source)return false;
                name=NormalizeCreatorProfileName(json.Deserialize<string>(raw));
                return name!=null;
            },8,"平台账号名称未就绪。");
            return name;
        } catch(TimeoutException) {return null;}
        catch(ArgumentException) {return null;}
    }

    const string CreatorProfileScript=@"((site) => {
        const {origin,pathname}=location;
        const path=pathname.replace(/\/$/,'');
        const visible=e=>e.getClientRects().length&&!e.closest('[hidden],[aria-hidden=true]');
        const unique=(root,selector)=>{const matches=[...root.querySelectorAll(selector)].filter(visible);return matches.length===1?matches[0]:null;};
        if(site==='toutiao'&&origin==='https://mp.toutiao.com'&&['/profile_v4/index','/profile_v4'].includes(path))
            return unique(document,'.user-auth-avator .auth-avator-name')?.textContent.trim()||null;
        if(site==='qiehao'&&origin==='https://om.qq.com'&&path==='/main')
            return unique(document,'[class*=""loginInfo-""] [class*=""usernameText-""]')?.textContent.trim()||null;
        if(site==='sohu'&&origin==='https://mp.sohu.com'&&path==='/mpfe/v4/contentManagement/first/page')
            return unique(document,'.user-info-wrap .user-head .user-name')?.textContent.trim()||null;
        if(site==='weibo'&&origin==='https://me.weibo.com'&&path==='/content/article') {
            const avatar=unique(document,'.woo-avatar-main[class*=""_avatar_""] .woo-avatar-img');
            const nameBox=avatar?.parentElement?.parentElement;
            return nameBox?.classList.contains('woo-box-alignCenter')?nameBox.innerText.trim()||null:null;
        }
        const baijiahao=site==='baijiahao'&&origin==='https://baijiahao.baidu.com'&&path==='/builder/rc/home';
        const wangyi=site==='wangyi'&&origin==='https://mp.163.com'&&path==='/subscribe_v4/index.html';
        if(!baijiahao&&!wangyi)return null;
        if(!window.__zhigaoCreatorProfile) {
            const profile={name:null};
            window.__zhigaoCreatorProfile=profile;
            fetch(baijiahao?'/builder/app/appinfo':'/wemedia/navinfo.do',{cache:'no-store',signal:AbortSignal.timeout(5500)})
                .then(r=>r.ok?r.json():null).then(body=>{
                    const value=baijiahao?(body?.errno===0?body.data?.user?.username:null):(body?.code===1?body.data?.tname:null);
                    if(typeof value==='string') profile.name=value;
                }).catch(()=>{});
        }
        return window.__zhigaoCreatorProfile.name;
    })";
}
