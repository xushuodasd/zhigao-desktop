using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string FixturePublic = "https://zhuanlan.zhihu.com/p/910123";
    const string FixtureDraft = FixturePublic + "/edit";
    const string DifferentPublic = "https://zhuanlan.zhihu.com/p/910124";
    const string FixtureTitle = "发布状态测试";
    const string FixtureAnalytics = @"<!doctype html><meta charset=utf-8>
        <button aria-pressed=true>近7日</button><button>近14日</button><button>近30日</button>
        <button id=lifetime aria-pressed=false>累计数据</button>
        <div><div>发布数</div><div>12</div><div>今日 1</div></div>
        <div><div>阅读数</div><div id=reads>9</div><div>今日 5</div></div>
        <div><div>播放数</div><div id=plays>7</div><div>今日 0</div></div>
        <p>2026-11-19 17:00:00 更新</p><script>
        document.querySelector('#lifetime').onclick=event=>{
            event.target.setAttribute('aria-pressed','true');
            setTimeout(()=>{document.querySelector('#reads').textContent='1.2万';document.querySelector('#plays').textContent='0'},250);
        };
        </script>";
    static string fixtureState="draft";

    static void StubZhihuPublication(WebView2 target) {
        target.CoreWebView2InitializationCompleted+=(s,e)=>{
            if(!e.IsSuccess)return;
            var core=target.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(sender,args)=>{
                string address=args.Request.Uri;
                string html="<!doctype html><meta charset=utf-8>";
                bool publicRoute=address==FixturePublic || address==DifferentPublic;
                bool editor=address==FixtureDraft || address==WriteUrl;
                int statusCode=publicRoute && fixtureState=="denied"?403:editor || publicRoute && fixtureState!="draft"?200:404;
                string headers="Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store";
                if(address==ZhihuAnalyticsUrl) {
                    html=FixtureAnalytics;
                    statusCode=200;
                } else if(address=="https://www.zhihu.com/api/v4/me") {
                    html="{\"articles_count\":42}";
                    headers="Content-Type: application/json\r\nCache-Control: no-store";
                    statusCode=200;
                } else if(address==FixturePublic && fixtureState=="redirect") {
                    statusCode=302;
                    headers+="\r\nLocation: "+DifferentPublic;
                } else if((address==FixturePublic && (fixtureState=="published" || fixtureState=="denied")) ||
                    (address==DifferentPublic && fixtureState=="redirect"))
                    html+="<article class=Post-Main><h1 class=Post-Title>"+FixtureTitle+"</h1><div class=Post-RichTextContainer><p>正文</p></div></article>";
                else if(editor)
                    html+="<textarea placeholder=标题></textarea><div class=public-DraftEditor-content contenteditable=true></div>";
                else html+="<main>页面不存在</main>";
                args.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)),
                    statusCode,"Test",headers);
            };
        };
    }

    public static void CheckManualZhihuPublish(string root) {
        Directory.CreateDirectory(root);
        typeof(LoginWindow).GetField("dataFolder",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).SetValue(null,root);
        File.WriteAllLines(Path.Combine(root,"publication-results.txt"),new[] {
            "2026-11-19 14:59    "+FixtureTitle+".docx    草稿 · "+DifferentPublic+"/edit",
            "2026-11-19 15:00    "+FixtureTitle+".docx    草稿 · "+FixtureDraft
        });
        var window=new LoginWindow(null) { ShowInTaskbar=false, Opacity=0 };
        StubZhihuPublication(window.page);
        StubZhihuPublication(window.profile);
        StubZhihuPublication(window.zhihuPublicPage);
        StubZhihuPublication(window.zhihuAnalyticsPage);
        window.Shown+=async delegate {
            try {
                await window.Wait(()=>System.Threading.Tasks.Task.FromResult(window.shellReady && window.timer.Enabled),12,"工作台未加载");
                var entry=window.records[0];
                if(entry.label!="草稿")throw new Exception("测试稿初始状态错误");
                foreach(string unavailable in new[]{"draft","denied","redirect","empty"}) {
                    fixtureState=unavailable;
                    await window.VerifyDashboardRecord("verify-history:0");
                    if(entry.label!="草稿" || File.Exists(window.VerificationsPath("知乎")))
                        throw new Exception("没有公开回执时误判成功："+unavailable);
                }
                Console.WriteLine("PASS 404、403、跳转到其他编号和空白响应均保留草稿");
                await window.dashboard.ExecuteScriptAsync("document.querySelector('[data-nav=history]').click()");
                await window.dashboard.ExecuteScriptAsync("document.querySelector('#historyRows .history-article[aria-label^=打开草稿]').click()");
                await window.Wait(()=>System.Threading.Tasks.Task.FromResult(window.page.Source!=null && window.page.Source.AbsoluteUri==FixtureDraft),5,"无法从工作台打开已存草稿");
                fixtureState="published";
                window.page.CoreWebView2.Navigate(FixturePublic);
                await window.Wait(()=>System.Threading.Tasks.Task.FromResult(window.page.Source!=null && window.page.Source.AbsoluteUri==FixturePublic),5,"模拟手动发布的页面没有打开");
                window.check.PerformClick();
                await window.Wait(()=>System.Threading.Tasks.Task.FromResult(!window.refreshingZhihuDrafts && entry.label=="已发布" && entry.link==FixturePublic),8,"手动发布后返回工作台，记录仍显示草稿");
                await window.Wait(async()=>await window.dashboard.ExecuteScriptAsync(
                    "document.querySelector('#historyRows .history-row .badge')?.textContent === '成功'")=="true",3,"发布记录未显示成功");
                using(var reopened=new LoginWindow(null)) {
                    if(reopened.records[0].label!="已发布" || reopened.records[0].link!=FixturePublic || reopened.records[1].label!="草稿")
                        throw new Exception("重启后未恢复核实状态，或更新了同名的其他稿件");
                }
                Console.WriteLine("PASS 手动发布后返回，原记录更新为成功，重启后保留且同名不同编号未误改");
                await window.dashboard.ExecuteScriptAsync("document.querySelector('[data-nav=accounts]').click()");
                if(window.loadingZhihuMetrics || window.zhihuMetrics.state!="idle")throw new Exception("未展开即读取知乎数据");
                await window.dashboard.ExecuteScriptAsync("document.querySelector('[data-analytics-toggle=zhihu]').click()");
                await window.Wait(()=>{
                    var metrics=window.zhihuMetrics;
                    return System.Threading.Tasks.Task.FromResult(metrics.state=="ready" && metrics.articleCount=="42" && metrics.reads=="1.2万" && metrics.plays=="0" &&
                        metrics.readsToday=="5" && metrics.playsToday=="0" && metrics.updated=="2026-11-19 17:00:00");
                },12,"未按账号文章数和创作中心累计统计分别读取");
                await window.Wait(async()=>await window.dashboard.ExecuteScriptAsync(
                    "document.querySelector('#analyticsArticles').textContent==='42' && document.querySelector('#analyticsPlays').textContent==='0'")== "true",4,"数据未显示在账号页");
                Console.WriteLine("PASS 账号页读取文章总数与累计阅读、播放，区分真实零值和今日值");
            } catch(Exception ex) {
                Console.Error.WriteLine(ex.Message);
                if(ex.Message.Contains("创作中心累计统计"))
                    Console.Error.WriteLine("离线统计快照 "+window.json.Serialize(window.zhihuMetrics)+" loading="+window.loadingZhihuMetrics+" account="+window.previous);
                Environment.ExitCode=1;
            }
            finally { window.busy=false;window.Close(); }
        };
        Application.Run(window);
    }
}

class ZhihuManualPublishCheck {
    [STAThread] static void Main() {
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS","--proxy-server=127.0.0.1:9 --disable-background-networking");
        Application.EnableVisualStyles();
        LoginWindow.CheckManualZhihuPublish(Path.Combine(Path.GetTempPath(),"zhigao-zhihu-check-"+Guid.NewGuid().ToString("N")));
    }
}
