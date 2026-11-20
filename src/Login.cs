using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow : Form {
    const string DeskUrl = "https://zhigao.invalid/";
    const string CsdnEditorUrl = "https://editor.csdn.net/md/";
    const string WriteUrl = "https://zhuanlan.zhihu.com/write";
    static readonly string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zhigao");
    readonly string historyFile = Path.Combine(dataFolder, "publication-results.txt");
    readonly string statusFile;
    readonly string accountFile = Path.Combine(dataFolder,"zhihu-account.txt");
    string rememberedAccount;
    readonly WebView2 dashboard = new WebView2 { Dock = DockStyle.Fill };
    readonly WebView2 page = new WebView2 { Dock = DockStyle.Fill };
    readonly WebView2 csdn = new WebView2 { Dock = DockStyle.Fill, Visible = false };
    CoreWebView2Environment browserEnvironment;
    string csdnState = "idle";
    readonly string csdnAccountMarker = Path.Combine(dataFolder,"csdn-connected");
    bool csdnOpening, csdnChecking, csdnVisible;
    readonly WebView2 profile = new WebView2 { Visible = false };
    readonly Panel platform = new Panel { Dock = DockStyle.Fill };
    readonly Button check = new Button { Dock = DockStyle.Left, Width = 180, Text = "‹  返回工作台", FlatStyle = FlatStyle.Flat };
    readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label platformCaption = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1200 };
    readonly List<Entry> records = new List<Entry>();
    readonly List<BatchItem> batch = new List<BatchItem>();
    BatchItem activeItem;
    readonly HashSet<BatchItem> runningBatchItems=new HashSet<BatchItem>();
    string batchFile;
    string verificationNotice;
    bool verifying;
    readonly Dictionary<BatchItem,Entry> batchEntries = new Dictionary<BatchItem,Entry>();
    sealed class BatchItem {
        public string platform { get; set; }
        public string account { get; set; }
        public string state { get; set; }
        public string step { get; set; }
        public string url { get; set; }
        public bool verifyCreator { get; set; }
        public bool openDraft { get; set; }
        public bool checking { get; set; }
    }
    Panel titleBar;
    readonly ToolTip windowTooltips = new ToolTip();
    bool inspecting, shellReady, picking, editorReady, busy, drafting;
    string queuedArticlePath;
    long queuedArticleBytes;
    int queuedArticleSelection;
    string message = "正在检查登录状态";
    string previous = "waiting";

    class Entry {
        [System.Web.Script.Serialization.ScriptIgnore]
        public string sourceLine { get; set; }
        public string file { get; set; }
        public string platform { get; set; }
        public string label { get; set; }
        public string detail { get; set; }
        public string time { get; set; }
        public string link { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore]
        public string creatorDraft { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore]
        public string editorDraft { get; set; }
        public bool verifyCreator { get { return CanVerifyCreatorRecord(this); } }
        public bool openDraft { get { return (creatorDraft!=null || editorDraft!=null) && (label=="需处理" || label=="未发出"); } }
        public bool checking { get; set; }
    }

    [STAThread]
    static void Main(string[] args) {
        Application.EnableVisualStyles();
        var app = new LoginWindow(args.Length == 1 ? args[0] : null);
        app.enableStartupRecovery=true;
        if (args.Length == 2 && args[0]=="--resume-csdn") app.resumeFile = args[1];
        Application.Run(app);
    }
    string resumeFile;
    bool enableStartupRecovery;

    LoginWindow(string file) {
        Match draftArgument = Regex.Match(file ?? "",@"^--csdn-draft=(\d+)$");
        bool openCsdnOnStart = file == "--csdn" || draftArgument.Success;
        statusFile = openCsdnOnStart ? null : file;
        Text = "知稿 · 发布工作台";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI",11);

        MinimumSize = new Size(810,550);
        StartPosition = FormStartPosition.CenterScreen;
        var top = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.White };
        check.FlatAppearance.BorderSize = 0;
        top.Controls.Add(platformCaption);
        top.Controls.Add(check);
        WireCreatorToolbar(top);
        platform.Controls.Add(page);
        platform.Controls.Add(csdn);
        platform.Controls.Add(top);
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(4);
        BackColor = Color.FromArgb(247,247,248);
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(profile);
        content.Controls.Add(platform);
        content.Controls.Add(dashboard);
        Controls.Add(content);
        titleBar = BuildTitleBar();
        Controls.Add(titleBar);
        dashboard.BringToFront();
        Resize += delegate { Padding = new Padding(WindowState == FormWindowState.Maximized ? 0 : 4); };
        check.Click += async delegate {
            bool returningFromZhihu = !csdnVisible && creatorVisible==null;
            var returningFromCreator=creatorVisible;
            var address = page.Source;
            ShowPage(false);
            if(returningFromCreator!=null && returningFromCreator.Site.Id=="sohu" &&
                !creatorSignInRequired.Contains("sohu") && sohuDraftAccess!=SohuDraftAccess.Allowed)
                await RefreshCreatorProfile(returningFromCreator.Site,true);
            if (!returningFromZhihu) return;
            if (address != null && (IsZhihuArticleUrl(address) ||
                (address.Scheme=="https" && address.Host=="www.zhihu.com" && address.AbsolutePath.StartsWith("/people/",StringComparison.Ordinal))))
                page.CoreWebView2.Navigate(WriteUrl);
            await RefreshZhihuDrafts(true);
        };
        status.TextChanged += delegate {
            if (activeItem != null && activeItem.state == "running")
                activeItem.step = Regex.Replace(status.Text.Trim(),@"^CSDN · ","");
            Render();
        };
        check.EnabledChanged += delegate { Render(); };
        try {
            if (File.Exists(accountFile)) rememberedAccount = File.ReadAllText(accountFile);
            if (File.Exists(historyFile)) {
                foreach (var row in File.ReadLines(historyFile)) if (!String.IsNullOrWhiteSpace(row)) records.Add(ParseResult(row));
                records.Reverse();
                RestoreVerifications();
            }
        } catch (IOException) { status.Text = "暂时无法读取本地发布记录"; }
        Shown += async delegate {
            FitInitialWindow();
            try {
                bool normalWorkbench=enableStartupRecovery && !openCsdnOnStart && statusFile==null;
                if(normalWorkbench)LoadAccountAnalyticsHistory();
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataFolder,"WebView2"));
                browserEnvironment = env;
                await dashboard.EnsureCoreWebView2Async(env);
                StartDashboard();
                await page.EnsureCoreWebView2Async(env);
                await profile.EnsureCoreWebView2Async(env);
                page.CoreWebView2.Navigate(WriteUrl);
                timer.Tick += async delegate {
                    if (IsDisposed || busy) return;
                    await CheckCsdn();
                    if (inspecting || csdnVisible) return;
                    inspecting = true;
                    try { await Inspect(); }
                    catch (Exception) { if (!IsDisposed && !busy) SetState("waiting","正在检查账号"); }
                    finally { inspecting = false; }
                };
                timer.Start();
                if (openCsdnOnStart || File.Exists(csdnAccountMarker)) await OpenCsdn(openCsdnOnStart);
                if (draftArgument.Success && csdn.CoreWebView2 != null)
                    csdn.CoreWebView2.Navigate(CsdnEditorUrl + "?articleId=" + draftArgument.Groups[1].Value);
                if (normalWorkbench) {
                    await RecoverCsdnJob(resumeFile);
                    await Task.WhenAll(RefreshZhihuDrafts(),VerifySubmittedCreatorRecords(PendingCreatorRecords()),RefreshCreatorProfilesOnStartup());
                    WatchRecentCreatorReceipts();
                    if(!IsDisposed)StartAccountAnalyticsRefresh();
                }
            } catch (Exception ex) {
                SetState("error","启动失败");
                status.Text = ex.Message;
                if (!shellReady) MessageBox.Show(this, ex.Message, "知稿启动失败");
            }
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if ((busy || verifying) && e.CloseReason == CloseReason.UserClosing) {
                e.Cancel = true;
                status.Text = "正在处理稿件，请等待发布结果再关闭。";
            }
        };
        FormClosed += delegate { timer.Dispose(); windowTooltips.Dispose(); };
    }

    void FitInitialWindow() {
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        int width = Math.Min(area.Width, Math.Max((int)(area.Width * .71), Math.Min(area.Width - 32,1180)));
        int height = Math.Min(area.Height, Math.Max((int)(area.Height * .80), Math.Min(area.Height - 32,760)));
        Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2,width,height);
    }

    sealed class TitleBar : Panel {
        protected override void WndProc(ref Message m) {
            base.WndProc(ref m);
            if (m.Msg == 0x84) m.Result = new IntPtr(-1);
        }
    }

    Panel BuildTitleBar() {
        var bar = new TitleBar { Dock = DockStyle.Top, Height = 46, BackColor = BackColor };
        AddWindowButton(bar, 13, "关闭",WindowAction.Close, Color.FromArgb(229,115,112), Close);
        AddWindowButton(bar, 49, "最小化",WindowAction.Minimize, Color.FromArgb(227,192,111), () => WindowState = FormWindowState.Minimized);
        AddWindowButton(bar, 85, "最大化或还原",WindowAction.Resize, Color.FromArgb(121,182,137), ToggleMaximize);
        return bar;
    }

    void ToggleMaximize() {
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        else {
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }
    }

    enum WindowAction { Close, Minimize, Resize }

    sealed class WindowButton : Button {
        readonly WindowAction action;
        readonly Color tint;
        bool hovered;
        bool pressed;

        public WindowButton(WindowAction action, Color tint) {
            this.action = action;
            this.tint = tint;
            FlatStyle = FlatStyle.Flat;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint,true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
        }
        protected override void OnMouseCaptureChanged(EventArgs e) {
            base.OnMouseCaptureChanged(e);
            if (!Capture) { pressed = false; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e) {
            // Painting the complete hit target keeps WinForms hover and focus rectangles out.
            var graphics = e.Graphics;
            graphics.Clear(Parent.BackColor);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float scale = Math.Min(Width,Height) / 24f;
            graphics.TranslateTransform(Width / 2f, Height / 2f);
            graphics.ScaleTransform(scale,scale);
            var fill = hovered && pressed ? ControlPaint.Dark(tint,.16f) : tint;
            using (var brush = new SolidBrush(fill)) graphics.FillEllipse(brush,-6,-6,12,12);
            using (var outline = new Pen(Color.FromArgb(26,0,0,0),.7f)) graphics.DrawEllipse(outline,-5.65f,-5.65f,11.3f,11.3f);
            if (Focused && ShowFocusCues) {
                using (var ring = new Pen(Color.FromArgb(57,88,156),1.2f)) graphics.DrawEllipse(ring,-9,-9,18,18);
            }

            using (var mark = new Pen(Color.FromArgb(hovered || Focused ? 200 : 145,35,32,35),1.15f)) {
                mark.StartCap = mark.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                if (action == WindowAction.Close) {
                    graphics.DrawLine(mark,-2,-2,2,2);
                    graphics.DrawLine(mark,-2,2,2,-2);
                } else if (action == WindowAction.Minimize) {
                    graphics.DrawLine(mark,-2.4f,0,2.4f,0);
                } else {
                    graphics.DrawLine(mark,-2.4f,2.4f,2.4f,-2.4f);
                    graphics.DrawLines(mark,new[] { new PointF(.2f,-2.4f),new PointF(2.4f,-2.4f),new PointF(2.4f,-.2f) });
                    graphics.DrawLines(mark,new[] { new PointF(-.2f,2.4f),new PointF(-2.4f,2.4f),new PointF(-2.4f,.2f) });
                }
            }
        }
    }

    void AddWindowButton(Panel bar,int x,string name,WindowAction kind,Color color,Action action) {
        var button = new WindowButton(kind,color) { Name = name, AccessibleName = name,
            Location = new Point(x,6), Size = new Size(34,34), BackColor = bar.BackColor };
        button.Click += delegate { action(); };
        windowTooltips.SetToolTip(button,name);
        bar.Controls.Add(button);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        int borderNone = unchecked((int)0xfffffffe);
        int roundCorners = 2;
        DwmSetWindowAttribute(Handle,34,ref borderNone,sizeof(int));
        DwmSetWindowAttribute(Handle,33,ref roundCorners,sizeof(int));
    }

    protected override CreateParams CreateParams {
        get { var cp = base.CreateParams; cp.Style |= 0x00040000 | 0x00010000 | 0x00020000; return cp; }
    }

    protected override void WndProc(ref Message m) {
        // The compositor supplies the shadow. Skip legacy nonclient repaint on focus.
        if (m.Msg == 0x86) {
            m.LParam = new IntPtr(-1);
            base.WndProc(ref m);
            m.Result = new IntPtr(1);
            return;
        }
        if (m.Msg == 0x83 || m.Msg == 0x85) { m.Result = IntPtr.Zero; return; }
        if (m.Msg == 0xA3 && m.WParam.ToInt32() == 2) { ToggleMaximize(); return; }
        const int hitTest = 0x84;
        base.WndProc(ref m);
        if (m.Msg != hitTest) return;
        long position = m.LParam.ToInt64();
        Point p = PointToClient(new Point((short)(position & 0xffff),(short)((position >> 16) & 0xffff)));
        if (titleBar != null && p.Y >= Padding.Top && p.Y < Padding.Top + titleBar.Height) { m.Result = new IntPtr(2); return; }
        if (WindowState == FormWindowState.Maximized) return;
        bool left = p.X < Padding.Left;
        bool right = p.X >= ClientSize.Width - Padding.Right;
        bool top = p.Y < Padding.Top;
        bool bottom = p.Y >= ClientSize.Height - Padding.Bottom;
        if (top) m.Result = new IntPtr(left ? 13 : right ? 14 : 12);
        else if (bottom) m.Result = new IntPtr(left ? 16 : right ? 17 : 15);
        else if (left || right) m.Result = new IntPtr(left ? 10 : 11);
    }

    void StartDashboard() {
        var core = dashboard.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.NavigationStarting += (s,e) => { if (e.Uri != DeskUrl) e.Cancel = true; };
        core.NewWindowRequested += (s,e) => { e.Handled = true; };
        core.AddWebResourceRequestedFilter(DeskUrl,CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (s,e) => {
            if (e.Request.Uri != DeskUrl) return;
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("dashboard.html");
            e.Response = core.Environment.CreateWebResourceResponse(stream,200,"OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'");
        };
        core.WebMessageReceived += FromDashboard;
        core.NavigationCompleted += (s,e) => {
            shellReady = e.IsSuccess;
            if (shellReady) Render();
        };
        core.Navigate(DeskUrl);
    }

    async void FromDashboard(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
        if (e.Source != DeskUrl || IsDisposed) return;
        string action = e.TryGetWebMessageAsString();
        try {
            if (action.StartsWith("verify-",StringComparison.Ordinal)) {
                if (!busy && !picking && !verifying) await VerifyDashboardRecord(action);
                return;
            }
            if (action=="history:shown") { await RefreshZhihuDrafts(); return; }
            if (action=="access:sohu") {
                if(!busy && !verifying && !picking && !creatorSignInRequired.Contains("sohu"))
                    await RefreshCreatorProfile(Array.Find(CreatorSites,s=>s.Id=="sohu"),true);
                return;
            }
            if (action=="stats:zhihu" || action=="stats:zhihu-refresh") {
                await RefreshZhihuMetrics(action=="stats:zhihu-refresh");
                return;
            }
            if (action=="stats:csdn" || action=="stats:csdn-refresh") {
                await RefreshCsdnMetrics(action=="stats:csdn-refresh");
                return;
            }
            if (action.StartsWith("stats:",StringComparison.Ordinal)) {
                const string suffix="-refresh";
                string id=action.Substring("stats:".Length);
                bool refresh=id.EndsWith(suffix,StringComparison.Ordinal);
                if(refresh)id=id.Substring(0,id.Length-suffix.Length);
                var site=Array.Find(CreatorSites,s=>s.Id==id);
                if(site!=null)await RefreshCreatorMetrics(site,refresh);
                return;
            }
            if (verifying) return;
            if (action == "account:csdn" && !busy) { await OpenCsdn(); return; }
            if (action == "account:zhihu" && !busy) { ShowPage(true); return; }
            if (action.StartsWith("account:",StringComparison.Ordinal) && !busy && !picking) {
                var site=Array.Find(CreatorSites,s=>s.Id==action.Substring("account:".Length));
                if(site!=null)await OpenCreator(site);
                return;
            }
            if (action.StartsWith("open-batch:",StringComparison.Ordinal)) {
                var item = batch.Find(b=>b.platform==action.Substring("open-batch:".Length));
                if (item != null && item.state=="published" && item.url != null)
                    Process.Start(new ProcessStartInfo(item.url) { UseShellExecute = true });
                else if(item!=null && item.openDraft && !busy && !picking) {
                    Entry entry;
                    if(batchEntries.TryGetValue(item,out entry))await OpenSavedCreatorDraft(entry);
                }
                return;
            }
            if (action.StartsWith("open:",StringComparison.Ordinal)) {
                int index;
                if (Int32.TryParse(action.Substring(5),out index) && index >= 0 && index < records.Count) {
                    var entry=records[index];
                    if (entry.label=="草稿" && entry.platform=="知乎" && !busy && !picking && IsZhihuEditUrl(entry.link)) {
                        ShowPage(true);
                        page.CoreWebView2.Navigate(entry.link);
                    } else if ((entry.label=="草稿" || entry.openDraft) && !busy && !picking) {
                        await OpenSavedCreatorDraft(entry);
                    } else if (entry.label=="已发布" && entry.link!=null)
                        Process.Start(new ProcessStartInfo(entry.link) { UseShellExecute = true });
                }
                return;
            }
            if (busy || picking) return;
            if (action=="draft:zhihu") {
                if(!editorReady || queuedArticlePath==null || !File.Exists(queuedArticlePath)) {
                    status.Text="请先登录知乎并选择 Word 文档。";
                    return;
                }
                await SaveZhihuDraftFromDesk(queuedArticlePath);
                return;
            }
            if (action == "remove-file") {
                queuedArticlePath = null;
                ClearBatch();
                status.Text = "";
                Render();
                return;
            }
            if (action.StartsWith("publish:",StringComparison.Ordinal)) {
                var request=action.Substring("publish:".Length).Split('|');
                string declaration=request.Length==2?request[1]:null;
                if(request.Length>2 || (declaration!=null && !IsCreatorDeclaration(declaration)))return;
                PublishTargets targets = ResolveTargets(request[0]);
                if (targets == null) { status.Text = "所选账号状态已变化，请重新确认。"; return; }
                if (queuedArticlePath == null || !File.Exists(queuedArticlePath)) {
                    queuedArticlePath = null;
                    status.Text = "找不到选定的 Word 文档，请重新选择。";
                    Render();
                    return;
                }
                await PublishSelected(queuedArticlePath,targets.ToZhihu,targets.ToCsdn,targets.Creators,declaration);
                return;
            }
            string path = null;
            if (action == "choose-file") {
                picking = true;
                try {
                    using (var picker = new OpenFileDialog { Filter = "Word 文档 (*.docx)|*.docx", Title = "选择待发布的 Word 文档" })
                        if (picker.ShowDialog(this) == DialogResult.OK) path = picker.FileName;
                } finally { picking = false; }
            } else if (action == "drop-file" && e.AdditionalObjects != null && e.AdditionalObjects.Count == 1) {
                var file = e.AdditionalObjects[0] as CoreWebView2File;
                if (file != null) path = file.Path;
            }
            if (path == null) return;
            if (!String.Equals(Path.GetExtension(path),".docx",StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) {
                status.Text = "请选择一个有效的 .docx 文件。";
                return;
            }
            queuedArticleBytes = new FileInfo(path).Length;
            queuedArticlePath = path;
            queuedArticleSelection++;
            ClearBatch();
            status.Text = "";
            Render();
        } catch (Exception ex) { status.Text = ex.Message; }
    }

    sealed class PublishTargets {
        public bool ToZhihu, ToCsdn;
        public readonly List<CreatorSite> Creators=new List<CreatorSite>();
    }
    async Task OpenSavedCreatorDraft(Entry entry) {
        var site=Array.Find(CreatorSites,s=>s.Label==entry.platform);
        var draft=site==null?null:CreatorRecordDraft(site,entry.creatorDraft??entry.editorDraft);
        if(draft!=null)await OpenCreator(site,draft.AbsoluteUri);
    }
    PublishTargets ResolveTargets(string keys) {
        var selected = keys.Split(',');
        if (selected.Length > CreatorSites.Length+2 || selected.Distinct().Count()!=selected.Length) return null;
        var targets = new PublishTargets();
        foreach (var id in selected) {
            if (id == "zhihu" && !targets.ToZhihu && editorReady) targets.ToZhihu = true;
            else if (id == "csdn" && !targets.ToCsdn && csdnState == "editor") targets.ToCsdn = true;
            else {
                var site=Array.Find(CreatorSites,s=>s.Id==id);
                if(site==null || !CreatorCanPublish(site))return null;
                targets.Creators.Add(site);
            }
        }
        return targets;
    }

    async Task SaveZhihuDraftFromDesk(string file,string existingUrl=null) {
        busy=true;
        drafting=true;
        ClearBatch();
        check.Enabled=false;
        status.Text="正在保存知乎草稿";
        Render();
        try {
            string url=await PrepareZhihuWord(file,true,existingUrl);
            Record(Path.GetFileName(file)+"    草稿 · "+url);
            status.Text="知乎草稿已保存，标题和图文已核对。"+
                (verifiedZhihuTopics.Length>0?"已选话题："+String.Join("、",verifiedZhihuTopics):"");
        } catch (Exception ex) {
            status.Text="知乎草稿未通过核对 · "+ex.Message;
            Record(Path.GetFileName(file)+"    草稿待核实 · "+ex.Message);
        } finally {
            drafting=false;
            busy=false;
            check.Enabled=true;
            prepared=false;
            Render();
        }
    }
    async Task PublishZhihu(string file) {
        if (!editorReady) throw new InvalidOperationException("知乎编辑器未就绪。");
        issued = false;
        receipt = null;
        string result;
        try {
            await PrepareZhihuWord(file,false);
            await Submit();
            result = "已发布 · " + receipt;
        }
        catch (PriorAttempt ex) { result = ex.Message; }
        catch (Exception ex) {
            result = (issued ? "待核实 · " : "未发出 · ") + ex.Message;
        }
        try {
            status.Text = result;
            Record(Path.GetFileName(file) + "    " + result);
            if (issued && receipt != null) page.CoreWebView2.Navigate(WriteUrl);
        }
        catch (IOException) { status.Text = result + "（本机记录写入失败）"; }

    }
    async Task PublishSelected(string file,bool toZhihu,bool toCsdn,IEnumerable<CreatorSite> creators=null,string declaration=null) {
        busy = true;
        ClearBatch();
        batchFile = Path.GetFileName(file);
        if (toZhihu) batch.Add(new BatchItem { platform="zhihu",account=String.IsNullOrWhiteSpace(nickname)?rememberedAccount:nickname,state="queued",step="等待开始" });
        if (toCsdn) batch.Add(new BatchItem { platform="csdn",account=CsdnAccountName,state="queued",step="等待开始" });
        if(creators!=null)
            foreach(var site in creators)
                batch.Add(new BatchItem {platform=site.Id,account=CreatorProfileName(site.Id),state="queued",step="等待开始"});
        status.Text = "";
        check.Enabled = false;
        Render();
        try {
            await RunPublishBatch(file,async item=>{
                    if (item.platform=="csdn") await RunCsdnJob(file);
                    else if(item.platform=="zhihu") await PublishZhihu(file);
                    else await PublishCreator(Array.Find(CreatorSites,s=>s.Id==item.platform),file,declaration);
            });
        } finally {
            activeItem = null;
            busy = false;
            check.Enabled = true;
            Render();
        }
        await VerifySubmittedCreatorRecords(batchEntries.Values.Where(entry=>entry.verifyCreator &&
            (entry.label=="已提交" || entry.label=="待核实")).ToArray());
        WatchRecentCreatorReceipts();
    }

    async Task RunPublishBatch(string file,Func<BatchItem,Task> publish) {
        using(var slots=new SemaphoreSlim(3))
        using(var legacySlot=new SemaphoreSlim(1)) {
            await Task.WhenAll(batch.ToArray().Select(async item=>{
                bool legacy=item.platform=="zhihu" || item.platform=="csdn";
                if(legacy)await legacySlot.WaitAsync();
                await slots.WaitAsync();
                try {
                    if(legacy)activeItem=item;
                    runningBatchItems.Add(item);
                    item.state="running";
                    item.step="正在检查稿件和账号";
                    Render();
                    await publish(item);
                } catch(Exception ex) {
                    Record(Path.GetFileName(file)+"    "+(item.platform=="zhihu"?"":BatchPlatformName(item.platform)+" · ")+
                        "待核实 · 流程中断，尚无平台回执。"+ex.Message.Replace('\r',' ').Replace('\n',' '));
                } finally {
                    runningBatchItems.Remove(item);
                    if(legacy) {activeItem=null;legacySlot.Release();}
                    slots.Release();
                }
            }));
        }
    }

    void ReportCreatorProgress(CreatorSite site,string text) {
        var item=runningBatchItems.FirstOrDefault(row=>row.platform==site.Id);
        if(item==null)status.Text=site.Label+" · "+text;
        else if(item.state=="running")item.step=text;
        Render();
    }
    void ClearBatch() {
        batchFile=null;
        verificationNotice=null;
        batch.Clear();
        batchEntries.Clear();
    }

    void Render() {
        platformCaption.Text = csdnVisible ? "CSDN · " + (csdnState=="editor"?(CsdnAccountName ?? "写作账号已登录"):csdnState=="signin"?"请登录":"正在检查账号")
            : "知乎 · " + message;
        RenderCreatorToolbar();
        if (!shellReady || IsDisposed) return;
        var accounts=new List<WorkbenchAccount> {
            new WorkbenchAccount { id="zhihu",platform="知乎",mark="知",publishable=true,
                name=editorReady && nickname.Length>0?nickname:rememberedAccount, connected=rememberedAccount!=null || editorReady,state=previous },
            new WorkbenchAccount { id="csdn",platform="CSDN",mark="C",publishable=true,
                name=CsdnAccountName,connected=csdnState=="editor" || File.Exists(csdnAccountMarker),state=csdnState }
        };
        accounts.AddRange(CreatorAccounts());
        dashboard.CoreWebView2.PostWebMessageAsJson(json.Serialize(new {
            ready = accounts.Any(a=>a.publishable && (a.canSubmit??a.state=="editor")) && !busy && !verifying, busy, drafting, verifying, verificationNotice, account = nickname, accountMessage = message,
            accountState = previous, status = status.Text.Trim(), history = records, batch, batchFile, zhihuMetrics, csdnMetrics, creatorMetrics,
            analyticsCollectedAt, analyticsDays, analyticsWriteFailed=analyticsWriteFailed.ToArray(),
            article = queuedArticlePath == null ? null : new { name = Path.GetFileName(queuedArticlePath), title = Path.GetFileNameWithoutExtension(queuedArticlePath), bytes = queuedArticleBytes, selection=queuedArticleSelection },
            accounts
        }));
    }
    void ShowPage(bool show) {
        HideCreator();
        csdnVisible = false;
        csdn.Visible = false;
        page.Visible = true;
        dashboard.Visible = !show;
        if (show) platform.BringToFront(); else dashboard.BringToFront();
        Render();
    }

    async Task OpenCsdn(bool show = true) {
        if (browserEnvironment == null) return;
        csdn.Visible = true;
        if (show) {
            HideCreator();
            csdnVisible = true;
            dashboard.Visible = false;
            page.Visible = false;
            platform.BringToFront();
            csdn.BringToFront();
            Render();
        }
        if (csdn.CoreWebView2 != null) {
            csdn.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = csdnVisible;
            return;
        }
        if (csdnOpening) return;
        csdnOpening = true;
        SetCsdnState("waiting");
        try {
            var options = browserEnvironment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "Zhigao-CSDN";
            await csdn.EnsureCoreWebView2Async(browserEnvironment,options);
            csdn.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = csdnVisible;
            csdn.CoreWebView2.NewWindowRequested += (sender,args) => {
                if (busy && !csdnVisible) args.Handled = true;
                if (IsCsdnAddress(args.Uri)) { csdn.CoreWebView2.Navigate(args.Uri); args.Handled = true; }
            };
            csdn.CoreWebView2.NavigationCompleted += (sender,args) => {
                if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    SetCsdnState("error");
            };
            csdn.CoreWebView2.Navigate(CsdnEditorUrl);
        } catch (Exception ex) {
            SetCsdnState("error");
            status.Text = "CSDN 页面启动失败 · " + ex.Message;
        } finally { csdnOpening = false; }
    }

    static bool IsCsdnAddress(string address) {
        Uri result;
        return Uri.TryCreate(address,UriKind.Absolute,out result) && result.Scheme == "https" &&
            (result.Host == "csdn.net" || result.Host.EndsWith(".csdn.net",StringComparison.Ordinal));
    }

    async Task CheckCsdn() {
        if (csdnChecking || csdn.CoreWebView2 == null || csdn.Source == null || !IsCsdnAddress(csdn.Source.AbsoluteUri)) return;
        csdnChecking = true;
        try {
            if (csdn.Source.Host == "passport.csdn.net") { SetCsdnState("signin"); return; }
            if (!IsCsdnEditor(csdn.Source)) { SetCsdnState("waiting"); return; }
            var editorAddress=csdn.Source;
            string result = await csdn.ExecuteScriptAsync(@"(() => {
                if (location.origin !== 'https://editor.csdn.net' || !['/md','/md/'].includes(location.pathname)) return null;
                const visible = node => !!node.getClientRects().length;
                const dialog = Array.from(document.querySelectorAll('[role=dialog],.login-mark')).some(
                    node => visible(node) && /登录|扫码/.test(node.innerText));
                const signin = Array.from(document.querySelectorAll('iframe')).some(
                    node => visible(node) && /^https:\/\/passport\.csdn\.net\//.test(node.src));
                if (dialog || signin) return {state:'signin'};
                const editor = [...document.querySelectorAll('pre.editor__inner[contenteditable=true]')].some(visible)
                    && !!document.querySelector('input.article-bar__title');
                const publish = !!document.querySelector('button.button-save[data-title=保存草稿]');
                if(!editor || !publish) return {state:'waiting'};
                "+CsdnAvatarIdentity+@"
                return {state:'editor',handle:accountHandle};
            })()");
            if (IsDisposed || csdn.Source!=editorAddress) return;
            var info=json.Deserialize<CsdnEditorInfo>(result);
            if (info==null) return;
            if (info.state=="signin") SetCsdnState("signin");
            else if (info.state=="editor") {
                UpdateCsdnHandle(info.handle);
                SetCsdnState("editor");
                await CheckCsdnNickname();
            }
        } catch (InvalidOperationException) { }
        finally { csdnChecking = false; }
    }

    void SetCsdnState(string next) {
        if (csdnState == next || IsDisposed) return;
        csdnState = next;
        if (next=="signin") {
            if (csdnHandle!=null) UpdateCsdnHandle(null);
            else ClearCsdnMetrics();
        }
        if (next == "editor") { try { File.WriteAllText(csdnAccountMarker,""); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        Render();
        if (statusFile != null) File.WriteAllText(statusFile + ".csdn",next);
    }
    string nickname = "";
    DateTime nextNameCheck;
    async System.Threading.Tasks.Task Inspect() {
        Uri uri = page.Source;
        if (uri == null) return;
        if (uri.Host == "www.zhihu.com" && uri.AbsolutePath.StartsWith("/signin")) {
            nickname = "";
            nextNameCheck = DateTime.MinValue;
            SetState("signin", "请在知乎页面登录");
            return;
        }
        if (previous == "signin" && uri.Host == "www.zhihu.com" && uri.AbsolutePath == "/") {
            page.CoreWebView2.Navigate(WriteUrl);
            return;
        }
        if (await Act("editor")) {
            if (DateTime.UtcNow >= nextNameCheck) {
                nextNameCheck = DateTime.UtcNow.AddMinutes(5);
                profile.CoreWebView2.Navigate("https://www.zhihu.com/people/me");
            }
            string raw = await profile.ExecuteScriptAsync(@"(() => {
                if(location.origin!=='https://www.zhihu.com') return '';
                const name = document.querySelector('.ProfileHeader-name')?.textContent?.trim();
                if(name) return name;
                if(!window.__zhigaoLookupStarted){
                    window.__zhigaoLookupStarted=true;
                    fetch('/api/v4/me').then(r=>r.ok?r.json():null).then(v=>{if(v && v.id)window.__zhigaoName=v.name}).catch(()=>{});
                }
                return window.__zhigaoName || '';
            })()");
            if (raw.StartsWith("\"") && raw.Length > 2) nickname = json.Deserialize<string>(raw);
            if (busy) return;
            if (previous == "signin") ShowPage(false);
            if (rememberedAccount == null || (nickname.Length > 0 && nickname != rememberedAccount)) {
                if (!String.IsNullOrEmpty(rememberedAccount)) ClearZhihuMetrics();
                rememberedAccount = nickname;
                try { Directory.CreateDirectory(dataFolder); File.WriteAllText(accountFile,rememberedAccount); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            SetState("editor", String.IsNullOrWhiteSpace(nickname) ? "已登录" : nickname + " · 已登录");
        } else SetState("waiting", "等待写作页面");
    }

    void SetState(string state,string description) {
        if (busy) return;
        bool changed = state != previous || description != message || editorReady != (state == "editor");
        previous = state;
        if (state=="signin" && changed) ClearZhihuMetrics();
        message = description;
        editorReady = state == "editor";
        if (!changed) return;
        Render();
        if (statusFile != null) File.WriteAllText(statusFile,state);
    }
    static Entry ParseResult(string line) {

        var fields = line.Split(new[] {"    "},3,StringSplitOptions.None);
        string text = fields.Length == 3 ? fields[2] : line;
        bool isCsdn = text.StartsWith("CSDN · ",StringComparison.Ordinal);
        var creator=Array.Find(CreatorSites,s=>text.StartsWith(s.Label+" · ",StringComparison.Ordinal));
        string creatorDraft=null,editorDraft=null;
        if(creator!=null) {
            bool navigationOnly=text.Contains("\t定位草稿=");
            string marker=navigationOnly?"\t定位草稿=":"\t草稿记录=";
            int separator=text.LastIndexOf(marker,StringComparison.Ordinal);
            if(separator>=0) {
                string address=text.Substring(separator+marker.Length);
                if(CreatorRecordDraft(creator,address)!=null) {
                    if(navigationOnly)editorDraft=address;else creatorDraft=address;
                }
                text=text.Substring(0,separator);
            }
        }
        string platform=isCsdn?"CSDN":creator==null?"知乎":creator.Label;
        if(isCsdn || creator!=null)text=text.Substring(platform.Length+" · ".Length);
        var parts = text.Split(new[] {" · "},2,StringSplitOptions.None);
        string label = parts.Length == 2 ? parts[0] : "记录";
        string detail = parts.Length == 2 ? parts[1] : text;
        Uri draftAddress;
        string creatorLink=creator!=null && label=="草稿" && Uri.TryCreate(detail,UriKind.Absolute,out draftAddress) &&
            GetCreatorDraftAddress(creator,draftAddress)!=null?detail:null;
        if(creator!=null && label=="已发布" && IsCreatorPublicArticle(creator,detail))creatorLink=detail;
        return new Entry { sourceLine=line, file = fields.Length == 3 ? fields[1] : "历史记录", platform = platform, label = label,
            detail = detail, time = fields.Length == 3 ? fields[0] : "", editorDraft=editorDraft, creatorDraft=creatorDraft??(label=="草稿"?creatorLink:null),
            link = creator!=null?creatorLink:(!isCsdn && label=="草稿" && IsZhihuEditUrl(detail)) ? detail :
                (label == "已发布" || label == "已提交" || label == "审核中") &&
                Regex.IsMatch(detail,isCsdn?@"^https://blog\.csdn\.net/[\w-]+/article/details/\d+/?$":@"^https://zhuanlan\.zhihu\.com/p/\d+/?$") ? detail : null };

    }
    void Record(string text) {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "    " + text;
        // The on-disk file is chronological; the dashboard shows newest first.
        var entry = ParseResult(line);
        records.Insert(0,entry);
        var item=runningBatchItems.FirstOrDefault(row=>entry.platform==BatchPlatformName(row.platform));
        if (item != null) {
            batchEntries[item]=entry;
            ApplyOutcome(item,entry);
        }
        try { Directory.CreateDirectory(dataFolder); File.AppendAllText(historyFile,line + Environment.NewLine); }
        catch (IOException) {
            if(item!=null)item.step+="（本地记录保存失败）";
            else status.Text+="（本地记录保存失败）";
        }
        Render();
    }
    static string BatchPlatformName(string platformId) {
        if(platformId=="zhihu")return "知乎";
        if(platformId=="csdn")return "CSDN";
        var site=Array.Find(CreatorSites,s=>s.Id==platformId);
        return site==null?platformId:site.Label;
    }
    sealed class Verification {
        public string original { get; set; }
        public string outcome { get; set; }
    }
    string VerificationsPath(string platformName) {
        if(platformName=="CSDN")return Path.Combine(dataFolder,"csdn-verifications.jsonl");
        if(platformName=="知乎")return Path.Combine(dataFolder,"zhihu-verifications.jsonl");
        var site=Array.Find(CreatorSites,s=>s.Label==platformName);
        return Path.Combine(dataFolder,site.Id+"-verifications.jsonl");
    }
    void RestoreVerifications() {
        foreach (string platformName in new[]{"CSDN","知乎"}.Concat(CreatorSites.Select(s=>s.Label))) {
            string path=VerificationsPath(platformName);
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path)) {
                try {
                    var v=json.Deserialize<Verification>(line);
                    if (v==null || String.IsNullOrEmpty(v.outcome)) continue;
                    var source=records.Find(e=>e.platform==platformName && e.sourceLine==v.original);
                    if(source==null)continue;
                    foreach(var original in records.FindAll(e=>SameVerificationTarget(e,source)))UpdateVerifiedEntry(original,v.outcome);
                } catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }
    static void UpdateVerifiedEntry(Entry entry,string outcome) {
        string prefix=entry.platform=="知乎"?"":entry.platform+" · ";
        var verified=ParseResult(entry.time+"    "+entry.file+"    "+prefix+outcome);
        entry.label=verified.label;
        entry.detail=verified.detail;
        entry.link=verified.link;
    }
    static bool SameVerificationTarget(Entry row,Entry source) {
        return row.platform==source.platform && (row.sourceLine==source.sourceLine ||
            SameCreatorPublication(row.platform,row.creatorDraft,source.creatorDraft));
    }
    void SaveVerification(Entry entry,string outcome) {
        var v=new Verification { original=entry.sourceLine,outcome=outcome };
        File.AppendAllText(VerificationsPath(entry.platform),json.Serialize(v)+Environment.NewLine);
        foreach(var matching in records.FindAll(r=>SameVerificationTarget(r,entry)))
            UpdateVerifiedEntry(matching,outcome);
        foreach(var pair in batchEntries)
            if(SameVerificationTarget(pair.Value,entry)) ApplyOutcome(pair.Key,pair.Value);
    }
    async Task VerifyDashboardRecord(string action) {
        Entry entry=null;
        if (action.StartsWith("verify-batch:",StringComparison.Ordinal)) {
            var item=batch.Find(b=>b.platform==action.Substring("verify-batch:".Length));
            if (item!=null) batchEntries.TryGetValue(item,out entry);
        } else if (action.StartsWith("verify-history:",StringComparison.Ordinal)) {
            int i;
            if (Int32.TryParse(action.Substring("verify-history:".Length),out i) && i>=0 && i<records.Count) entry=records[i];
        }
        if (entry==null) return;
        if (entry.platform=="知乎") { await VerifyZhihuRecord(entry); return; }
        if (entry.verifyCreator) { await VerifyCreatorRecord(entry); return; }
        if (entry.platform!="CSDN" || !Regex.IsMatch(entry.label,"^(待核实|已提交|审核中|草稿)$")) return;
        verifying=true;
        verificationNotice="正在 CSDN 创作中心核实《"+Path.GetFileNameWithoutExtension(entry.file)+"》";
        Render();
        try {
            DateTime when;
            bool hasTime=DateTime.TryParseExact(entry.time,"yyyy-MM-dd HH:mm",System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,out when);
            string id=Regex.Match(entry.detail,@"草稿编号 (\d+)").Groups[1].Value;
            var listing=await FindCsdnArticle(Path.GetFileNameWithoutExtension(entry.file),id,hasTime?(DateTime?)when:null);
            var outcome=CsdnListingOutcome(listing);
            if (outcome==null && listing.State=="draft" && hasTime && DateTime.Now-when>TimeSpan.FromMinutes(2)
                && entry.label!="审核中" && entry.label!="已提交")
                outcome="草稿 · 草稿编号 "+listing.Id+"，CSDN 草稿箱已确认，尚未公开。";
            if (outcome==null) {
                verificationNotice="CSDN 暂无可确认的新回执，已保留上次状态。";
                return;
            }
            if (outcome != entry.label+" · "+entry.detail) SaveVerification(entry,outcome);
            verificationNotice="CSDN 核实结果 · "+entry.label;
        } catch (Exception) { verificationNotice="核实过程未完成，可稍后再查。"; }
        finally { verifying=false; Render(); }
    }
    static void ApplyOutcome(BatchItem item,Entry entry) {
        switch (entry.label) {
            case "已发布": item.state=entry.link==null?"uncertain":"published"; break;
            case "审核中": item.state="review"; break;
            case "已提交": item.state="submitted"; break;
            case "需处理": item.state="blocked"; break;
            case "草稿": item.state="draft"; break;
            case "未发出": item.state="failed"; break;
            default: item.state="uncertain"; break;
        }
        item.url=item.state=="published"?entry.link:null;
        item.step=item.url==null?entry.detail:"已取得文章链接";
        item.verifyCreator=entry.verifyCreator;
        item.openDraft=entry.openDraft;
    }
}





















