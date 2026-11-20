using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

partial class LoginWindow {
    static byte[] DrawCreatorTitleCover(string title) {
        const int width=1200, height=900;
        var textBounds=new RectangleF(106,159,988,590);
        using(var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb))
        using(var canvas=Graphics.FromImage(bitmap))
        using(var paper=new LinearGradientBrush(new Rectangle(0,0,width,height),
            Color.FromArgb(13,28,51),Color.FromArgb(26,63,108),LinearGradientMode.ForwardDiagonal))
        using(var accent=new SolidBrush(Color.FromArgb(115,197,253)))
        using(var ink=new SolidBrush(Color.FromArgb(247,250,255)))
        using(var format=new StringFormat {LineAlignment=StringAlignment.Center})
        using(var output=new MemoryStream()) {
            canvas.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            canvas.FillRectangle(paper,0,0,width,height);
            canvas.FillRectangle(accent,106,113,108,8);
            for(int fontSize=86;fontSize>=32;fontSize-=6) {
                using(var font=new Font("Microsoft YaHei UI",fontSize,FontStyle.Bold,GraphicsUnit.Pixel)) {
                    var measure=canvas.MeasureString(title,font,textBounds.Size,format);
                    if(measure.Height>=textBounds.Height-8 && fontSize>32)continue;
                    canvas.DrawString(title,font,ink,textBounds,format);
                    break;
                }
            }
            bitmap.Save(output,ImageFormat.Png);
            return output.ToArray();
        }
    }
}
