using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
partial class LoginWindow {
 public static async Task LiveDraft(LoginWindow f,string file) {
    string result;
    try{
       await f.Wait(()=>Task.FromResult(f.editorReady),32,"editor was not ready");
       f.busy=true;
       await f.ImportDocument(file);
       await f.Wait(()=>f.Act("publish-ready"),6,"publish button readiness timed out");
       if(!f.prepared || !await f.Matches(true))
         throw new Exception("final comparison or ready button failed");
       var contents=await f.Contents();
       result="PASS: production import, title, complete body and "+contents["readyImages"]+" images verified; publish available. "+f.page.Source.AbsolutePath;
    }catch(Exception e){result="FAIL: "+e.Message+" | "+f.status.Text+" path "+f.page.Source.AbsolutePath;}
    if(result.StartsWith("FAIL")) result+=" ready="+await f.InspectAction("publish-ready")+" dialog="+await f.InspectAction("dialog")+" matches="+await f.Matches(true);
    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"result.txt"),result);
    f.busy=false;f.Close();
 }
 public static Form VerifyForm(string file){var f=new LoginWindow(null);f.Shown+=async delegate{await LiveDraft(f,file);};return f;}
}
class LiveCheck {
 [STAThread] static void Main(string[] args){Application.EnableVisualStyles();Application.Run(LoginWindow.VerifyForm(args[0]));}
}



