using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Offline Word -> editor DOM -> production integrity check. No external requests or publishing.
partial class LoginWindow : Form {
    const string WriteUrl = "https://zhuanlan.zhihu.com/write";
    readonly WebView2 page = new WebView2 { Dock=DockStyle.Fill };
    readonly Label status = new Label();
    static readonly XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static XElement P(string text) { return new XElement(w+"p",new XElement(w+"r",new XElement(w+"t",text))); }
    static XDocument Sample() {
        var table = new XElement(w+"tbl",Enumerable.Range(0,4).Select(row => new XElement(w+"tr",
            Enumerable.Range(0,4).Select(col => new XElement(w+"tc",P(row == 1 && col == 1 ? "" : row+"-"+col))))));
        return new XDocument(new XElement(w+"document",new XElement(w+"body",P("1. 表格前"),table,P("2. 表格后"))));
    }
    static XDocument ReadDoc(string file) {
        using (var stream = File.OpenRead(file))
        using (var zip = new ZipArchive(stream))
        using (var xml = zip.GetEntry("word/document.xml").Open()) return XDocument.Load(xml);
    }
    static string TextOf(XElement node) { return String.Concat(node.Descendants(w+"t").Select(n => n.Value)); }
    static string Html(XDocument doc) {
        var editor = new XElement("div",new XAttribute("class","public-DraftEditor-content"),new XAttribute("contenteditable","true"));
        foreach (var block in doc.Descendants(w+"body").Single().Elements()) {
            if (block.Name == w+"tbl") {
                editor.Add(new XElement("table",block.Elements(w+"tr").Select(row => new XElement("tr",
                    row.Elements(w+"tc").Select(cell => new XElement("td",new XElement("span",TextOf(cell))))))));
            } else if (block.Name == w+"p") {
                var value = TextOf(block);
                var prefix = Regex.Match(value,@"^\s*\d{1,4}\.\s+");
                var run = new XElement("span",new XAttribute("data-text","true"),value.Substring(prefix.Length));
                editor.Add(prefix.Success ? new XElement("ol",new XElement("li",new XAttribute("class","public-DraftStyleDefault-orderedListItem"),run)) : run);
            }
        }
        // Explicit end tags are required for empty spans/cells in HTML.
        foreach (var node in editor.Descendants().Where(e => e.IsEmpty).ToList()) node.Value = "";
        return "<!doctype html><meta charset=utf-8><textarea placeholder='标题'>样稿</textarea>"+editor;
    }
    async Task Check(string label,string script,bool expected,string detail) {
        if (script != null) await page.ExecuteScriptAsync(script);
        var content=await Contents();
        if (Matches(content,true) != expected || (!expected && !(Difference(content).Contains(detail))))
            throw new Exception(label+" 与预期不符。"+Difference(content));
        Console.WriteLine("PASS "+label+(expected ? "" : " · "+Difference(content)));
    }
    async Task Run(string file) {
        var doc = file == null ? Sample() : ReadDoc(file);
        ReadZhihuWord(doc);
        title="样稿";
        if (wordTables.Count != 1 || wordTables[0].rows.Length != 4 || wordTables[0].rows.Any(row => row.Length != 4))
            throw new Exception("测试输入需有一张 4×4 表格");
        var html = Html(doc);
        var env = await CoreWebView2Environment.CreateAsync(null,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"profile"),
            new CoreWebView2EnvironmentOptions("--proxy-server=127.0.0.1:9 --disable-background-networking"));
        await page.EnsureCoreWebView2Async(env);
        var core = page.CoreWebView2;
        core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (s,e) => {
            e.Response=env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(e.Request.Uri == WriteUrl ? html : "")),
                200,"OK","Content-Type: text/html; charset=utf-8");
        };
        var loaded=new TaskCompletionSource<bool>();
        core.NavigationCompleted+=(s,e)=>loaded.TrySetResult(e.IsSuccess);
        core.Navigate(WriteUrl);
        if (!await loaded.Task) throw new Exception("本地回归页未加载");
        Console.WriteLine("Word "+body.Length+" 字；4×4 表格始于第 "+(wordTables[0].start+1)+" 字");
        await Check("原位逐格",null,true,null);
        await Check("同文但列边界改变",
            "let tr=document.querySelector('tr'), a=tr.cells[0], b=tr.cells[1];window.origTable=tr.parentElement.closest('table').outerHTML;a.textContent+=b.textContent;b.textContent='';",false,"行第");
        await Check("位置改变","document.querySelector('table').outerHTML=window.origTable;document.querySelector('.public-DraftEditor-content').prepend(document.querySelector('table'))",false,"位置");
        await Check("表格缺失","document.querySelector('table').remove()",false,"知乎编辑器可核对 0 个");
    }
    LoginWindow(string file) {
        ShowInTaskbar=false; Width=600; Height=400; Controls.Add(page);
        Shown+=async delegate { try { await Run(file); } catch(Exception e) { Console.Error.WriteLine(e);Environment.ExitCode=1; } finally { Close(); } };
    }
    [STAThread] static void Main(string[] args) { Application.EnableVisualStyles(); Application.Run(new LoginWindow(args.Length==0 ? null : args[0])); }
}
