using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string CreatorListUrl = "https://mp.csdn.net/mp_blog/manage/article";

    internal sealed class CsdnListing {
        public string State = "unknown", Id, Url;
    }
    sealed class CreatorRow {
        public string id { get; set; }
        public string name { get; set; }
        public string href { get; set; }
        public string text { get; set; }
    }
    sealed class CreatorTab {
        public int total { get; set; }
        public CreatorRow[] rows { get; set; }
    }

    async Task<CsdnListing> FindCsdnArticle(string title,string articleId,DateTime? legacyTime) {
        var unknown = new CsdnListing();
        if (browserEnvironment == null || (String.IsNullOrEmpty(articleId) && !legacyTime.HasValue)) return unknown;
        // Read-only content management view with the same account; never navigate the active draft.
        using (var listing = new WebView2 { Visible = false }) {
            platform.Controls.Add(listing);
            try {
                var options = browserEnvironment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = "Zhigao-CSDN";
                await listing.EnsureCoreWebView2Async(browserEnvironment, options);
                listing.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                listing.CoreWebView2.NewWindowRequested += (s,e) => { e.Handled = true; };
                listing.CoreWebView2.Navigate(CreatorListUrl);
                bool byId = !String.IsNullOrEmpty(articleId);
                foreach (var tab in new[] { "已发布","审核中/未通过","草稿箱" }) {
                    var data = await ReadCreatorTab(listing,tab);
                    if (data == null) return unknown;
                    var matches = new List<CreatorRow>();
                    foreach (var row in data.rows) {
                        if (row.name == title && (!byId || row.id == articleId)) matches.Add(row);
                    }
                    if (matches.Count == 0) {
                        // If there are extra pages an unseen homonym prevents legacy matching.
                        if (!byId && data.total != data.rows.Length) return unknown;
                        continue;
                    }
                    if (!byId) {
                        if (tab != "草稿箱" || data.total != data.rows.Length) return unknown;
                        matches.RemoveAll(r => !SameAttemptMinute(r.text,legacyTime.Value));
                    }
                    if (matches.Count != 1) return unknown;
                    var match = matches[0];
                    if (tab == "草稿箱" && Regex.IsMatch(match.text,@"(?:^|\s)草稿(?:$|\s)"))
                        return new CsdnListing { State="draft",Id=match.id };
                    if (tab == "已发布" && IsCsdnReceipt(match.href,match.id) && !Regex.IsMatch(match.text,"仅我可见|私密|粉丝可见|VIP可见"))
                        return new CsdnListing { State="published",Id=match.id,Url=new Uri(match.href).GetLeftPart(UriPartial.Path) };
                    if (tab == "审核中/未通过") {
                        if (Regex.IsMatch(match.text,"未通过|已驳回|审核拒绝")) return new CsdnListing { State="rejected",Id=match.id };
                        if (Regex.IsMatch(match.text,"审核中|待审核|审核通过后")) return new CsdnListing { State="review",Id=match.id };
                    }
                    return unknown;
                }
            } catch (TimeoutException) { return unknown; } catch (InvalidOperationException) { return unknown; } catch (System.Runtime.InteropServices.COMException) {
                return unknown;
            } finally { platform.Controls.Remove(listing); }
        }
        return unknown;
    }
    static bool SameAttemptMinute(string text,DateTime local) {
        DateTime parsed;
        var stamp = Regex.Match(text,@"\b\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\b");
        return stamp.Success && DateTime.TryParseExact(stamp.Value,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)
            && parsed.ToString("yyyyMMddHHmm") == local.ToString("yyyyMMddHHmm");
    }
    async Task<CreatorTab> ReadCreatorTab(WebView2 view,string tab) {
        const string originGuard = "if(location.origin!=='https://mp.csdn.net'||location.pathname.replace(/\\/$/,'')!=='/mp_blog/manage/article')return null;";
        string prefix = "(()=>{" + originGuard + "const key="+json.Serialize(tab)+";const el=[...document.querySelectorAll('.nav-link')].find(n=>n.textContent.trim().split('(')[0]===key);if(!el)return null;";
        string script = prefix + @"if(!el.classList.contains('active')){el.click();return null;}
            const count=el.textContent.match(/\((\d+)\)/); if(!count)return null;
            const total=Number(count[1]);const rows=[];
            for(const card of document.querySelectorAll('.article-list-item-txt')){
                const a=card.querySelector('a[href]');const block=card.closest('.flex-cont');
                if(!a||!block)return null;
                const url=new URL(a.href);
                let id;
                if(url.protocol==='https:'){
                    if(url.hostname==='editor.csdn.net' && /^\/md\/?$/.test(url.pathname))
                        id=url.searchParams.get('articleId');
                    if(url.hostname==='mp.csdn.net')
                        id=url.pathname.match(/^\/mp_blog\/creation\/editor\/(\d+)\/?$/)?.[1];
                    if(url.hostname==='blog.csdn.net')
                        id=url.pathname.match(/^\/[\w-]+\/article\/details\/(\d+)\/?$/)?.[1];
                }
                if(!/^\d+$/.test(id||''))return null;
                const text=block.innerText||block.textContent;
                if(key==='草稿箱' && !/(^|\s)草稿(\s|$)/.test(text))return null;
                if(key==='审核中/未通过' && !/待审核|审核中|未通过|驳回|审核拒绝/.test(text))return null;
                if(key==='已发布' && /(?:^|\s)(?:草稿|审核中|未通过)(?:$|\s)/.test(text))return null;
                const actions=card.closest('.list-item-mp-right')||block;
                const publicLink=[...actions.querySelectorAll('a[href]')].map(n=>new URL(n.href)).find(u=>
                    u.origin==='https://blog.csdn.net' && u.pathname.match(/^\/[\w-]+\/article\/details\/(\d+)\/?$/)?.[1]===id);
                rows.push({id,name:a.textContent.trim(),href:publicLink?publicLink.origin+publicLink.pathname:'',text});
            }
            if((total===0)!==(rows.length===0) || rows.length>total)return null;
            return {total,rows};})()";
        CreatorTab found = null;
        string previous = null;
        int stable = 0;
        await Wait(async () => {
            string raw = await view.ExecuteScriptAsync(script);
            if (raw != previous) stable=0;
            previous=raw;
            if (!raw.StartsWith("{",StringComparison.Ordinal) || ++stable < 3) return false;
            found = json.Deserialize<CreatorTab>(raw);
            return found != null && found.rows != null;
        },20,tab+"列表未就绪");
        return found;
    }
}

