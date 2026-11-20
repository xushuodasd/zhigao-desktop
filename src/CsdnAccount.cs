using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    readonly WebView2 csdnAccountPage = new WebView2 { Visible=false };
    string csdnHandle, csdnNickname;
    DateTime nextCsdnNicknameCheck;

    string CsdnAccountName {
        get { return csdnState=="editor" ? csdnNickname ?? csdnHandle : null; }
    }

    sealed class CsdnEditorInfo {
        public string state { get; set; }
        public string handle { get; set; }
    }

    // The Markdown editor exposes the account handle on its creator-center avatar.
    const string CsdnAvatarIdentity = @"
        const avatar=[...document.querySelectorAll('a[href]')].find(a=>
            a.origin==='https://mp.csdn.net' && a.pathname==='/mp_blog/manage/article' && visible(a) && a.querySelector('img'));
        const image=avatar?.querySelector('img');
        const accountHandle=[image?.alt,image?.title,avatar?.title].find(value=>/^[\w-]{1,64}$/.test(value?.trim()||''))?.trim()||null;";

    void UpdateCsdnHandle(string handle) {
        if (handle!=null && !Regex.IsMatch(handle,@"\A[A-Za-z0-9_-]{1,64}\z")) handle=null;
        if (String.Equals(csdnHandle,handle,StringComparison.OrdinalIgnoreCase)) return;
        csdnHandle=handle;
        csdnNickname=null;
        ClearCsdnMetrics();
        nextCsdnNicknameCheck=DateTime.MinValue;
        Render();
    }

    async Task CheckCsdnNickname() {
        string handle=csdnHandle;
        if (handle==null || browserEnvironment==null || DateTime.UtcNow<nextCsdnNicknameCheck) return;
        nextCsdnNicknameCheck=DateTime.UtcNow.AddMinutes(1);
        try {
            if(csdnAccountPage.CoreWebView2==null) {
                platform.Controls.Add(csdnAccountPage);
                var options=browserEnvironment.CreateCoreWebView2ControllerOptions();
                options.ProfileName="Zhigao-CSDN";
                await csdnAccountPage.EnsureCoreWebView2Async(browserEnvironment,options);
                csdnAccountPage.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled=false;
                csdnAccountPage.CoreWebView2.NewWindowRequested+=(s,e)=>{e.Handled=true;};
            }
            var core=csdnAccountPage.CoreWebView2;
            var loaded=new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> completed=(s,e)=>
                loaded.TrySetResult(e.IsSuccess && e.HttpStatusCode>=200 && e.HttpStatusCode<300);
            core.NavigationCompleted+=completed;
            string name;
            try {
                core.Navigate("https://blog.csdn.net/"+handle);
                if (await Task.WhenAny(loaded.Task,Task.Delay(7000))!=loaded.Task) { core.Stop(); return; }
                if (!await loaded.Task) return;
                string raw=await csdnAccountPage.ExecuteScriptAsync("(() => {const handle="+json.Serialize(handle)+@";
                    if(location.origin!=='https://blog.csdn.net'||location.pathname.replace(/\/$/,'').toLowerCase()!=='/'+handle.toLowerCase())return null;
                    const nickname=document.querySelector('.user-profile-head-name,.profile-intro-name,.blogger-name')?.textContent.trim();
                    const title=document.title.match(/^(.+?)\s*-\s*CSDN博客$/)?.[1]?.trim();
                    return nickname||title||null;
                })()");
                name=json.Deserialize<string>(raw);
            } finally { core.NavigationCompleted-=completed; }
            if (String.IsNullOrWhiteSpace(name) || name.Length>80 || IsDisposed || csdnState!="editor" || !IsCsdnEditor(csdn.Source)) return;
            string current=await csdn.ExecuteScriptAsync(@"(() => {
                if(location.origin!=='https://editor.csdn.net')return null;
                const visible=node=>!!node.getClientRects().length;
                "+CsdnAvatarIdentity+@"
                return accountHandle;
            })()");
            if (!String.Equals(json.Deserialize<string>(current),handle,StringComparison.OrdinalIgnoreCase)) return;
            nextCsdnNicknameCheck=DateTime.UtcNow.AddMinutes(5);
            if (csdnNickname==name) return;
            csdnNickname=name;
            Render();
        } catch(InvalidOperationException) { }
        catch(System.Runtime.InteropServices.COMException) { }
    }
}
