using System;
using System.IO;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow:Form {
 readonly WebView2 page = new WebView2 { Dock=DockStyle.Fill };
 readonly Label status = new Label();
 readonly Button check = new Button();
 const string WriteUrl="https://zhuanlan.zhihu.com/write";
 readonly string dir=AppDomain.CurrentDomain.BaseDirectory;
 string mode;
 int passes;
 void Record(string s){}
 void Out(string s){File.AppendAllText(Path.Combine(dir,"report.txt"),s+Environment.NewLine);}
 async Task One(string test){
   mode=test; attempt=Path.Combine(dir,Guid.NewGuid().ToString("N")+".attempt");
   prepared=true; issued=false; receipt=null; title="离线样稿"; body="正文"; imageCount=0;
   var nav=new TaskCompletionSource<bool>();
   EventHandler<CoreWebView2NavigationCompletedEventArgs> cb=(s,e)=>nav.TrySetResult(e.IsSuccess);
   page.CoreWebView2.NavigationCompleted+=cb;
   page.CoreWebView2.Navigate("https://zhuanlan.zhihu.com/p/123/edit?mode="+test);
   if(!await nav.Task)throw new Exception("map navigation failed");
   page.CoreWebView2.NavigationCompleted-=cb;
   string msg="";
   try { await Submit(); } catch(Exception ex){msg=ex.Message;}
   bool ok=issued && File.Exists(attempt) && (test=="success"?(receipt=="https://zhuanlan.zhihu.com/p/123" && File.ReadAllText(attempt)==receipt && msg=="") : (receipt==null && File.ReadAllText(attempt)=="" && msg!=""));
   Out((ok?"PASS ":"FAIL ")+test+" | "+msg+" | receipt="+receipt);
   if(ok)passes++;
 }
 async Task Run(){
  try {
   var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(dir,"isolated-profile"),new CoreWebView2EnvironmentOptions("--proxy-server=127.0.0.1:9 --disable-background-networking"));
   await page.EnsureCoreWebView2Async(env); var c=page.CoreWebView2;
   c.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
   c.WebResourceRequested+=(s,e)=>{
      var u=new Uri(e.Request.Uri);
      bool editor=u.Host=="zhuanlan.zhihu.com"&&u.AbsolutePath=="/p/123/edit";
      string html="<!doctype html><meta charset=utf-8><h1>本地回执模拟</h1>";
      if(editor) html=@"<!doctype html><meta charset=utf-8><textarea placeholder='标题'>离线样稿</textarea><div class='public-DraftEditor-content' contenteditable='true'><div data-text='true'>正文</div></div>
      <button id='pub'>发布</button><div id='modal' role='dialog' hidden><span id='hint'></span><button id='yes'>确认发布</button></div><script>
      const mode=new URLSearchParams(location.search).get('mode');
      pub.onclick=()=>{if(mode==='wrong'){location.href='/p/555';return;} modal.hidden=false;if(mode==='required')hint.textContent='请选择必填选项';};
      yes.onclick=()=>location.href='/p/123';
      </script>";
      e.Response=env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)),200,"OK","Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
   };
   foreach(string test in new[]{"success","required","wrong"})await One(test);
   Environment.ExitCode=passes==3?0:2;
  }catch(Exception ex){Out("ERROR "+ex);Environment.ExitCode=2;} finally{Close();}
 }
 LoginWindow(){Text="知稿离线回执测试";Width=720;Height=460;Controls.Add(page);Shown+=async delegate{await Run();};}
 [STAThread] static void Main(){Application.EnableVisualStyles();Application.Run(new LoginWindow());}
}
