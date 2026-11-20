using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

partial class LoginWindow {
    // One outstanding local CSDN intent. The per-document platform attempt still protects the final click.
    sealed class CsdnJob {
        public string file { get; set; }
        public string hash { get; set; }
        public string phase { get; set; }
        public int runs { get; set; }
    }
    const int MaxCsdnStarts = 2;
    CsdnJob recoveringCsdn;
    string JobPath { get { return Path.Combine(dataFolder,"csdn-job.json"); } }

    FileStream AcquireCsdnJob() {
        Directory.CreateDirectory(dataFolder);
        try { return new FileStream(JobPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch (IOException ex) {
            int code = ex.HResult & 65535;
            if (code == 32 || code == 33) return null;
            throw;
        }
    }
    CsdnJob ReadCsdnJob() {
        if (!File.Exists(JobPath)) return null;
        var value = json.Deserialize<CsdnJob>(File.ReadAllText(JobPath));
        if (value == null || String.IsNullOrWhiteSpace(value.file) || String.IsNullOrWhiteSpace(value.hash) ||
            !Path.IsPathRooted(value.file) || value.runs < 0 ||
            (value.phase != "pending" && value.phase != "finished" && value.phase != "paused"))
            throw new InvalidDataException("本地 CSDN 任务记录无法读取，请核实上一次发布结果。");
        return value;
    }
    void SaveCsdnJob(CsdnJob job) { WriteCsdnAttempt(JobPath,json.Serialize(job)); }
    static string JobDigest(Stream stream) {
        using (var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream));
    }
    bool HasUnresolvedCsdnHistory(string file,string hash) {
        var latest = records.Find(r => r.platform == "CSDN" &&
            String.Equals(r.file,Path.GetFileName(file),StringComparison.OrdinalIgnoreCase));
        return latest != null && (latest.label == "待核实" || latest.label == "需处理" ||
            latest.label == "审核中" || latest.label == "已提交") &&
            File.Exists(Path.Combine(dataFolder,"Attempts-CSDN",hash+".txt"));
    }
    void JobNotice(string file,string reason) {
        status.Text="CSDN · "+reason;
        if (activeItem!=null) Record(Path.GetFileName(file)+"    CSDN · "+reason);
        else Render();
    }
    async Task ExecuteCsdnJob(CsdnJob job) {
        job.runs++;
        job.phase="pending";
        SaveCsdnJob(job);
        try { await PublishCsdn(job.file); }
        finally {
            // Returned outcomes, including "待核实", are terminal for unattended startup.
            // A process interruption leaves the durable pending marker in place.
            job.phase="finished";
            SaveCsdnJob(job);
        }
    }
    async Task RunCsdnJob(string file) {
        file=Path.GetFullPath(file);
        if (recoveringCsdn != null) {
            if (!String.Equals(recoveringCsdn.file,file,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前恢复任务与所选稿件不符。");
            // RecoverCsdnJob holds the job lock and a read-only file handle across this await.
            await ExecuteCsdnJob(recoveringCsdn);
            return;
        }
        using (var lease=AcquireCsdnJob()) {
            if (lease==null) { JobNotice(file,"未发出 · 另一个知稿窗口正在处理 CSDN 任务。"); return; }
            using (var source=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                var previous=ReadCsdnJob();
                var digest=JobDigest(source);
                if (previous!=null && previous.phase=="pending" &&
                    (!String.Equals(previous.file,file,StringComparison.OrdinalIgnoreCase) || previous.hash!=digest)) {
                    JobNotice(file,"未发出 · 另一份中断的 CSDN 任务正在等待恢复。");
                    return;
                }
                await ExecuteCsdnJob(new CsdnJob {file=file, hash=digest});
            }
        }
    }
    async Task RecoverCsdnJob(string legacyFile) {
        try { await RecoverCsdnJobCore(legacyFile); }
        catch (IOException ex) { JobNotice(legacyFile,"待核实 · 无法读取或锁定上次任务。"+ex.Message); }
        catch (UnauthorizedAccessException) { JobNotice(legacyFile,"待核实 · 无法访问上次任务保存的 Word 或本地记录。"); }
        catch (ArgumentException) { JobNotice(legacyFile,"待核实 · 上次任务的路径或本地记录无效。"); }
    }
    async Task RecoverCsdnJobCore(string legacyFile) {
        if (busy) return;
        using (var lease=AcquireCsdnJob()) {
            if (lease==null) return;
            var job=ReadCsdnJob();
            bool pending=job!=null && job.phase=="pending";
            if (!pending && String.IsNullOrEmpty(legacyFile)) return;
            string file=pending ? job.file : Path.GetFullPath(legacyFile);
            if (pending && legacyFile!=null && !String.Equals(file,Path.GetFullPath(legacyFile),StringComparison.OrdinalIgnoreCase)) {
                JobNotice(file,"待核实 · 当前中断任务与指定稿件不同。");
                return;
            }
            if (!String.Equals(Path.GetExtension(file),".docx",StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(file) || (pending && job.runs>=MaxCsdnStarts)) {
                if (pending) { job.phase="paused"; SaveCsdnJob(job); }
                JobNotice(file,File.Exists(file) && pending && job.runs>=MaxCsdnStarts ?
                    "待核实 · 上次恢复也曾中断，请核对结果后从工作台继续。" : "待核实 · 找不到原 Word，已暂停恢复上次提交。");
                return;
            }
            using (var source=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                string digest=JobDigest(source);
                if (pending && job.hash!=digest) {
                    job.phase="paused";SaveCsdnJob(job);
                    JobNotice(file,"待核实 · 原 Word 已被修改，已暂停恢复上次提交。");
                    return;
                }
                if (legacyFile!=null && !HasUnresolvedCsdnHistory(file,digest)) {
                    JobNotice(file,"待核实 · 找不到与这份 Word 完全匹配的 CSDN 提交记录。");
                    return;
                }
                if (!pending) job=new CsdnJob {file=file,hash=digest};
                queuedArticlePath=file;
                queuedArticleBytes=source.Length;
                recoveringCsdn=job;
                try { await PublishSelected(file,false,true); }
                finally { recoveringCsdn=null; }
            }
        }
    }
}
