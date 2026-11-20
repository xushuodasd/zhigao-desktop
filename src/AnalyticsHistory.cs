using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class LoginWindow {
    sealed class AnalyticsDailySample {
        public string date { get; set; }
        public string collectedAt { get; set; }
        public string articleCount { get; set; }
        public string reads { get; set; }
        public string plays { get; set; }
    }

    readonly Timer analyticsTimer=new Timer {Interval=15000};
    readonly Dictionary<string,DateTimeOffset> analyticsLastAttempt=new Dictionary<string,DateTimeOffset>();
    readonly Dictionary<string,string> analyticsCollectedAt=new Dictionary<string,string>();
    readonly Dictionary<string,AnalyticsDailySample[]> analyticsDays=new Dictionary<string,AnalyticsDailySample[]>();
    readonly HashSet<string> analyticsWriteFailed=new HashSet<string>();
    bool automaticAnalyticsRunning;
    static readonly CultureInfo AnalyticsCulture=CultureInfo.InvariantCulture;
    const int AnalyticsVisibleDays=30;
    string AnalyticsDirectory { get { return Path.Combine(dataFolder,"Analytics"); } }

    void StartAccountAnalyticsRefresh() {
        analyticsTimer.Tick+=async delegate { await RefreshDueAccountAnalytics(); };
        analyticsTimer.Start();
        FormClosed+=delegate {analyticsTimer.Dispose();};
    }

    static bool AnalyticsRefreshDue(DateTimeOffset previous,DateTimeOffset now) {
        return previous.Date!=now.Date || now<previous || now-previous>=TimeSpan.FromHours(1);
    }

    async Task RefreshDueAccountAnalytics() {
        if(IsDisposed || automaticAnalyticsRunning || busy || picking || verifying)return;
        automaticAnalyticsRunning=true;
        try {
            var platforms=new List<string>();
            if(editorReady)platforms.Add("zhihu");
            if(csdnState=="editor")platforms.Add("csdn");
            platforms.AddRange(CreatorSites.Where(site=>rememberedCreators.Contains(site.Id) && !creatorSignInRequired.Contains(site.Id)).Select(site=>site.Id));
            foreach(string platformId in platforms) {
                if(IsDisposed || busy || picking || verifying)break;
                DateTimeOffset attempted;
                if(analyticsLastAttempt.TryGetValue(platformId,out attempted) && !AnalyticsRefreshDue(attempted,DateTimeOffset.Now))continue;
                try {
                    if(platformId=="zhihu" && editorReady)await RefreshZhihuMetrics(true);
                    else if(platformId=="csdn" && csdnState=="editor")await RefreshCsdnMetrics(true);
                    else {
                        var site=Array.Find(CreatorSites,s=>s.Id==platformId);
                        if(site!=null && !creatorSignInRequired.Contains(platformId))await RefreshCreatorMetrics(site,true);
                    }
                } catch(InvalidOperationException) { }
                catch(System.Runtime.InteropServices.COMException) { }
            }
        } finally {automaticAnalyticsRunning=false;}
    }

    DateTimeOffset BeginAccountAnalytics(string platformId) {
        var started=DateTimeOffset.Now;
        analyticsLastAttempt[platformId]=started;
        return started;
    }

    void SaveAccountAnalytics(string platformId,DateTimeOffset started,string articles,string reads,string plays,string reportedDate=null) {
        var now=DateTimeOffset.Now;
        if(now.Date!=started.Date)return;
        string collected=now.ToString("yyyy-MM-dd HH:mm",AnalyticsCulture);
        analyticsCollectedAt[platformId]=collected;
        try {
            string directory=Path.Combine(AnalyticsDirectory,platformId);
            Directory.CreateDirectory(directory);
            using(File.Open(Path.Combine(directory,"snapshot.lock"),FileMode.OpenOrCreate,FileAccess.Write,FileShare.None)) {
                var trafficDay=AnalyticsTrafficDay(platformId,now.Date,reportedDate);
                if(trafficDay==now.Date)SaveAnalyticsDay(directory,now.Date,collected,articles,reads,plays);
                else {
                    SaveAnalyticsDay(directory,now.Date,collected,articles,null,null);
                    SaveAnalyticsDay(directory,trafficDay,collected,null,reads,plays);
                }
                analyticsDays[platformId]=ReadAnalyticsDays(directory);
            }
            analyticsWriteFailed.Remove(platformId);
        } catch(IOException) {analyticsWriteFailed.Add(platformId);}
        catch(UnauthorizedAccessException) {analyticsWriteFailed.Add(platformId);}
    }

    static DateTime AnalyticsTrafficDay(string platformId,DateTime today,string reportedDate) {
        if(platformId=="toutiao" || platformId=="weibo" || platformId=="qiehao")return today.AddDays(-1);
        DateTime reported;
        if(platformId=="zhihu" && reportedDate!=null && reportedDate.Length>=10 &&
            DateTime.TryParseExact(reportedDate.Substring(0,10),"yyyy-MM-dd",AnalyticsCulture,DateTimeStyles.None,out reported) && reported<=today)
            return reported;
        return today;
    }

    void SaveAnalyticsDay(string directory,DateTime day,string collected,string articles,string reads,string plays) {
        articles=CreatorMetricNumber(articles);
        reads=CreatorMetricNumber(reads);
        plays=CreatorMetricNumber(plays);
        if(articles==null && reads==null && plays==null)return;
        var date=day.ToString("yyyy-MM-dd",AnalyticsCulture);
        var path=Path.Combine(directory,date+".json");
        var row=ReadAnalyticsDay(path) ?? new AnalyticsDailySample {date=date};
        if(String.CompareOrdinal(row.collectedAt,collected)>0)return;
        row.collectedAt=collected;
        row.articleCount=articles ?? row.articleCount;
        row.reads=reads ?? row.reads;
        row.plays=plays ?? row.plays;
        WriteCsdnAttempt(path,json.Serialize(row));
    }

    AnalyticsDailySample ReadAnalyticsDay(string path) {
        if(!File.Exists(path))return null;
        try {return json.Deserialize<AnalyticsDailySample>(File.ReadAllText(path));}
        catch(ArgumentException) {return null;}
        catch(InvalidOperationException) {return null;}
    }

    AnalyticsDailySample[] ReadAnalyticsDays(string directory) {
        if(!Directory.Exists(directory))return new AnalyticsDailySample[0];
        return Directory.EnumerateFiles(directory,"????-??-??.json").OrderByDescending(path=>path,StringComparer.Ordinal)
            .Take(AnalyticsVisibleDays).Select(ReadAnalyticsDay).Where(row=>row!=null).ToArray();
    }

    void LoadAccountAnalyticsHistory() {
        foreach(string platformId in new[]{"zhihu","csdn"}.Concat(CreatorSites.Select(site=>site.Id))) {
            try {
                var history=ReadAnalyticsDays(Path.Combine(AnalyticsDirectory,platformId));
                analyticsDays[platformId]=history;
                var mostRecent=history.Select(row=>row.collectedAt).OrderByDescending(value=>value,StringComparer.Ordinal).FirstOrDefault();
                if(mostRecent!=null)analyticsCollectedAt[platformId]=mostRecent;
            }
            catch(IOException) {analyticsWriteFailed.Add(platformId);}
            catch(UnauthorizedAccessException) {analyticsWriteFailed.Add(platformId);}
        }
    }
}
