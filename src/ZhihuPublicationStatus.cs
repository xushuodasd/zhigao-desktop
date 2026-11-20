using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    readonly WebView2 zhihuPublicPage = new WebView2 { Visible=false };
    readonly SemaphoreSlim zhihuStatusGate = new SemaphoreSlim(1);
    readonly Dictionary<string,DateTime> zhihuLastChecked = new Dictionary<string,DateTime>();
    bool refreshingZhihuDrafts, zhihuPublicPageReady;

    static bool IsZhihuArticleUrl(Uri address) {
        return address!=null && address.Scheme=="https" && address.Host=="zhuanlan.zhihu.com" &&
            address.IsDefaultPort && address.UserInfo.Length==0 && Regex.IsMatch(address.AbsolutePath,@"\A/p/[0-9]+/?\z");
    }

    static string PublishedAddressFor(Entry entry) {
        return entry.platform=="知乎" && entry.label=="草稿" && IsZhihuEditUrl(entry.link)
            ? entry.link.Substring(0,entry.link.Length-5) : null;
    }

    async Task<bool> CheckZhihuPublicPage(string expected) {
        await zhihuStatusGate.WaitAsync();
        try {
            if (IsDisposed || browserEnvironment==null) return false;
            if (!zhihuPublicPageReady) {
                platform.Controls.Add(zhihuPublicPage);
                await zhihuPublicPage.EnsureCoreWebView2Async(browserEnvironment);
                zhihuPublicPage.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled=false;
                zhihuPublicPage.CoreWebView2.NewWindowRequested+=(sender,e)=>{ e.Handled=true; };
                await zhihuPublicPage.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable","{}");
                await zhihuPublicPage.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled","{\"cacheDisabled\":true}");
                zhihuPublicPageReady=true;
            }
            var core=zhihuPublicPage.CoreWebView2;
            var completion=new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> finished=(sender,e)=>
                completion.TrySetResult(e.IsSuccess && e.HttpStatusCode >= 200 && e.HttpStatusCode < 300);
            core.NavigationCompleted+=finished;
            try {
                core.Navigate(expected);
                if (await Task.WhenAny(completion.Task,Task.Delay(8500))!=completion.Task) {
                    core.Stop();
                    return false;
                }
                if (!await completion.Task) return false;
                return await zhihuPublicPage.ExecuteScriptAsync("(() => {const expected="+json.Serialize(expected)+@";
                    if(location.origin+location.pathname.replace(/\/$/,'')!==expected)return false;
                    const canonical=document.querySelector('link[rel=canonical]');
                    if(canonical){const url=new URL(canonical.href);if(url.origin+url.pathname.replace(/\/$/,'')!==expected)return false;}
                    const heading=document.querySelector('h1.Post-Title');
                    const body=document.querySelector('.Post-RichTextContainer,[itemprop=articleBody]');
                    return Boolean(heading?.textContent.trim() && body && (body.textContent.trim() || body.querySelector('img,video')));})()")=="true";
            } finally { core.NavigationCompleted-=finished; }
        } catch(InvalidOperationException) { return false; }
        catch(System.Runtime.InteropServices.COMException) { return false; }
        finally { zhihuStatusGate.Release(); }
    }

    async Task<bool> ReconcileZhihuDraft(Entry entry) {
        string expected=PublishedAddressFor(entry);
        if (expected==null) return entry.label=="已发布";
        if (!await CheckZhihuPublicPage(expected)) return false;
        if (IsDisposed) return false;
        foreach (var draft in records.FindAll(r=>PublishedAddressFor(r)==expected)) {
            if (PublishedAddressFor(draft)==expected) SaveVerification(draft,"已发布 · "+expected);
        }
        Render();
        return true;
    }

    async Task RefreshZhihuDrafts(bool returning = false) {
        if (refreshingZhihuDrafts || busy || picking || verifying || browserEnvironment==null) return;
        var candidates=records.FindAll(r=>PublishedAddressFor(r)!=null);
        refreshingZhihuDrafts=true;
        try {
            foreach (var entry in candidates) {
                if (IsDisposed || busy || picking || verifying) break;
                string address=PublishedAddressFor(entry);
                if (address==null) continue;
                DateTime last;
                if (!returning && zhihuLastChecked.TryGetValue(address,out last) &&
                    DateTime.UtcNow-last<TimeSpan.FromSeconds(30)) continue;
                zhihuLastChecked[address]=DateTime.UtcNow;
                await ReconcileZhihuDraft(entry);
            }
        } catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        finally { refreshingZhihuDrafts=false; }
    }

    async Task VerifyZhihuRecord(Entry entry) {
        if (PublishedAddressFor(entry)==null || browserEnvironment==null) return;
        verifying=true;
        verificationNotice="正在核实知乎文章状态";
        Render();
        try {
            verificationNotice=await ReconcileZhihuDraft(entry)?"知乎已发布，记录已更新。":
                "暂未取得该草稿编号的公开发布回执。";
        } catch(IOException) { verificationNotice="知乎已发布，本地记录写入失败。"; }
        catch(UnauthorizedAccessException) { verificationNotice="知乎已发布，本地记录写入失败。"; }
        finally { verifying=false; if(!IsDisposed)Render(); }
    }
}
