using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    string[] verifiedZhihuTopics=new string[0];
    static bool IsZhihuEditUrl(string url) {
        return url!=null && Regex.IsMatch(url,@"\Ahttps://zhuanlan\.zhihu\.com/p/[0-9]+/edit\z");
    }

    string ZhihuDraftReceiptPath() {
        var directory=Path.Combine(dataFolder,"Drafts-Zhihu");
        Directory.CreateDirectory(directory);
        // A renamed Word has a different intended title. Keep its draft separate.
        string key=Path.GetFileNameWithoutExtension(attempt)+"\n"+title;
        using (var sha=SHA256.Create())
            return Path.Combine(directory,BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key)))+".txt");
    }

    async Task CheckZhihuDraftReadback(string url,string[] topics) {
        if (!IsZhihuEditUrl(url)) throw new InvalidDataException("知乎没有返回可核对的草稿编号。");
        // Keep the editing view alive so autosave can finish while a separate view reads it back.
        using (var view=new WebView2 { Visible=false }) {
            platform.Controls.Add(view);
            try {
                await view.EnsureCoreWebView2Async(browserEnvironment);
                await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable","{}");
                await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled","{\"cacheDisabled\":true}");
                for (int n=0;n<3;n++) {
                    if (n==0) view.CoreWebView2.Navigate(url); else view.CoreWebView2.Reload();
                    try {
                        await Wait(async ()=>view.Source!=null && view.Source.AbsoluteUri==url
                            && Matches(await Contents(view),true)
                            && (topics.Length==0 || await ZhihuDraftTopicsMatch(view,topics)),8,"重新打开后，知乎草稿的标题、图文或已选话题尚未核对通过。");
                        return;
                    } catch (TimeoutException) { if(n==2)throw; }
                }
            } finally { platform.Controls.Remove(view); }
        }
    }

    async Task<string> PrepareZhihuWord(string file,bool draftOnly,string existingUrl=null) {
        using (var source=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            LoadZhihuSource(file);
            if(!draftOnly)CheckZhihuAttempt();
            string marker=ZhihuDraftReceiptPath();
            using (File.Open(marker+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                string saved=File.Exists(marker)?File.ReadAllText(marker).Trim():null;
                bool replaceMarker=false;
                if (draftOnly && saved!=null && File.Exists(attempt)) {
                    string published=File.ReadAllText(attempt).TrimEnd('/');
                    if (published.Length==0 || saved==published+"/edit") {
                        saved=null;
                        replaceMarker=true;
                    }
                }
                if(existingUrl!=null && saved!=null && existingUrl!=saved)
                    throw new InvalidDataException("这份 Word 已关联另一份知乎草稿。");
                string url=saved??existingUrl;
                if (url!=null) {
                    if (!IsZhihuEditUrl(url)) throw new InvalidDataException("已记录的知乎草稿链接无效。");
                    string publishedAddress=url.Substring(0,url.Length-5);
                    if (!draftOnly && records.Exists(r=>r.platform=="知乎" && r.label=="已发布" && r.link==publishedAddress))
                        throw new PriorAttempt("已发布 · "+publishedAddress);
                    status.Text="正在核对已有知乎草稿";
                    if(page.Source==null || page.Source.AbsoluteUri!=url) page.CoreWebView2.Navigate(url);
                    await Wait(async ()=>page.Source!=null && page.Source.AbsoluteUri==url && Matches(await Contents(),true) && await Act("unpublished-draft"),
                        20,"当前知乎账号无法确认这仍是与 Word 一致的未公开草稿。请检查稿件状态和改动。");
                    prepared=true;
                } else {
                    await ImportDocument(file,draftOnly);
                    await Wait(()=>Task.FromResult(page.Source!=null && IsZhihuEditUrl(page.Source.AbsoluteUri)),15,"知乎尚未返回草稿编号。");
                    url=page.Source.AbsoluteUri;
                }
                status.Text="正在核对知乎文章话题";
                string[] topics=await SetZhihuDraftTopics();
                status.Text="正在重新打开草稿，核对标题、正文和表格";
                await CheckZhihuDraftReadback(url,topics);
                verifiedZhihuTopics=topics;
                if (saved==null) {
                    string temp=marker+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try {
                        File.WriteAllText(temp,url);
                        if(replaceMarker)File.Replace(temp,marker,null);else File.Move(temp,marker);
                    }
                    finally { if(File.Exists(temp))File.Delete(temp); }
                }
                return url;
            }
        }
    }
}
