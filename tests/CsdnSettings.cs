using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

partial class LoginWindow {
    LoginWindow(bool isolateSettings) {
        ShowInTaskbar=false; Opacity=0; Controls.Add(csdn); csdn.Visible=true;
    }
    const string CsdnSettingsFixture = @"<!doctype html><meta charset='utf-8'>
        <input class='article-bar__title' value='AI时代程序员的出路思考'><pre class='editor__inner' contenteditable='true'>AI时代</pre>
        <div class='modal__publish-article'>
          <div class='form-entry required'><label>文章标签</label><div class='mark_selection' typename='tags'><div class='mark_selection_title_el_tag' id='tags'></div></div></div>
          <div class='form-entry'><label>文章摘要</label><textarea placeholder='文章摘要'></textarea></div>
          <div class='form-entry'><label>封面</label><button>从本地上传</button></div>
          <div class='form-entry'><label>参与活动</label><div class='el-select' data-field='campaign'><input placeholder='请选择创作活动'></div></div>
          <div class='form-entry'><label>创作话题</label><div class='el-select' data-field='topic'><input placeholder='请选择创作话题'></div></div>
          <label><input type=radio name='type' checked>原创</label><label><input type=radio name='scope' checked>全部可见</label>
          <label><input type=radio name='sync' checked>否</label><button class='btn-b-red' id='submit'>发布文章</button>
        </div><script>
          const mode=new URLSearchParams(location.search).get('mode');
          window.clicks=0;window.summary='';
          document.querySelector('textarea').oninput=e=>window.summary=e.target.value;
          setTimeout(()=>{tags.innerHTML='<span class=el-tag>人工智能</span>';},1000);
          if(mode==='required'){let r=document.createElement('div');r.className='form-entry required';r.innerHTML='<label>封面</label><input type=file>';submit.before(r);}
          if(mode==='review'){document.querySelector('textarea').value='用户写的摘要';}
          const related=mode==='related'||mode==='ignore-selection'||mode==='changed'||mode==='consent';
          const offered={campaign:related?['AI 与程序员征稿活动','鸿蒙开发录']:['AtomGit 开源同行','鸿蒙心迹 · 开发录原创征稿活动'],
            topic:related?['AI 绘画','程序员的出路']:['IT疑难杂症诊疗室','开源项目吐槽大会']};
          if(mode==='ambiguous')offered.topic=['程序员的出路交流','程序员的出路分享'];
          const decoy=document.createElement('div');decoy.className='el-select-dropdown';
          decoy.innerHTML='<li class=el-select-dropdown__item>程序员的出路</li>';
          decoy.onclick=()=>{throw Error('wrong dropdown')};document.body.append(decoy);
          for(const select of document.querySelectorAll('.el-select')){
            const input=select.querySelector('input');let list;
            if(mode==='prefilled' && select.dataset.field==='topic')input.value='程序员的出路';
            if(mode==='unrelated-existing' && select.dataset.field==='topic')input.value='鸿蒙原生应用开发';
            input.onclick=()=>{
              list=document.createElement('div');list.className='el-select-dropdown';
              for(const label of offered[select.dataset.field]){
                const opt=document.createElement('li');opt.className='el-select-dropdown__item';opt.textContent=label;
                opt.onclick=()=>{if(mode!=='ignore-selection')input.value=label;list.remove();
                  if(mode==='consent' && select.dataset.field==='campaign'){
                    let consent=document.createElement('div');consent.setAttribute('role','dialog');consent.textContent='报名需同意活动授权协议';
                    document.body.append(consent);
                  }
                };list.append(opt);
              }
              document.body.append(list);
            };
            input.onkeydown=e=>{if(e.key==='Escape')list?.remove()};
          }
          submit.onclick=()=>{
            window.clicks++;
            let notice=document.createElement('div');notice.className='el-message';
            notice.textContent=mode==='reject'?'请选择文章标签':mode==='transient'?'本次提交失败，系统繁忙，请稍后重试':mode==='review'?'已提交审核':'发布成功';
            document.body.append(notice);setTimeout(()=>notice.remove(),90);
          };</script>";

