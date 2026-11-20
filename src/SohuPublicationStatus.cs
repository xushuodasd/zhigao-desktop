using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

partial class LoginWindow {
    const string SohuPublicationPath="/mpbp/bp/news/v4/users/news";

    sealed class SohuPublicationData {
        public SohuPublicationRow[] news { get; set; }
    }
    sealed class SohuPublicationRow {
        public string id { get; set; }
        public string userId { get; set; }
        public int? status { get; set; }
        public string title { get; set; }
        public int? visibleToLoginedUsers { get; set; }
        public int? type { get; set; }
        public int? secureScore { get; set; }
    }

    async Task<CreatorSubmission> ReadSohuPublication(WebView2 view,string id,string file) {
        var core=view.CoreWebView2;
        var fresh=new TaskCompletionSource<Tuple<Uri,CreatorSubmission>>(TaskCreationOptions.RunContinuationsAsynchronously);
        string publicCandidate=null,officialTitle=null;
        Func<string,CreatorSubmission> parse=body=>ParseSohuPublication(id,body,(link,titleText)=>{publicCandidate=link;officialTitle=titleText;});
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs> observe=async(sender,e)=>{
            Uri address;
            if(e.Request.Method!="GET" || e.Response.StatusCode!=200 || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out address) ||
                !CanUseSohuPublicationRequest(address))return;
            CreatorSubmission seen=null;
            try {
                using(var stream=await e.Response.GetContentAsync())
                using(var reader=new StreamReader(stream)) {
                    var buffer=new char[1048577];
                    int length=await reader.ReadBlockAsync(buffer,0,buffer.Length);
                    if(length<buffer.Length)seen=parse(new string(buffer,0,length));
                }
            } catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(System.Runtime.InteropServices.COMException) { }
            fresh.TrySetResult(Tuple.Create(address,seen));
        };
        core.WebResourceResponseReceived+=observe;
        try {
            if(!await NavigateCreatorPublicationList(view,SohuMetricsHome) || view.Source==null ||
                view.Source.GetLeftPart(UriPartial.Path)!=SohuMetricsHome)return null;
            if(await Task.WhenAny(fresh.Task,Task.Delay(5500))!=fresh.Task)return null;
        } finally { core.WebResourceResponseReceived-=observe; }

