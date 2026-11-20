using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

partial class LoginWindow {
    const string CsdnBody = "pre.editor__inner[contenteditable=true]";
    const string CsdnTitle = "input.article-bar__title";
    const string CsdnImageInput = "input[type=file][accept^='image/gif']";
    static readonly Regex CsdnImageLink = new Regex(@"!\[[^\]\r\n]*\]\((https://i-blog\.csdnimg\.cn/[^\s)]+)\)");
    static readonly Regex CsdnArticleId = new Regex(@"(?:^\?|&)articleId=(\d+)(?:&|$)");

    sealed class CsdnDocument {
        public string Title;
        public string Markdown;
        public readonly List<string> Images = new List<string>();
    }

    static CsdnDocument ReadCsdnDocument(string file,string folder) {
        var doc = new CsdnDocument { Title = Path.GetFileNameWithoutExtension(file) };
        if (doc.Title.Length < 5 || doc.Title.Length > 100) throw new InvalidDataException("CSDN 标题须为 5 至 100 字，请调整文件名。");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        using (var stream = File.OpenRead(file))
        using (var zip = new ZipArchive(stream)) {
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) throw new InvalidDataException("文档正文无法读取。");
            XDocument content;
            using (var xml = entry.Open()) content = XDocument.Load(xml);
            var body = content.Descendants(w+"body").Single();
            if (body.Descendants(XName.Get("imagedata","urn:schemas-microsoft-com:vml")).Any())
                throw new InvalidDataException("此版本暂时无法可靠导入旧式 Word 图形。");
            var images = new Dictionary<string,string>();
            var rels = zip.GetEntry("word/_rels/document.xml.rels");
            if (rels != null) {
                using (var xml = rels.Open())
                    foreach (var rel in XDocument.Load(xml).Descendants(pr+"Relationship")) {
                        string type = (string)rel.Attribute("Type");
                        if (type == r.NamespaceName+"/image" && (string)rel.Attribute("TargetMode") != "External")
                            images[(string)rel.Attribute("Id")] = (string)rel.Attribute("Target");
                    }
            }
            var paragraphs = new List<string>();
            var blocks = body.Descendants().Where(block => (block.Name == w+"p" || block.Name == w+"tbl") &&
                !block.Ancestors().Any(parent => parent.Name == w+"tbl" || parent.Name == w+"p"));
            foreach (var paragraph in blocks) {
                if (paragraph.Name == w+"tbl") {
                    paragraphs.Add(RenderCsdnTable(paragraph,w,a));
                    continue;
                }
                var line = new StringBuilder();
                foreach (var part in paragraph.Descendants()) {
                    if (part.Name == w+"t") line.Append(part.Value);
                    else if (part.Name == w+"tab") line.Append("    ");
                    else if (part.Name == w+"br") line.Append("  \n");
                    else if (part.Name == a+"blip") {
                        string id = (string)part.Attribute(r+"embed"), target;
                        if (id == null || !images.TryGetValue(id,out target)) throw new InvalidDataException("文档引用了一张外部或无法读取的图片。");
                        var path = new Uri(new Uri("https://docx.invalid/word/document.xml"),target);
                        var image = path.Host == "docx.invalid" ? zip.GetEntry(Uri.UnescapeDataString(path.AbsolutePath.TrimStart('/'))) : null;
                        string ext = Path.GetExtension(path.AbsolutePath).ToLowerInvariant();
                        if (image == null || image.Length > 5L*1024*1024 || !new[]{".png",".jpg",".jpeg",".webp",".gif",".bmp"}.Contains(ext))
                            throw new InvalidDataException("Word 包含 CSDN 无法接收的图片。单张图片上限为 5 MB。");
                        string output = Path.Combine(folder,doc.Images.Count + ext);
                        using(var input=image.Open())
                        using(var outputStream=File.Create(output)) input.CopyTo(outputStream);
                        line.Append("\n\n\u0001").Append(doc.Images.Count).Append("\u0002\n\n");
                        doc.Images.Add(output);
                    }
                }
                string text = line.ToString().Trim();
                if (text.Length == 0) continue;
                var props = paragraph.Element(w+"pPr");
                var styleElement = props == null ? null : props.Element(w+"pStyle");
                string style = styleElement == null ? null : (string)styleElement.Attribute(w+"val");
                Match heading = Regex.Match(style ?? "",@"^Heading([1-6])$",RegexOptions.IgnoreCase);
                if (heading.Success) text = new string('#',Int32.Parse(heading.Groups[1].Value)) + " " + text;
                else if (props != null && props.Element(w+"numPr") != null) text = "- " + text;
                paragraphs.Add(text);
            }
            doc.Markdown = String.Join("\n\n",paragraphs);
            if (doc.Markdown.Length == 0) throw new InvalidDataException("文档没有可导入的正文或图片。");
        }
        return doc;
    }

    static string RenderCsdnTable(XElement table,XNamespace w,XNamespace a) {
        if (table.Descendants().Any(node => node.Name == w+"tbl" || node.Name == w+"gridSpan" ||
            node.Name == w+"vMerge" || node.Name == w+"hMerge" || node.Name == a+"blip"))
            throw new InvalidDataException("CSDN 仅支持直接导入普通文字表格。当前表格包含合并单元格、嵌套表格或图片。");

        var rows = table.Elements(w+"tr").ToArray();
        if (rows.Length == 0 || table.Descendants(w+"tr").Count() != rows.Length)
            throw new InvalidDataException("无法识别该 Word 表格的行。");
        int columns = rows[0].Elements(w+"tc").Count();
        var markdown = new List<string>();
        foreach (var row in rows) {
            var cells = row.Elements(w+"tc").ToArray();
            if (columns == 0 || cells.Length != columns || row.Descendants(w+"tc").Count() != columns)
                throw new InvalidDataException("CSDN 表格要求每行的列数一致。");
            markdown.Add("| " + String.Join(" | ",cells.Select(cell=>RenderCsdnCell(cell,w))) + " |");
            if (markdown.Count == 1)
                markdown.Add("| " + String.Join(" | ",Enumerable.Repeat("---",columns)) + " |");
        }
        return String.Join("\n",markdown);
    }

    static string RenderCsdnCell(XElement cell,XNamespace w) {
        return String.Join("<br>",cell.Descendants(w+"p").Select(paragraph => {
            var text = new StringBuilder();
            foreach (var part in paragraph.Descendants()) {
                if (part.Name == w+"br" || part.Name == w+"cr") text.Append("<br>");
                else if (part.Name == w+"tab") text.Append(" ");
                else if (part.Name == w+"t") {
                    foreach (char c in part.Value)
                        if ("&<>|\\`*_[]~!".IndexOf(c) >= 0) text.Append("&#").Append((int)c).Append(';');
                        else text.Append(c);
                }
            }
            return text.ToString().Trim();
        }));
    }

    static bool IsCsdnEditor(Uri page) {
        return page != null && page.Scheme=="https" && page.Host=="editor.csdn.net" &&
            (page.AbsolutePath=="/md/" || page.AbsolutePath=="/md");
    }

    async Task WaitForCsdnEditor() {
        await Wait(()=>Task.FromResult(csdn.CoreWebView2 != null),12,"CSDN 后台浏览器启动失败。");
        var address = csdn.Source;
        if (address == null || (!IsCsdnEditor(address) && address.Host!="passport.csdn.net"))
            csdn.CoreWebView2.Navigate(CsdnEditorUrl);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 22) {
            address = csdn.Source;
            if (address != null && address.Host=="passport.csdn.net") {
                SetCsdnState("signin");
                throw new InvalidOperationException("CSDN 登录已过期，请在「账号」里重新登录。");
            }
            if (IsCsdnEditor(address) && await CsdnScript("return true;")=="true") return;
            await Task.Delay(300);
        }
        throw new TimeoutException("CSDN 编辑器加载超时，文章未送出。");
    }

    async Task<string> CsdnScript(string script) {
        var task = csdn.ExecuteScriptAsync("(()=>{if(location.origin!=='https://editor.csdn.net'||!['/md','/md/'].includes(location.pathname))return null;" +
            "const title=document.querySelector(" + json.Serialize(CsdnTitle) + "),body=document.querySelector(" + json.Serialize(CsdnBody) + ");" +
            "if(!title||!body)return null;" + script + "})()");
        if (await Task.WhenAny(task,Task.Delay(8000)) != task)
            throw new TimeoutException("CSDN 编辑器未响应。");
        return await task;
    }

    async Task PrepareCsdnDraft(CsdnDocument doc,string folder) {
        await WaitForCsdnEditor();
        const string draftState = @"const text=body.innerText.trim();
            return new URLSearchParams(location.search).has('articleId')?'saved':
             (!title.value.trim() || title.value.trim()==='【无标题】') &&
             (!text || (text.startsWith('@[TOC]') && text.includes('欢迎使用Markdown编辑器') && text.includes('合理的创建标题'))) ? 'empty':'occupied';";
        string initial = await CsdnScript(draftState);
        if (initial == "\"saved\"") {
            var completed = new TaskCompletionSource<bool>();
            EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs> handler = (sender,args) => completed.TrySetResult(args.IsSuccess);
            csdn.CoreWebView2.NavigationCompleted += handler;
            try {
                csdn.CoreWebView2.Navigate(CsdnEditorUrl);
                await Wait(()=>Task.FromResult(completed.Task.Status==TaskStatus.RanToCompletion && completed.Task.Result),15,"新建 CSDN 编辑页超时。");
            } finally { csdn.CoreWebView2.NavigationCompleted -= handler; }
            await Wait(async ()=>await CsdnScript("return true;")=="true",15,"无法打开新文章编辑器。");
            initial = await CsdnScript(draftState);
        }
        if (initial != "\"empty\"") throw new InvalidOperationException("编辑器已有未保存内容。请在 CSDN 保存手头的草稿后再导入。");
        status.Text = "CSDN · 正在导入正文与图片…";
        await CsdnScript("title.value="+json.Serialize(doc.Title)+";title.dispatchEvent(new Event('input',{bubbles:true}));" +
            "body.focus();getSelection().selectAllChildren(body);document.execCommand('insertText',false," + json.Serialize(Regex.Replace(doc.Markdown,"\u0001[0-9]+\u0002","")) + ");getSelection().collapseToEnd();return true;");
        var links = new List<string>();
        foreach (string localFile in doc.Images) {
            string oldText = await ReadCsdnBody();
            await CsdnScript("body.focus();getSelection().selectAllChildren(body);getSelection().collapseToEnd();const button=document.querySelector('button[data-title^=图片]');if(button){button.click();return true;}");
            var core = csdn.CoreWebView2;
            object nodeId = null;
            await Wait(async () => {
                if (csdn.Source == null || csdn.Source.GetLeftPart(UriPartial.Authority)!="https://editor.csdn.net") return false;
                dynamic dom = json.DeserializeObject(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument","{}"));
                dynamic nodes = json.DeserializeObject(await core.CallDevToolsProtocolMethodAsync("DOM.querySelectorAll",
                    json.Serialize(new {nodeId=dom["root"]["nodeId"],selector=CsdnImageInput})));
                object[] ids = nodes["nodeIds"];
                if (ids.Length != 1) return false;
                nodeId = ids[0];
                return true;
            },8,"找不到 CSDN 图片上传窗口。");
            await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",json.Serialize(new {nodeId, files=new[]{localFile}}));
            string link = null;
            int oldCount = CsdnImageLink.Matches(oldText).Count;
            await Wait(async ()=>{
                var matches = CsdnImageLink.Matches(await ReadCsdnBody());
                if (matches.Count != oldCount+1) return false;
                link=matches[matches.Count-1].Groups[1].Value;
                return true;
            },25,"CSDN 未返回图片链接。");
            links.Add(link);
            status.Text = "CSDN · 已上传 " + links.Count + " / " + doc.Images.Count + " 张图";
        }
        string markdown = Regex.Replace(doc.Markdown,"\u0001([0-9]+)\u0002",m => "![配图]("+links[Int32.Parse(m.Groups[1].Value)]+")");
        string mdPath = Path.Combine(folder,"article.md");
        File.WriteAllText(mdPath,markdown,new UTF8Encoding(false));
        var editor = csdn.CoreWebView2;
        dynamic root = json.DeserializeObject(await editor.CallDevToolsProtocolMethodAsync("DOM.getDocument","{}"));
        dynamic input = json.DeserializeObject(await editor.CallDevToolsProtocolMethodAsync("DOM.querySelector",
            json.Serialize(new {nodeId=root["root"]["nodeId"],selector="#import-markdown-file-input"})));
        if ((int)input["nodeId"] == 0) throw new InvalidOperationException("找不到 CSDN Markdown 导入入口。");
        await editor.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",json.Serialize(new {nodeId=input["nodeId"],files=new[]{mdPath}}));
        await Wait(async ()=> (await ReadCsdnBody()).Trim()==markdown.Trim(),10,"CSDN 未完成 Markdown 导入。");
        await CsdnScript("title.value="+json.Serialize(doc.Title)+";title.dispatchEvent(new Event('input',{bubbles:true}));");
        await Wait(()=>CsdnMatches(doc.Title,markdown),8,"保存前的图文核对没有通过。");
        if (await CsdnScript("const save=document.querySelector('button.button-save[data-title=保存草稿]');if(!save||save.disabled)return false;save.click();return true;")!="true")
            throw new InvalidOperationException("保存草稿按钮不可用。");
        await Wait(()=>Task.FromResult(csdn.Source != null && CsdnArticleId.IsMatch(csdn.Source.Query)),18,"CSDN 未返回草稿编号。");
        string draftUrl = csdn.Source.AbsoluteUri;
        var reload = new TaskCompletionSource<bool>();
        EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs> onReload = (sender,args)=>reload.TrySetResult(args.IsSuccess);
        csdn.CoreWebView2.NavigationCompleted += onReload;
        try {
            csdn.CoreWebView2.Navigate(draftUrl);
            await Wait(()=>Task.FromResult(reload.Task.Status==TaskStatus.RanToCompletion && reload.Task.Result),15,"草稿重新打开超时。");
        } finally { csdn.CoreWebView2.NavigationCompleted -= onReload; }
        await Wait(()=>CsdnMatches(doc.Title,markdown),15,"重新打开的草稿与原内容不一致。");
        if (links.Count > 0) await Wait(async ()=>await CsdnScript("const links="+json.Serialize(links)+";return links.every(url=>[...document.images].some(img=>img.complete&&img.naturalWidth>0&&img.src.split('#')[0]===url.split('#')[0]));")=="true",20,"草稿已保存，预览图片未全部加载。");
    }

    async Task<string> ReadCsdnBody() {
        return json.Deserialize<string>(await CsdnScript("return body.textContent;")) ?? "";
    }

    async Task<bool> CsdnMatches(string title,string body) {
        return await CsdnScript("return title.value==="+json.Serialize(title)+";")=="true" &&
            (await ReadCsdnBody()).Replace("\r\n","\n").Trim()==body.Replace("\r\n","\n").Trim();
    }
}