    public static void StartSettingsChecks(string report) {
        var test = new LoginWindow(true);
        test.Shown+=async delegate {
            try {
                var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Path.GetDirectoryName(report),"isolated-settings-profile"),
                    new CoreWebView2EnvironmentOptions("--proxy-server=127.0.0.1:9 --disable-background-networking"));
                await test.csdn.EnsureCoreWebView2Async(env);
                var core=test.csdn.CoreWebView2;
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(s,e)=> {
                    e.Response=env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(CsdnSettingsFixture)),200,"OK",
                        "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                };
                File.WriteAllText(report,"");
                foreach(var mode in new[]{"accepted","required","reject","review","transient","related","ambiguous","changed","ignore-selection","wrong-title","consent","prefilled","unrelated-existing"}) {
                    string url=CsdnEditorUrl+"?articleId=123&mode="+mode;
                    core.Navigate(url);
                    await test.Wait(async ()=>test.csdn.Source != null && test.csdn.Source.AbsoluteUri==url &&
                        await test.CsdnScript("return new URLSearchParams(location.search).get('mode')==="+test.json.Serialize(mode)+";")=="true",15,"fixture");
                    var doc=new CsdnDocument {Title="AI时代程序员的出路思考",Markdown="TL;DR\n\n这是用于检验发布表单能否在本地提取原文摘要的正文段落。"};
                    if(mode=="wrong-title") doc.Title="另一份稿件";
                    await test.OpenCsdnPublishPanel();
                    bool blocked=false;
                    try { await test.PrepareCsdnSettings(doc); }
                    catch(CsdnSettingsNeeded ex) { if(!ex.Message.Contains("封面") && !ex.Message.Contains("标题") && !ex.Message.Contains("条款") && !ex.Message.Contains("原选项"))throw; blocked=true; }
                    catch(TimeoutException ex) { if(mode!="ignore-selection" || !ex.Message.Contains("未确认"))throw; blocked=true; }
                    if(mode=="required" || mode=="ignore-selection" || mode=="wrong-title" || mode=="consent" || mode=="unrelated-existing") {
                        if(!blocked || await test.CsdnScript("return window.clicks;")!="0")throw new Exception("未在设置错误处停止");
                    } else {
                        if(blocked)throw new Exception("误判可选设置");
                        string expected=mode=="review" ? "用户写的摘要":CsdnSummary(doc.Markdown);
                        string campaign=mode=="related" || mode=="changed" ? "AI 与程序员征稿活动":"";
                        string topic=mode=="related" || mode=="changed" || mode=="prefilled" ? "程序员的出路":"";
                        if(await test.CsdnScript("return document.querySelector('textarea').value==="+test.json.Serialize(expected)+
                            " && document.querySelector('input[placeholder*=活动]').value==="+test.json.Serialize(campaign)+
                            " && document.querySelector('input[placeholder*=话题]').value==="+test.json.Serialize(topic)+";")!="true")
                            throw new Exception("摘要、活动或话题设置错误");
                        if(mode=="changed") {
                            await test.CsdnScript("document.querySelector('input[placeholder*=话题]').value='其他话题';");
                            if(await test.CsdnScript("const id='123';"+CsdnFinalClick)!="false" || await test.CsdnScript("return window.clicks;")!="0")
                                throw new Exception("提交前设置变化未被识别");
                            File.AppendAllText(report,"PASS "+mode+Environment.NewLine);
                            continue;
                        }
                        if(await test.CsdnScript("const id='123';"+CsdnFinalClick)!="true")throw new Exception("无法安全提交");
                        await Task.Delay(200);
                        string receipt=await test.WaitForCsdnReceipt("123",doc.Title);
                        string label=mode=="review"?"审核中":mode=="reject"?"需处理":"已提交";
                        if(receipt==null || (mode=="transient"?receipt!="retry-rejected":!receipt.StartsWith(label+" · ")) || await test.CsdnScript("return window.clicks;")!="1")
                            throw new Exception("平台回执错误 "+receipt);
                    }
                    File.AppendAllText(report,"PASS "+mode+Environment.NewLine);
                }
                var draft=new CsdnListing{Id="123", State="draft"};
                if(!CanRetryCsdnSubmission("false",null,draft,"123") || !CanRetryCsdnSubmission("true","retry-rejected",draft,"123") ||
                    CanRetryCsdnSubmission(null,null,draft,"123") || CanRetryCsdnSubmission("true",null,draft,"123") ||
                    CanRetryCsdnSubmission("true","retry-rejected",draft,"124") || CanRetryCsdnSubmission("false",null,new CsdnListing{Id="123", State="review"},"123"))
                    throw new Exception("待核实与可重试提交的边界错误");
                File.AppendAllText(report,"PASS retry evidence and duplicate protection"+Environment.NewLine);                if (IsCsdnReceipt("https://blog.csdn.net/u/article/details/124","123")||
                    !IsCsdnReceipt("https://blog.csdn.net/u/article/details/123","123"))
                    throw new Exception("ID mismatch");
                if(!ShouldResetCsdnEditor(new Uri("https://blog.csdn.net/u/article/details/123"),"123") ||
                    !ShouldResetCsdnEditor(new Uri(CsdnEditorUrl+"?articleId=123"),"123") ||
                    ShouldResetCsdnEditor(new Uri(CsdnEditorUrl+"?articleId=555"),"123") ||
                    ShouldResetCsdnEditor(new Uri(CsdnEditorUrl),"123"))
                    throw new Exception("没有保护其他草稿，或成功页未回到背景编辑器");
                File.AppendAllText(report,"PASS post-publication editor routing"+Environment.NewLine);
                Environment.ExitCode=0;
            } catch(Exception ex) { Environment.ExitCode=2; File.AppendAllText(report,"FAIL "+ex); }
            finally { test.Close(); }
        };
        Application.Run(test);
    }
}
class CsdnSettingsCheck {
    [STAThread] static void Main(string[] args){
        Application.EnableVisualStyles();
        LoginWindow.StartSettingsChecks(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"report.txt"));
    }
}


