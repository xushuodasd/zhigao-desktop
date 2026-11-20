using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

partial class LoginWindow {
    async Task VerifySohuPublishFixture(string testRoot) {
        var site=CreatorSites.Single(candidate=>candidate.Id=="sohu");
        var draft=new Uri(SohuArticleDraftPrefix+"100001");
        const string link="https://www.sohu.com/a/1104032649_123063036", title="原始稿件";
        string publishedHtml="<link rel=\"canonical\" href=\""+link+"\"><meta property=\"og:title\" content=\""+title+
            "\"><article id=\"mp-editor\">"+new string('文',50)+"</article>";
        if(!SohuPublicPageMatches(publishedHtml,link,title) || SohuPublicPageMatches(publishedHtml,link,"同名以外稿件") ||
            SohuPublicPageMatches(publishedHtml.Replace("</article>","登录后查看全文</article>"),link,title))
            throw new InvalidOperationException("搜狐匿名文章核验未正确区分公开正文、标题和登录门槛。");
        foreach(string scenario in new[]{"unverified","short","declaration","category","dialog","different-id","originality","success"}) {
            bool blocked=new[]{"unverified","short","declaration","category","dialog"}.Contains(scenario);
            string filepath=Path.Combine(testRoot,"搜狐号发布夹具 "+scenario+".docx");
            using(var stream=File.Create(filepath)) {
                using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
                using(var entry=zip.CreateEntry("word/document.xml").Open())
                using(var writer=new StreamWriter(entry,new UTF8Encoding(false))) {
                    writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>");
                    writer.Write(new string('稿',scenario=="short"?60:230));
                    writer.Write("</w:t></w:r></w:p></w:body></w:document>");
                }
                stream.Position=0;
                var word=new CreatorWord(filepath,stream);
                var session=new CreatorSession(site);
                using(var view=session.Web) {
                    platform.Controls.Add(view);
                    view.Visible=true;
                    view.BringToFront();
                    await InitCreatorView(view,site,allowPopups:false);
                    var core=view.CoreWebView2;
                    core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                    int journal=0, posts=0;
                    bool unrecordedSubmission=false, wrongRequest=false;
                    core.WebResourceRequested+=(sender,args)=>{
                        var uri=new Uri(args.Request.Uri);
                        string payload=null, contentType="application/json; charset=utf-8";
                        if(uri==draft && args.Request.Method=="GET") {
                            payload=SohuPublishFixtureHtml(word,scenario);
                            contentType="text/html; charset=utf-8";
                        } else if(uri.AbsoluteUri=="https://mp.sohu.com/mpbp/bp/news/v4/news/publish/v2" &&
                            args.Request.Method=="POST") {
                            posts++;
                            unrecordedSubmission|=journal!=1;
                            string data=scenario=="different-id"?"100002":scenario=="originality"?"{\"id\":100001}":"100001";
                            payload="{\"success\":true,\"code\":2000000,\"data\":"+data+"}";
                        } else if(args.Request.Method!="GET")wrongRequest=true;
                        args.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(payload??"")),
                            payload==null?404:200,payload==null?"Not Found":"OK","Content-Type: "+contentType+"\r\nCache-Control: no-store");
                    };
                    core.Navigate(draft.AbsoluteUri);
                    await Wait(async()=>CreatorMismatch(word,await ReadCreatorSnapshot(site,view))==null,6,
                        "搜狐号发布夹具未加载。");
                    if(scenario=="success") {
                        await view.ExecuteScriptAsync(@"document.querySelector('.ql-editor').insertAdjacentHTML('beforeend',
                            '<ol id=list-test><li data-list=bullet>甲</li><li data-list=bullet>乙</li><li data-list=ordered>丙</li></ol>')");
                        var lists=(await ReadCreatorSnapshot(site,view)).lists;
                        if(lists.Length!=2 || lists[0].tag!="ul" || lists[0].count!=2 || lists[0].offset!=word.Text.Length ||
                            lists[1].tag!="ol" || lists[1].count!=1 || lists[1].start!=1 || lists[1].offset!=word.Text.Length+2)
                            throw new Exception("搜狐 Quill 混合列表的编号、条目数或位置有误。");
                        await view.ExecuteScriptAsync("document.getElementById('list-test').remove()");
                    }
                    var submission=await SubmitSohuCreatorDraft(session,word,draft,()=>journal++);
                    string expectedState=blocked?"blocked":scenario=="success"?"submitted":"uncertain";
                    string clicks=await view.ExecuteScriptAsync("window.publishClicks || 0");
                    if(submission.state!=expectedState || journal!=(blocked?0:1) || posts!=(blocked?0:1) ||
                        clicks!=(blocked?"0":"1") || unrecordedSubmission || wrongRequest)
                        throw new InvalidOperationException("搜狐发布夹具不符合门槛、回执或单次提交要求："+scenario+
                            " "+submission.state+" / "+journal+" / "+posts+" / "+clicks);
                }
            }
        }
        Console.WriteLine("PASS 搜狐号发布门槛、已登记后唯一点击、同编号回执和原创争议待核实");
    }

    static string SohuPublishFixtureHtml(CreatorWord word,string scenario) {
        return "<!doctype html><meta charset=utf-8><textarea placeholder='请输入标题'>"+word.Title+"</textarea>"+
            "<div class='ql-editor' contenteditable='true'><div>"+word.Html+"</div></div>"+
            "<section id=info-source-signature><div class=option-item><div class=info-radio>"+
            "<label>含有AI生成内容 <input type=radio "+(scenario=="declaration"?"":"checked")+"></label></div></div></section>"+
            "<div id=channel-select><div class=plugin-dropdown><div class=select-main><span class=select-text>科技</span></div></div>"+
            "<div class=plugin-dropdown><div class=select-main><span class='select-text "+(scenario=="category"?"placeholder":"")+"'>"+
            (scenario=="category"?"请选择":"互联网")+"</span></div></div></div>"+
            (scenario=="dialog"?"<div class=win-mask>发布提示</div>":"")+
            "<ul class=button-list><li id=publish class='publish-report-btn active "+(scenario=="unverified"?"disable-button":"positive-button")+
            "' report-attr='{\"type\":\"button\",\"code\":\"content-button-commit\"}'>发布</li>"+
            "<li class='publish-report-btn normal timeout-pub negative-button'>定时发布</li>"+
            "<li class='publish-report-btn normal negative-button'>存草稿</li>"+
            (scenario=="unverified"?"<li class=rest-number>未实名暂无法发布文章</li>":"")+"</ul>"+
            "<script>document.querySelector('#publish').onclick=()=>{window.publishClicks=(window.publishClicks||0)+1;"+
            "fetch('/mpbp/bp/news/v4/news/publish/v2',{method:'POST'}).then(r=>r.text());};</script>";
    }

    public static int RunSohuCreatorPublishFixture(string testRoot) {
        typeof(LoginWindow).GetField("dataFolder",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,testRoot);
        int result=1;
        using(var form=new LoginWindow(null) {ShowInTaskbar=false,Opacity=0}) {
            form.Shown+=async delegate {
                try {
                    await form.Wait(()=>Task.FromResult(form.shellReady),10,"工作台未加载");
                    await form.VerifySohuPublishFixture(testRoot);
                    result=0;
                } catch(Exception ex) {Console.Error.WriteLine(ex);}
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        return result;
    }
}

static class SohuCreatorPublishCheck {
    [STAThread] static int Main() {
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS","--proxy-server=127.0.0.1:9 --disable-background-networking");
        Application.EnableVisualStyles();
        string root=Path.Combine(Path.GetTempPath(),"Zhigao-SohuPublishFixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return LoginWindow.RunSohuCreatorPublishFixture(root);
    }
}
