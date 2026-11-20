using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;

partial class LoginWindow {

    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8388608 };
    readonly string inspector = LoadInspector();
    bool prepared;
    string title, body, attempt;
    int imageCount;
    readonly Dictionary<int,string> numberedParagraphs = new Dictionary<int,string>();
    sealed class ZhihuTable {
        public int start { get; set; }
        public string[][] rows { get; set; }
        public bool merged { get; set; }
    }
    readonly List<ZhihuTable> wordTables = new List<ZhihuTable>();

    static string LoadInspector() {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("inspect.js"))
        using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
    }
    Task<string> InspectAction(string action, Microsoft.Web.WebView2.WinForms.WebView2 view = null) {
        return (view ?? page).ExecuteScriptAsync("(" + inspector + ")(" + json.Serialize(action) + "," +
            json.Serialize(new { titleSelector = "textarea[placeholder*=标题],input[placeholder*=标题]",
                bodySelector = ".public-DraftEditor-content[contenteditable=true]", title = title }) + ")");
    }
    async Task<bool> Act(string action) { return await InspectAction(action) == "true"; }
    async Task<bool> ClickEditorControl(string action) {
        string result = await InspectAction(action);
        if (!result.StartsWith("{")) return false;
        var point = json.Deserialize<Dictionary<string,object>>(result);
        if (point == null) return false;
        var core = page.CoreWebView2;
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",json.Serialize(new { type="mouseMoved", x=point["x"], y=point["y"] }));
        foreach (var type in new[] {"mousePressed","mouseReleased"})
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",json.Serialize(new { type, button="left", clickCount=1, x=point["x"], y=point["y"] }));
        return true;
    }
    async Task Wait(Func<Task<bool>> probe, int seconds, string error) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) {
            if (await probe()) return;
            await Task.Delay(300);
        }
        throw new TimeoutException(error);
    }
    static string Compact(string text) { return Regex.Replace(text, @"[\s\u200b\ufeff]+", ""); }
    async Task<Dictionary<string,object>> Contents(Microsoft.Web.WebView2.WinForms.WebView2 view = null) {
        var value = json.Deserialize<Dictionary<string,object>>(await InspectAction("contents",view));
        if (value == null) return null;
        string text = Compact((string)value["text"]);
        var tables = json.ConvertToType<List<ZhihuTable>>(value["tables"]);
        var insertions = new List<KeyValuePair<int,int>>();
        int prefixLength = 0;
        foreach (object entry in (System.Collections.IEnumerable)value["listStarts"]) {
            int position = Convert.ToInt32(entry) + prefixLength;
            string prefix;
            if (position<=text.Length && numberedParagraphs.TryGetValue(position,out prefix) && !text.Substring(position).StartsWith(prefix,StringComparison.Ordinal)) {
                text = text.Insert(position,prefix);
                insertions.Add(new KeyValuePair<int,int>(Convert.ToInt32(entry),prefix.Length));
                prefixLength += prefix.Length;
            }
        }
        value["text"] = text;
        foreach (var table in tables)
            table.start += insertions.Where(n => n.Key < table.start).Sum(n => n.Value);
        value["tableError"] = CompareTables(tables);
        return value;
    }
    string CompareTables(List<ZhihuTable> tables) {
        if (tables.Count != wordTables.Count)
            return String.Format("Word 有 {0} 个表格，知乎编辑器可核对 {1} 个",wordTables.Count,tables.Count);
        for (int i = 0; i < wordTables.Count; i++) {
            var original = wordTables[i];
            var actual = tables[i];
            string name = String.Format("第 {0} 个表格",i + 1);
            if (original.start != actual.start) return name + "在正文中的位置与 Word 不一致";
            if (actual.merged || original.rows.Length != actual.rows.Length) return name + "的结构与 Word 不一致";
            for (int row = 0; row < original.rows.Length; row++) {
                if (original.rows[row].Length != actual.rows[row].Length)
                    return name + String.Format("第 {0} 行列数与 Word 不一致",row + 1);
                for (int col = 0; col < original.rows[row].Length; col++)
                    if (original.rows[row][col] != Compact(actual.rows[row][col]))
                        return name + String.Format("第 {0} 行第 {1} 列与 Word 不一致",row + 1,col + 1);
            }
        }
        return "";
    }
    bool Matches(Dictionary<string,object> value, bool withTitle) {
        return value != null && (!withTitle || (string)value["title"] == title) &&
            (string)value["tableError"] == "" && Compact((string)value["text"]) == body &&
            (int)value["imageCount"] == imageCount && (bool)value["imagesLoaded"];
    }
    async Task<bool> Matches(bool withTitle) { return Matches(await Contents(), withTitle); }
    string Difference(Dictionary<string,object> value) {
        if (value == null) return "知乎编辑器已关闭或登录已失效。";
        string actual = Compact((string)value["text"]);
        var details = new List<string>();
        if ((string)value["tableError"] != "") details.Add((string)value["tableError"]);
        if (actual != body) {
            int index = 0;
            while (index < actual.Length && index < body.Length && actual[index] == body[index]) index++;
            details.Add(String.Format("正文 {0}/{1} 字，第 {2} 字起有差异",actual.Length,body.Length,index+1));
        }
        if ((int)value["imageCount"] != imageCount || !(bool)value["imagesLoaded"])
            details.Add(String.Format("已就绪图片 {0}/{1} 张，编辑器内共 {2} 张",value["readyImages"],imageCount,value["imageCount"]));
        return String.Join("；",details) + "。";
    }
    class PriorAttempt : Exception { public PriorAttempt(string message) : base(message) {} }
    void ReadZhihuWord(XDocument doc) {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var bodyText = new System.Text.StringBuilder();
        XElement previousParagraph = null;
        numberedParagraphs.Clear();
        wordTables.Clear();
        foreach (var node in doc.Descendants().Where(n => n.Name == w+"t" || n.Name == w+"tbl")) {
            if (node.Name == w+"tbl") {
                if (node.Descendants(w+"tbl").Any() || node.Descendants(w+"gridSpan").Any(e => (string)e.Attribute(w+"val") != "1") ||
                    node.Descendants(w+"hMerge").Any() || node.Descendants(w+"vMerge").Any() ||
                    node.Descendants(w+"gridBefore").Concat(node.Descendants(w+"gridAfter")).Any(e => (string)e.Attribute(w+"val") != "0"))
                    throw new InvalidDataException("知乎暂不能逐格核对含合并、嵌套或省略单元格的 Word 表格。");
                var rows = node.Elements(w+"tr").Select(row => row.Elements(w+"tc")
                    .Select(cell => Compact(String.Concat(cell.Descendants(w+"t").Select(e => e.Value)))).ToArray()).ToArray();
                if (rows.Length == 0 || rows.Any(row => row.Length == 0))
                    throw new InvalidDataException("知乎暂不能核对这份 Word 的表格结构。");
                wordTables.Add(new ZhihuTable { start=bodyText.Length, rows=rows });
                continue;
            }
            var paragraph = node.Ancestors(w+"p").FirstOrDefault();
            if (paragraph != null && paragraph != previousParagraph) {
                var paragraphText = String.Concat(paragraph.Descendants(w+"t").Select(e => e.Value));
                var marker = Regex.Match(paragraphText,@"^\s*(\d{1,4}\.)\s+");
                if (marker.Success) numberedParagraphs[bodyText.Length] = marker.Groups[1].Value;
            }
            previousParagraph = paragraph;
            bodyText.Append(Compact(node.Value));
        }
        body = bodyText.ToString();
        imageCount = doc.Descendants(a + "blip").Count() + doc.Descendants(XName.Get("imagedata","urn:schemas-microsoft-com:vml")).Count();
    }
    async Task<bool> Attach(string file) {
        if (page.Source.GetLeftPart(UriPartial.Authority) != "https://zhuanlan.zhihu.com")
            throw new InvalidOperationException("请先进入知乎文章编辑器。");
        var core = page.CoreWebView2;
        dynamic document = json.DeserializeObject(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{}"));
        string query = json.Serialize(new { nodeId = document["root"]["nodeId"],
            selector = ":is(.Modal,[role=dialog]) input[type=file][accept*='.docx' i][accept*='.md' i]:not([accept*='.pdf' i])" });
        dynamic nodes = json.DeserializeObject(await core.CallDevToolsProtocolMethodAsync("DOM.querySelectorAll", query));
        object[] ids = nodes["nodeIds"];
        if (ids.Length != 1) return false;
        await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", json.Serialize(new { nodeId = ids[0], files = new[] { file } }));
        return true;
    }
    void LoadZhihuSource(string file) {
        prepared = false;
        if (new FileInfo(file).Length > 30L * 1024 * 1024)
            throw new InvalidDataException("知乎文档导入的大小上限为 30 MB，请缩小图片后再选择文件。");
        using (var stream = File.OpenRead(file))
        using (var zip = new ZipArchive(stream)) {
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) throw new InvalidDataException("无法读取 Word 文档正文。");
            using (var xml = entry.Open()) ReadZhihuWord(XDocument.Load(xml));
        }
        title = Path.GetFileNameWithoutExtension(file);
        if (title.Length > 100 || body.Length + imageCount == 0) throw new InvalidDataException("请使用 100 字以内的文件名，并在文档中加入正文或图片。");
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zhigao", "Attempts");
        Directory.CreateDirectory(folder);
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(file)) attempt = Path.Combine(folder, BitConverter.ToString(hash.ComputeHash(stream)) + ".txt");
    }
    void CheckZhihuAttempt() {
        if (File.Exists(attempt)) {
            string link = File.ReadAllText(attempt);
            throw new PriorAttempt(link.Length == 0 ? "待核实 · 这份稿件已有提交记录，请在知乎创作中心核实。" : "已发布 · " + link);
        }
    }
    async Task ImportDocument(string file,bool onlyDraft = false) {
        LoadZhihuSource(file);
        if (!onlyDraft) CheckZhihuAttempt();
        var initial = await Contents();
        if (initial == null) throw new InvalidOperationException("知乎编辑器未就绪，请先检查登录状态。");
        if (Compact((string)initial["title"]).Length > 0 || (bool)initial["hasContent"]) {
            status.Text = "正在打开新草稿…";
            page.CoreWebView2.Navigate(WriteUrl);
            await Wait(async () => {
                var blank = await Contents();
                return blank != null && Compact((string)blank["title"]).Length == 0 && !(bool)blank["hasContent"];
            }, 20, "新草稿未就绪，原草稿已保留。请打开知乎页面检查。");
        }
        status.Text = "正在导入 " + Path.GetFileName(file) + "…";
        await Wait(() => ClickEditorControl("import-point"), 8, "未找到知乎写作工具栏中的「导入」。");
        await Wait(() => ClickEditorControl("doc-point"), 8, "知乎未打开「导入文档」菜单。");
        await Wait(() => Attach(file), 10, "知乎导入窗口未提供 Word 上传入口。");
        Dictionary<string,object> last = null;
        try {
            await Wait(async () => {
                last = await Contents();
                if (Matches(last,false)) return true;
                if (last != null) {
                    string tableError = (string)last["tableError"];
                    status.Text = tableError.Length > 0 ? "核对表格中 · " + tableError :
                        String.Format("核对图文中 · 正文 {0}/{1} 字 · 图片 {2}/{3} 张",
                        Compact((string)last["text"]).Length, body.Length,last["readyImages"],imageCount);
                }
                return false;
            },90,"导入超时");
        } catch (TimeoutException) { throw new InvalidOperationException(Difference(last) + "草稿已保留。"); }
        await SetVerifiedTitle();
    }
    async Task SetVerifiedTitle() {
        if (!await Act("title") || !await Matches(true)) throw new InvalidOperationException("写入标题时草稿发生变化，请检查草稿。");
        prepared = true;
        status.Text = "标题和图文已核对";
    }
    string PublicUrl() {
        var uri = page.Source;
        if (uri == null || uri.GetLeftPart(UriPartial.Authority) != "https://zhuanlan.zhihu.com" || !Regex.IsMatch(uri.AbsolutePath, @"^/p/\d+/?$")) return null;
        if (!String.IsNullOrEmpty(expectedPublicPath) && uri.AbsolutePath.TrimEnd('/') != expectedPublicPath) return null;
        return uri.GetLeftPart(UriPartial.Path);
    }
    bool issued;
    string receipt, expectedPublicPath;
    async Task Submit() {
        if (!prepared || !await Matches(true)) throw new InvalidOperationException("草稿已变化，请先核对当前内容。");
        await Wait(() => Act("publish-ready"), 6, "知乎的发布按钮暂不可用，请查看页面提示。");
        string draftPath = page.Source.AbsolutePath.TrimEnd('/');
        expectedPublicPath = Regex.IsMatch(draftPath,@"^/p/\d+/edit$") ? draftPath.Substring(0,draftPath.Length-5) : null;
        using (File.Open(attempt, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {}
        issued = true;
        prepared = false;
        status.Text = "  正在等待知乎发布结果…";
        if (!await ClickEditorControl("publish-point")) throw new InvalidOperationException("未确认提交，请在知乎页面检查。");
        await Wait(async () => PublicUrl() != null || await Act("dialog"), 12, "尚未确认发布结果，请查看知乎页面和创作中心。");
        if (PublicUrl() == null && !await ClickEditorControl("confirm-point")) throw new InvalidOperationException("知乎有待确认的发布设置，请在当前窗口完成。");
        await Wait(() => Task.FromResult(PublicUrl() != null), 20, "尚未取得文章链接，请到创作中心核对发布状态。");
        receipt = PublicUrl();
        File.WriteAllText(attempt, receipt);
    }
}



