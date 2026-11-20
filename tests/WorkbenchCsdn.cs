using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Uses the complete production form, its drag/drop bridge and a local CSDN fixture.
// The fixture has no network access or user profile. It never navigates a real author account.
partial class LoginWindow {
    const string MockId = "900123";
    const string MockLink = "https://blog.csdn.net/workbench_fixture/article/details/"+MockId;
    static string mockCsdnReads="2,316";
    const string MockEditor = @"<!doctype html><meta charset=utf-8>
      <a href='https://mp.csdn.net/mp_blog/manage/article?spm=fixture'><img id=fixture-avatar alt=workbench_fixture width=20 height=20></a>
      <input class=article-bar__title><pre class=editor__inner contenteditable=true></pre>
      <input id=import-markdown-file-input type=file><button class=button-save data-title=保存草稿>保存草稿</button>
      <button id=openPanel>发布文章</button><div hidden class=modal__publish-article>
      <div class='mark_selection' typename=tags><div class=mark_selection_title_el_tag><span class=el-tag>人工智能</span></div></div>
      <textarea placeholder='文章摘要'></textarea>
      <label><input type=radio checked>原创</label><label><input type=radio checked>全部可见</label>
      <label><input type=radio checked>否</label><button class=btn-b-red>发布文章</button></div>
      <script>
      const title=document.querySelector('.article-bar__title'),body=document.querySelector('pre');
      const file=document.querySelector('[type=file]'),store=sessionStorage;
      if(location.search){title.value=store.getItem('title');body.textContent=store.getItem('markdown');}
      file.onchange=async()=>{body.textContent=await file.files[0].text();};
      document.querySelector('.button-save').onclick=()=>{
        store.setItem('title',title.value);store.setItem('markdown',body.textContent);
        history.replaceState(null,'','?articleId=900123');
      };
      document.querySelector('#openPanel').onclick=()=>document.querySelector('.modal__publish-article').hidden=false;
      document.querySelector('.btn-b-red').onclick=()=>location.assign('"+MockLink+@"');
      </script>";

