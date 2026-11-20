using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

partial class LoginWindow {
    static byte[] DrawCreatorTable(string[][] rows) {
        int columns=rows[0].Length;
        if(columns>16 || rows.Length>120)throw new InvalidDataException("网易号表格过大，无法生成清晰图片。");
        const int padding=18;
        int columnWidth=Math.Max(210,1480/columns);
        int width=columnWidth*columns+1;
        float contentWidth=columnWidth-2*padding;
        using(var font=new Font("Microsoft YaHei UI",31,FontStyle.Regular,GraphicsUnit.Pixel))
        using(var format=new StringFormat())
        using(var measuringBitmap=new Bitmap(1,1))
        using(var measure=Graphics.FromImage(measuringBitmap)) {
            format.FormatFlags=StringFormatFlags.MeasureTrailingSpaces;
            float[] heights=rows.Select(row=>row.Max(cell=>measure.MeasureString(cell.Length==0?" ":cell,font,
                new SizeF(contentWidth,100000),format).Height)+2*padding).ToArray();
            int height=(int)Math.Ceiling(heights.Sum())+1;
            if(height>6000 || (long)width*height>14000000)
                throw new InvalidDataException("网易号表格过长，无法生成清晰图片。");
            using(var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb))
            using(var image=Graphics.FromImage(bitmap))
            using(var border=new Pen(Color.FromArgb(183,194,207),2))
            using(var ink=new SolidBrush(Color.FromArgb(28,38,53)))
            using(var output=new MemoryStream()) {
                image.Clear(Color.White);
                image.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float y=0;
                for(int row=0;row<rows.Length;row++) {
                    for(int column=0;column<columns;column++) {
                        float x=column*columnWidth;
                        image.DrawString(rows[row][column],font,ink,
                            new RectangleF(x+padding,y+padding,contentWidth,heights[row]-2*padding+1),format);
                    }
                    image.DrawLine(border,0,y,width-1,y);
                    y+=heights[row];
                }
                image.DrawLine(border,0,height-1,width-1,height-1);
                for(int column=0;column<=columns;column++)
                    image.DrawLine(border,column*columnWidth,0,column*columnWidth,height-1);
                bitmap.Save(output,ImageFormat.Png);
                if(output.Length>5L*1024*1024)throw new InvalidDataException("网易号生成的表格图片超过 5 MB。");
                return output.ToArray();
            }
        }
    }
}