        var first=await fresh.Task;
        if(first.Item2!=null)return await ConfirmSohuPublicPage(first.Item2,publicCandidate,officialTitle);
        var observed=first.Item1;
        string title=Path.GetFileNameWithoutExtension(file).Trim();
        string query=Regex.Replace(observed.Query.Substring(1),@"(^|&)newsType=[0-9]+",match=>match.Groups[1].Value+"newsType=1");
        var articles=new UriBuilder(observed) {Query=query};
        if(title.Length>0) {
            var search=new UriBuilder(articles.Uri) {Query=query+"&titleKeyword="+Uri.EscapeDataString(title)+"&sortType=0"};
            var found=await ReadCreatorPublicationResponse(view,search.Uri,parse);
            if(found!=null)return await ConfirmSohuPublicPage(found,publicCandidate,officialTitle);
        }
        var result=await ReadCreatorPublicationResponse(view,articles.Uri,parse);
        return await ConfirmSohuPublicPage(result,publicCandidate,officialTitle);
    }

    static Task<CreatorSubmission> ConfirmSohuPublicPage(CreatorSubmission result,string candidate,string title) {
        if(result==null || result.state!="submitted" || String.IsNullOrEmpty(candidate) || String.IsNullOrEmpty(title))
            return Task.FromResult(result);
        // When the feed omits its access flag, confirm the public article independently of the creator session.
        return Task.Run(()=>{
            try {
                var request=(HttpWebRequest)WebRequest.Create(candidate);
                request.AllowAutoRedirect=false;
                request.Timeout=6500;
                request.ReadWriteTimeout=6500;
                request.UserAgent="Mozilla/5.0";
                using(var response=(HttpWebResponse)request.GetResponse())
                using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) {
                    if(response.StatusCode!=HttpStatusCode.OK || response.ResponseUri.AbsoluteUri!=candidate)return result;
                    var buffer=new char[1048577];
                    int count=reader.ReadBlock(buffer,0,buffer.Length);
                    if(count<buffer.Length && SohuPublicPageMatches(new string(buffer,0,count),candidate,title))
                        return new CreatorSubmission {state="published",url=candidate};
                }
            } catch(WebException) { }
            catch(IOException) { }
            return result;
        });
    }

    static bool SohuPublicPageMatches(string html,string candidate,string title) {
        var canonical=Regex.Match(html,@"<link\s+rel=""canonical""\s+href=""([^""]+)""",RegexOptions.IgnoreCase);
        var heading=Regex.Match(html,@"<meta\s+property=""og:title""\s+content=""([^""]+)""",RegexOptions.IgnoreCase);
        var article=Regex.Match(html,@"<article\b[^>]*\bid=""mp-editor""[^>]*>([\s\S]*?)</article>",RegexOptions.IgnoreCase);
        return canonical.Success && WebUtility.HtmlDecode(canonical.Groups[1].Value)==candidate.Split('?')[0] &&
            heading.Success && WebUtility.HtmlDecode(heading.Groups[1].Value)==title && article.Success &&
            Regex.Replace(article.Groups[1].Value,@"<[^>]*>|\s","").Length>=40 &&
            !Regex.IsMatch(article.Groups[1].Value,@"(?:登录后|登录才|登录即可)(?:查看|阅读)全文");
    }

    static bool CanUseSohuPublicationRequest(Uri address) {
        return address.Scheme=="https" && address.Host=="mp.sohu.com" && address.IsDefaultPort && address.UserInfo.Length==0 &&
            address.AbsolutePath==SohuPublicationPath && Regex.IsMatch(QiehaoQueryValue(address,"newsType")??"",@"\A[0-9]+\z") &&
            QiehaoQueryValue(address,"statusType")=="1" && QiehaoQueryValue(address,"pno")=="1" &&
            QiehaoQueryValue(address,"columnId")=="" && QiehaoQueryValue(address,"streamId")=="" &&
            !Regex.IsMatch(address.Query,@"(?:^\?|&)(?:titleKeyword|sortType)=");
    }

    CreatorSubmission ParseSohuPublication(string id,string payload,Action<string,string> checkVisibility=null) {
        try {
            var reply=json.Deserialize<SohuMetricsReply<SohuPublicationData>>(payload);
            if(reply==null || !reply.success || reply.code!=2000000 || reply.data==null || reply.data.news==null)return null;
            var rows=reply.data.news.Where(row=>row!=null && row.id==id).Take(2).ToArray();
            if(rows.Length!=1)return null;
            var article=rows[0];
            switch(article.status) {
                case 1: return new CreatorSubmission {state="draft"};
                case 2: return new CreatorSubmission {state="review",message="搜狐号内容管理显示该稿件正在审核。"};
                case 3: return new CreatorSubmission {state="failed",message="搜狐号内容管理显示该稿件未通过审核。"};
                case 4:
                    if(article.visibleToLoginedUsers==1)
                        return new CreatorSubmission {state="blocked",message="搜狐号显示该文章仅登录用户可见。"};
                    if(!Regex.IsMatch(article.userId??"",@"\A[0-9]{1,32}\z"))
                        return new CreatorSubmission {state="submitted",message="搜狐号显示已发布，暂未确认文章链接或公开范围。"};
                    string host=article.type==30?"biznews.sohu.com":"www.sohu.com";
                    string query=article.type!=30 && article.secureScore==10?"?sec=wd":"";
                    string publicUrl="https://"+host+"/a/"+id+"_"+article.userId+query;
                    if(article.visibleToLoginedUsers!=0) {
                        if(!article.visibleToLoginedUsers.HasValue && checkVisibility!=null && !String.IsNullOrWhiteSpace(article.title))
                            checkVisibility(publicUrl,article.title);
                        return new CreatorSubmission {state="submitted",message="搜狐号显示已发布，暂未确认文章的公开访问状态。"};
                    }
                    return new CreatorSubmission {state="published",url=publicUrl};
                case 5: return new CreatorSubmission {state="submitted",message="搜狐号显示该稿件已进入定时发布。"};
                case 7:
                case 9: return new CreatorSubmission {state="blocked",message="搜狐号显示该稿件已删除。"};
                default: return null;
            }
        } catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }
}