    static void StubSurface(WebView2 view,string name,string title,Action publicNavigation) {
        view.CoreWebView2InitializationCompleted+=(s,e)=>{
            if(!e.IsSuccess)return;
            var core=view.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(sender,args)=>{
                string url=args.Request.Uri;
                string html="<!doctype html><meta charset=utf-8>";
                if(name=="csdn" && IsCsdnEditor(new Uri(url))) html=MockEditor;
                if(name=="csdn-account" && url=="https://blog.csdn.net/workbench_fixture")
                    html+="<div class='profile-intro-name'>示例创作者</div>";
                if(name=="csdn-account" && url=="https://blog.csdn.net/another_fixture")
                    html+="<div class='profile-intro-name'>切换后的创作者</div>";
                if(name=="csdn-stats" && url==CreatorListUrl)
                    html+="<span class=nav-link role=tab>全部(19)</span><span class=nav-link role=tab>已发布(12)</span><span class=nav-link role=tab>草稿箱(7)</span>";
                if(name=="csdn-stats" && url==CsdnWorksAnalyticsUrl)
                    html+="<div class=metric-card><p class=metric-card__title>文章总数</p><p class=metric-card__num>10</p></div>"+
                        "<div class=metric-card><p class=metric-card__title>阅读量</p><p class=metric-card__num>"+mockCsdnReads+"</p></div><button>阅读量</button><p>总阅读数 9900</p>";
                if(name=="csdn" && url==MockLink) {
                    publicNavigation();
                    html+="<h1 class=title-article>"+System.Net.WebUtility.HtmlEncode(title)+"</h1>";
                }
                args.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)),
                    200,"OK","Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
        };
    }
    async Task DropFromWorkbench(string file) {
        var core=dashboard.CoreWebView2;
        await dashboard.ExecuteScriptAsync("let i=document.createElement('input');i.id='qaFile';i.type='file';document.body.append(i)");
        dynamic dom=json.DeserializeObject(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument","{}"));
        dynamic node=json.DeserializeObject(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector",json.Serialize(new {nodeId=dom["root"]["nodeId"],selector="#qaFile"})));
        await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",json.Serialize(new {nodeId=node["nodeId"], files=new[]{file}}));
        await dashboard.ExecuteScriptAsync("let transfer=new DataTransfer();transfer.items.add(document.querySelector('#qaFile').files[0]);document.querySelector('#dropzone').dispatchEvent(new DragEvent('drop',{bubbles:true,dataTransfer:transfer}));");
        await Wait(()=>Task.FromResult(queuedArticlePath==file),5,"拖入 Word 后工作台没有收到文件");
    }
    public static void CheckWorkbench(string file,string testRoot) {
        Directory.CreateDirectory(testRoot);
        // .NET Framework test process only. Redirect the host *before* constructing any form.
        var field=typeof(LoginWindow).GetField("dataFolder",BindingFlags.Static|BindingFlags.NonPublic);
        field.SetValue(null,testRoot);
        if((string)field.GetValue(null)!=testRoot)throw new Exception("不能隔离宿主存储");
        var f=new LoginWindow(null);
        if(Path.GetDirectoryName(f.historyFile)!=testRoot || Path.GetDirectoryName(f.csdnAccountMarker)!=testRoot)
            throw new Exception("实际存储目录没有隔离");
        int publicCount=0;
        bool leftWorkbench=false;
        string article=Path.GetFileNameWithoutExtension(file);
        f.dashboard.VisibleChanged+=(s,e)=>{if(f.busy && !f.dashboard.Visible)leftWorkbench=true;};
        foreach(var surface in new[]{f.page,f.profile,f.csdn})
            StubSurface(surface,surface==f.csdn?"csdn":"other",article,()=>publicCount++);
        StubSurface(f.csdnAccountPage,"csdn-account",article,()=>{});
        StubSurface(f.csdnAnalyticsPage,"csdn-stats",article,()=>{});
        f.ShowInTaskbar=false; f.Opacity=0;
        f.Shown+=async delegate {
            try {
                await f.Wait(()=>Task.FromResult(f.shellReady && f.timer.Enabled),20,"工作台启动失败");
                await f.OpenCsdn(false);
                await f.Wait(()=>Task.FromResult(f.csdnState=="editor"),12,"离线 CSDN 登录态未生效");
                await f.Wait(async()=>await f.dashboard.ExecuteScriptAsync("document.querySelector('#accountRows').textContent.includes('示例创作者') && document.querySelector('#targets').textContent.includes('示例创作者')")=="true",10,"工作台没有显示 CSDN 当前账号昵称");
                await f.dashboard.ExecuteScriptAsync("document.querySelector('[data-nav=accounts]').click()");
                if(f.loadingCsdnMetrics || f.csdnMetrics.state!="idle")throw new Exception("展开前开始了 CSDN 数据读取");
                await f.dashboard.ExecuteScriptAsync("document.querySelector('[data-analytics-toggle=csdn]').click()");
                await f.Wait(async()=>await f.dashboard.ExecuteScriptAsync("document.querySelector('#csdnAnalyticsArticles').textContent==='12' && document.querySelector('#csdnAnalyticsReads').textContent==='2,316'")=="true",11,"CSDN 文章数或访问量未显示");
                await f.dashboard.ExecuteScriptAsync("document.querySelector('[data-nav=publish]').click()");
                Console.WriteLine("PASS 展开 CSDN 数据后读取已发布总数和站内阅读量");
                await f.DropFromWorkbench(file);
                await f.dashboard.ExecuteScriptAsync("document.querySelector('#targets .csdn').parentNode.querySelector('input').click()");
                for(int i=1;i<=2;i++) {
                    await f.Wait(async()=>await f.dashboard.ExecuteScriptAsync("!document.querySelector('#publishArticle').disabled")== "true",8,"工作台未允许发送");
                    await f.dashboard.ExecuteScriptAsync("document.querySelector('#publishArticle').click()");
                    await f.Wait(()=>Task.FromResult(!f.busy && f.records.Count>=i),65,"没有收到工作台结果");
                    if(leftWorkbench || !f.dashboard.Visible || f.csdnVisible || f.batch.Count!=1 || f.batch[0].platform!="csdn" ||
                        f.batch[0].state!="published" || f.batch[0].url!=MockLink || f.batch[0].account!="示例创作者" || publicCount!=1 ||
                        f.records.Any(r=>r.file!=Path.GetFileName(file) || r.platform!="CSDN"))
                        throw new Exception("工作台回执、平台选择或防重失败："+f.status.Text+" mock导航="+publicCount);
                    Console.WriteLine(i==1 ? "PASS 工作台拖入 Word、仅选 CSDN、后台导入及草稿读回、提交、公开回执" : "PASS 同稿再次操作沿用已验证回执，未重复提交");
                    // Do not depend on how quickly the platform switches back to an editor after a receipt.
                    if(i==1 && !IsCsdnEditor(f.csdn.Source)) {
                        f.csdn.CoreWebView2.Navigate(CsdnEditorUrl);
                        await f.Wait(async()=>await f.CsdnScript("return true;")=="true",8,"测试账号未就绪");
                        await f.CheckCsdn();
                    }
                }
                var job=f.ReadCsdnJob();
                if(job==null || job.phase!="finished" || job.file!=file || job.runs!=1)throw new Exception("本地提交意图没有可靠落盘");
                int before=f.records.Count;
                await f.RecoverCsdnJob(null);
                await f.RecoverCsdnJob(file);
                if(f.records.Count!=before)throw new Exception("已结束任务意外重发");
                job.phase="pending"; f.SaveCsdnJob(job);
                using(var lease=f.AcquireCsdnJob()){
                    if(lease==null)throw new Exception("模拟锁无法获取");
                    await f.RecoverCsdnJob(null);
                    if(f.records.Count!=before || f.ReadCsdnJob().runs!=1)throw new Exception("竞争持锁时执行了恢复");
                }
                await f.RecoverCsdnJob(null);
                job=f.ReadCsdnJob();
                if(job.phase!="finished" || job.runs!=2 || f.records.Count!=before+1 || f.batch.Count!=1 || f.batch[0].platform!="csdn" ||
                    f.batch[0].state!="published" || publicCount!=1)throw new Exception("恢复未安全沿用原 CSDN 回执");
                job.phase="pending"; f.SaveCsdnJob(job);
                await f.RecoverCsdnJob(null);
                if(f.ReadCsdnJob().phase!="paused" || f.records.Count!=before+1)throw new Exception("中断恢复未在次数上限暂停");
                job.runs=1;job.phase="pending";job.hash="changed";f.SaveCsdnJob(job);
                await f.RecoverCsdnJob(null);
                if(f.ReadCsdnJob().phase!="paused" || f.records.Count!=before+1)throw new Exception("已改变的文档被恢复");
                // A real legacy record has no durable job intent. Explicit recovery requires BOTH the
                // same filename's unresolved CSDN history and a platform attempt keyed by exact file bytes.
                f.Record(Path.GetFileName(file)+"    CSDN · 待核实 · 回执尚未落盘。");
                await f.RecoverCsdnJob(file);
                if(f.records.Count!=before+3 || publicCount!=1 || f.batch[0].platform!="csdn" || f.batch[0].state!="published")
                    throw new Exception("显式恢复没有沿用原回执");
                Console.WriteLine("PASS 完成任务及已公开稿不自动重跑；抢锁、文件变更、恢复次数上限均拦截；匹配旧记录可恢复");
                await f.Wait(async()=>await f.CsdnScript("return document.querySelector('#fixture-avatar')!=null;")=="true",8,"编辑器未恢复");
                await f.csdn.ExecuteScriptAsync("document.querySelector('#fixture-avatar').alt='another_fixture';");
                await f.Wait(()=>Task.FromResult(f.CsdnAccountName=="切换后的创作者"),10,"切换账号后没有刷新昵称");
                if(f.csdnMetrics.articleCount!=null || f.csdnMetrics.reads!=null)
                    throw new Exception("换号之后显示了旧账号的数据");
                mockCsdnReads="0";
                await f.RefreshCsdnMetrics(true);
                if(f.csdnMetrics.reads!="0")throw new Exception("未读取换号后的访问量 0");
                f.csdn.CoreWebView2.Navigate("https://passport.csdn.net/login");
                await f.Wait(()=>Task.FromResult(f.csdnState=="signin" && f.CsdnAccountName==null),8,"退出登录后显示了旧昵称");
                Console.WriteLine("PASS 当前 CSDN 昵称同时显示在账号页、发布区和结果；换号及退出时清理旧昵称");
            } catch(Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode=1; }
            finally { f.busy=false;f.Close(); }
        };
        Application.Run(f);
    }
}
class WorkbenchCsdnCheck {
    [STAThread] static void Main(string[] args) {
        if(args.Length!=1||!File.Exists(args[0]))throw new ArgumentException("测试需传入一份不带图片的 Word");
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS","--proxy-server=127.0.0.1:9 --disable-background-networking");
        Application.EnableVisualStyles();
        LoginWindow.CheckWorkbench(Path.GetFullPath(args[0]),Path.Combine(Path.GetTempPath(),"zhigao-offline-"+Guid.NewGuid().ToString("N")));
    }
}
