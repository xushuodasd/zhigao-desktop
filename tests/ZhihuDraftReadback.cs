using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Open a previously created draft from its exact edit link in a fresh controller.
// This probe only reads the DOM. It cannot upload, select a different article, or submit.
sealed class ZhihuDraftReadback : Form {
    readonly WebView2 page = new WebView2 { Dock=DockStyle.Fill };
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    static readonly XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    sealed class ReadTable {
        public string[][] rows {get;set;}
        public string before {get;set;}
        public string after {get;set;}
        public bool merged {get;set;}
    }
    sealed class Snapshot {
        public string title {get;set;}
        public string path {get;set;}
        public ReadTable[] tables {get;set;}
        public string[] atomic {get;set;}
        public string editorClass {get;set;}
        public string[] tableElements {get;set;}
        public string[] draftIndicators {get;set;}
    }
    static string TextOf(XElement elem) {
        return elem == null ? "" : String.Concat(elem.Descendants(w+"t").Select(n=>n.Value));
    }
    static string Norm(string s) {
        return Regex.Replace((s??"").Replace("\u200b","").Replace("\ufeff",""),@"\s+"," ").Trim();
    }
    static ReadTable[] WordTables(string filename) {
        using(var file=File.OpenRead(filename))
        using(var zip=new ZipArchive(file))
        using(var xml=zip.GetEntry("word/document.xml").Open()) {
            var body=XDocument.Load(xml).Descendants(w+"body").Single();
            var blocks=body.Elements().ToList();
            return blocks.Where(e=>e.Name==w+"tbl").Select(table=>new ReadTable {
                rows=table.Elements(w+"tr").Select(row=>row.Elements(w+"tc").Select(TextOf).ToArray()).ToArray(),
                before=TextOf(table.ElementsBeforeSelf(w+"p").LastOrDefault(p=>TextOf(p).Trim().Length>0)),
                after=TextOf(table.ElementsAfterSelf(w+"p").FirstOrDefault(p=>TextOf(p).Trim().Length>0))
            }).ToArray();
        }
    }
    const string Probe = @"(()=>{
        const editor=document.querySelector('.public-DraftEditor-content[contenteditable=true]');
        const title=document.querySelector('textarea[placeholder*=标题],input[placeholder*=标题]');
        if(!editor||!title||!editor.textContent.trim())return null;
        const visible=e=>e.getClientRects().length && getComputedStyle(e).visibility!=='hidden';
        const sel='table,[role=table],[role=grid]';
        const rootTables=[...editor.querySelectorAll(sel)].filter(t=>visible(t)&&!t.parentElement.closest(sel));
        const outputs=rootTables.map(t=>{
            const before=document.createRange(),after=document.createRange();
            before.selectNodeContents(editor);before.setEndBefore(t);
            after.selectNodeContents(editor);after.setStartAfter(t);
            const rows=[...t.querySelectorAll('tr,[role=row]')].filter(r=>visible(r)&&r.closest(sel)===t);
            return {rows:rows.map(r=>[...r.querySelectorAll('th,td,[role=cell],[role=columnheader],[role=rowheader],[role=gridcell]')]
                       .filter(c=>visible(c)&&c.closest('tr,[role=row]')===r).map(c=>c.innerText)),
                    merged:[...t.querySelectorAll('[rowspan],[colspan],[aria-colspan],[aria-rowspan]')].some(c=>
                       ['rowspan','colspan','aria-colspan','aria-rowspan'].some(a=>c.hasAttribute(a)&&Number(c.getAttribute(a))!==1)),
                    before:before.toString(),after:after.toString()};
        });
        const atomic=[...editor.querySelectorAll('[contenteditable=false],iframe,figure,[class*=able]')].slice(0,36)
                     .map(n=>n.tagName+'.'+String(n.className).slice(0,90));
        const draftIndicators=[...document.querySelectorAll('span,div,button')].filter(n=>{
            if(!visible(n)||editor.contains(n))return false;
            const text=(n.innerText||'').trim();
            return text.length>0&&text.length<45&&/(刚刚|分钟前|秒前).*草稿|已保存|保存成功|草稿已同步|草稿已保存/.test(text)&&
                ![...n.children].some(c=>c.innerText?.trim()===text);
        }).map(n=>(n.innerText||'').trim());
        return {path:location.pathname,title:title.value,tables:outputs,atomic,editorClass:editor.className,
                tableElements:rootTables.map(t=>t.tagName+'.'+t.className),draftIndicators:[...new Set(draftIndicators)].slice(0,6)};
    })()";
    async Task Run(string filename,string url) {
        var original=WordTables(filename);
        if(original.Length!=1 || original[0].rows.Length!=4 || original[0].rows.Any(row=>row.Length!=4))
            throw new InvalidDataException("此项验收需要原稿有一张 4×4 表格。");
        string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Zhigao","WebView2");
        var env=await CoreWebView2Environment.CreateAsync(null,profile);
        await page.EnsureCoreWebView2Async(env);
        var core=page.CoreWebView2;
        core.NewWindowRequested+=(s,e)=>{e.Handled=true;};
        await core.CallDevToolsProtocolMethodAsync("Network.enable","{}");
        await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled","{\"cacheDisabled\":true}");
        core.Navigate(url);
        var clock=Stopwatch.StartNew();
        Snapshot last=null;
        while(clock.Elapsed.TotalSeconds<25) {
            if(page.Source!=null && page.Source.AbsoluteUri.TrimEnd('/')==url) {
                last=json.Deserialize<Snapshot>(await page.ExecuteScriptAsync(Probe));
                if(last!=null && last.tables!=null && last.title==Path.GetFileNameWithoutExtension(filename) && last.tables.Length==original.Length)
                    break;
            }
            await Task.Delay(450);
        }
        if(last==null)throw new InvalidOperationException("未能在已登录会话读到指定草稿。");
        if(last.path.TrimEnd('/')!=new Uri(url).AbsolutePath || last.title!=Path.GetFileNameWithoutExtension(filename) || last.tables.Length!=original.Length)
            throw new InvalidDataException("回读路由 "+last.path+"，标题 "+last.title+"，可见表格 "+last.tables.Length+"。编辑器节点 "+String.Join(", ",last.atomic));
        var table=last.tables[0];
        var wanted=original[0];
        if(table.merged||table.rows.Length!=4||table.rows.Any(r=>r.Length!=4))throw new InvalidDataException("草稿表格的行列或合并状态与 Word 不一致。");
        for(int r=0;r<4;r++)for(int c=0;c<4;c++)
            if(Norm(table.rows[r][c])!=Norm(wanted.rows[r][c])) throw new InvalidDataException("第 "+(r+1)+" 行第 "+(c+1)+" 列与 Word 不一致。");
        if(!Norm(table.before).EndsWith(Norm(wanted.before),StringComparison.Ordinal)||!Norm(table.after).StartsWith(Norm(wanted.after),StringComparison.Ordinal))
            throw new InvalidDataException("表格的相邻段落与 Word 不一致。草稿表前尾部 "+Norm(table.before).Substring(Math.Max(0,Norm(table.before).Length-110))+"；草稿表后开头 "+Norm(table.after).Substring(0,Math.Min(110,Norm(table.after).Length))+"。");
        Console.WriteLine(json.Serialize(new{draft=url,title=last.title,rows=table.rows.Length,columns=4,matchedCells=16,before=wanted.before,after=wanted.after,
            cells=table.rows,editorClass=last.editorClass,tableElements=last.tableElements,draftIndicators=last.draftIndicators}));
    }
    ZhihuDraftReadback(string file,string url) {
        Width=1024;Height=768;ShowInTaskbar=false;Opacity=0;Controls.Add(page);
        Shown+=async delegate {try{await Run(file,url);}catch(Exception e){Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}finally{Close();}};
    }
    [STAThread] static void Main(string[] args) {
        if(args.Length!=2||!Regex.IsMatch(args[1],@"^https://zhuanlan\.zhihu\.com/p/\d+/edit/?$")) { Environment.ExitCode=2;return; }
        Application.EnableVisualStyles();Application.Run(new ZhihuDraftReadback(args[0],args[1].TrimEnd('/')));
    }
}
