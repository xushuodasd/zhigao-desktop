using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

partial class LoginWindow {
    sealed class CreatorWord {
        static readonly XNamespace W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static readonly XNamespace A="http://schemas.openxmlformats.org/drawingml/2006/main";
        static readonly XNamespace R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static readonly XNamespace PR="http://schemas.openxmlformats.org/package/2006/relationships";
        static readonly XNamespace V="urn:schemas-microsoft-com:vml";
        public readonly string Title;
        public string Html { get; private set; }
        public string Text { get { return text.ToString(); } }
        public readonly List<CreatorTable> Tables=new List<CreatorTable>();
        public readonly List<CreatorImage> Images=new List<CreatorImage>();
        public readonly List<CreatorList> Lists=new List<CreatorList>();
        readonly StringBuilder text=new StringBuilder();
        readonly StringBuilder originalText=new StringBuilder();
        readonly bool renderTablesAsImages;
        readonly Dictionary<string,XElement> links=new Dictionary<string,XElement>();
        readonly Dictionary<string,ListDefinition> numbering=new Dictionary<string,ListDefinition>();
        readonly string markerPrefix="ZHIGAO"+Guid.NewGuid().ToString("N")+"IMG";
        ZipArchive archive;

        public CreatorWord(string file,Stream source,bool renderTablesAsImages=false) {
            this.renderTablesAsImages=renderTablesAsImages;
            Title=Path.GetFileNameWithoutExtension(file).Trim();
            if(Title.Length==0 || Title.Length>100)throw new InvalidDataException("请使用 1 到 100 字的文件名作为标题。");
            if(source.Length>30L*1024*1024)throw new InvalidDataException("Word 超过 30 MB。");
            using(var zip=new ZipArchive(source,ZipArchiveMode.Read,true)) {
                archive=zip;
                var document=ReadXml("word/document.xml",true);
                var body=document.Root==null?null:document.Root.Element(W+"body");
                if(body==null)throw new InvalidDataException("找不到 Word 正文。");
                if(body.Descendants().Any(e=>e.Name==V+"imagedata" || e.Name==W+"object" || e.Name==W+"ins" ||
                    e.Name==W+"del" || e.Name==W+"altChunk" || e.Name==W+"txbxContent" ||
                    e.Name==W+"footnoteReference" || e.Name==W+"endnoteReference" ||
                    e.Name.NamespaceName=="http://schemas.openxmlformats.org/officeDocument/2006/math"))
                    throw new InvalidDataException("稿件含旧式图形、嵌入对象、修订、文本框、脚注或公式，当前无法完整导入。");
                var rels=ReadXml("word/_rels/document.xml.rels",false);
                if(rels!=null)
                    foreach(var rel in rels.Descendants(PR+"Relationship")) {
                        string id=(string)rel.Attribute("Id");
                        if(!String.IsNullOrEmpty(id))links[id]=rel;
                    }
                ReadNumbering();
                Html=RenderBlocks(body,false);
                if(originalText.ToString()!=String.Concat(body.Descendants(W+"t").Select(n=>Compact(n.Value))) ||
                    Images.Count(image=>image.Data==null)!=body.Descendants(A+"blip").Count())
                    throw new InvalidDataException("Word 中存在暂时无法完整转换的正文或图片。");
                if(Text.Length==0 && Images.Count==0)throw new InvalidDataException("Word 没有可导入的正文。");
                if(originalText.Length>250000)throw new InvalidDataException("正文过长，无法可靠核对。");
            }
        }

        XDocument ReadXml(string path,bool required) {
            var entry=archive.GetEntry(path);
            if(entry==null) {
                if(required)throw new InvalidDataException("无法读取 Word 文档包。");
                return null;
            }
            if(entry.Length>8L*1024*1024)throw new InvalidDataException("Word 文档结构超出可核对大小。");
            using(var stream=entry.Open())return XDocument.Load(stream);
        }

        sealed class ListDefinition {
            public string Kind;
            public int Start;
            public int Used;
        }

        static string Value(XElement node) { return node==null?null:(string)node.Attribute(W+"val"); }
        void ReadNumbering() {
            var doc=ReadXml("word/numbering.xml",false);
            if(doc==null || doc.Root==null)return;
            foreach(var number in doc.Root.Elements(W+"num")) {
                string id=(string)number.Attribute(W+"numId"), reference=Value(number.Element(W+"abstractNumId"));
                var format=doc.Root.Elements(W+"abstractNum").FirstOrDefault(e=>(string)e.Attribute(W+"abstractNumId")==reference);
                var level=format==null?null:format.Elements(W+"lvl").FirstOrDefault(e=>(string)e.Attribute(W+"ilvl")=="0");
                var kind=Value(level==null?null:level.Element(W+"numFmt"));
                if(id==null || (kind!="bullet" && kind!="decimal"))continue;
                var levelOverride=number.Elements(W+"lvlOverride").FirstOrDefault(e=>(string)e.Attribute(W+"ilvl")=="0");
                if(levelOverride!=null && levelOverride.Element(W+"lvl")!=null)continue;
                string startText=Value(levelOverride==null?null:levelOverride.Element(W+"startOverride")) ?? Value(level.Element(W+"start"));
                int start;
                if(!Int32.TryParse(startText,out start) || start<0)start=1;
                numbering[id]=new ListDefinition { Kind=kind=="bullet"?"ul":"ol",Start=start };
            }
        }

        IEnumerable<XElement> Blocks(XElement container) {
            foreach(var node in container.Elements()) {
                if(node.Name==W+"p" || node.Name==W+"tbl")yield return node;
                if(node.Name!=W+"sdt")continue;
                var inner=node.Element(W+"sdtContent");
                if(inner==null)continue;
                foreach(var block in Blocks(inner))yield return block;
            }
        }

        string RenderBlocks(XElement container,bool inTable) {
            var output=new StringBuilder();
            string listId=null;
            CreatorList list=null;
            foreach(var block in Blocks(container)) {
                var props=block.Element(W+"pPr");
                var listProps=props==null?null:props.Element(W+"numPr");
                string id=listProps==null?null:Value(listProps.Element(W+"numId"));
                if(id=="0")id=null;
                if(list!=null && id!=listId) {
                    output.Append("</").Append(list.tag).Append(">");
                    list=null;
                }
                if(block.Name==W+"tbl") {
                    if(inTable)throw new InvalidDataException("暂时无法验证嵌套表格。");
                    output.Append(RenderTable(block));
                    continue;
                }
                if(id==null) {
                    string style=Value(props==null?null:props.Element(W+"pStyle"));
                    var heading=Regex.Match(style??"",@"^(?:Heading|标题)\s*([1-6])$",RegexOptions.IgnoreCase);
                    string tag=heading.Success?"h"+heading.Groups[1].Value:"p";
                    output.Append("<").Append(tag).Append(">").Append(RenderInline(block,inTable)).Append("</").Append(tag).Append(">");
                    continue;
                }
                ListDefinition def;
                string level=Value(listProps.Element(W+"ilvl"))??"0";
                if(level!="0" || !numbering.TryGetValue(id,out def))
                    throw new InvalidDataException("暂时无法验证这份 Word 的多级列表或编号格式。");
                if(list==null) {
                    listId=id;
                    list=new CreatorList { tag=def.Kind, offset=text.Length, start=def.Kind=="ol"?checked(def.Start+def.Used):1 };
                    output.Append("<").Append(list.tag);
                    if(list.start!=1)output.Append(" start=\"").Append(list.start).Append("\"");
                    output.Append(">");
                    Lists.Add(list);
                }
                list.count++;
                def.Used++;
                output.Append("<li>").Append(RenderInline(block,inTable)).Append("</li>");
            }
            if(list!=null)output.Append("</").Append(list.tag).Append(">");
            return output.ToString();
        }

        string RenderTable(XElement table) {
            if(table.Descendants().Any(e=>e.Name==W+"tbl" || e.Name==W+"vMerge" || e.Name==W+"hMerge" ||
                (e.Name==W+"gridSpan" && Value(e)!="1") || (e.Name==W+"gridBefore" && Value(e)!="0") ||
                (e.Name==W+"gridAfter" && Value(e)!="0")))
                throw new InvalidDataException("暂时无法验证合并、嵌套或省略单元格的表格。");
            var output=new StringBuilder("<table><tbody>");
            var rows=new List<string[]>();
            var shape=new CreatorTable { offset=text.Length };
            foreach(var row in table.Elements(W+"tr")) {
                var cells=new List<string>();
                output.Append("<tr>");
                foreach(var cell in row.Elements(W+"tc")) {
                    int start=text.Length;
                    int listCount=Lists.Count;
                    output.Append("<td>").Append(RenderBlocks(cell,true)).Append("</td>");
                    if(renderTablesAsImages && Lists.Count!=listCount)
                        throw new InvalidDataException("网易号无法按原格式转换带编号列表的表格。");
                    cells.Add(text.ToString(start,text.Length-start));
                }
                output.Append("</tr>");
                if(cells.Count==0 || (rows.Count>0 && rows[0].Length!=cells.Count))
                    throw new InvalidDataException("无法确定 Word 表格的列数。");
                rows.Add(cells.ToArray());
            }
            if(rows.Count==0 || table.Descendants(W+"tr").Count()!=rows.Count)
                throw new InvalidDataException("无法确定 Word 表格的行数。");
            shape.cells=rows.ToArray();
            if(renderTablesAsImages) {
                var displayRows=table.Elements(W+"tr")
                    .Select(row=>row.Elements(W+"tc").Select(TableCellDisplayText).ToArray()).ToArray();
                if(!displayRows.SelectMany(row=>row).Select(Compact).SequenceEqual(rows.SelectMany(row=>row)))
                    throw new InvalidDataException("网易号表格图片无法覆盖原表格的全部文字。");
                if(Images.Count>=40)throw new InvalidDataException("网易号表格图片和 Word 配图合计超过 40 张。");
                var image=new CreatorImage { Offset=shape.offset, Token=markerPrefix+Images.Count+"END",
                    Name="word-table-"+(Images.Count+1)+".png", Mime="image/png", Data=DrawCreatorTable(displayRows) };
                Images.Add(image);
                text.Length=shape.offset;
                return "<p>"+image.Token+"</p>";
            }
            Tables.Add(shape);
            return output.Append("</tbody></table>").ToString();
        }

        string TableCellDisplayText(XElement cell) {
            return String.Join("\n",Blocks(cell).Select(paragraph=>String.Concat(paragraph.Descendants().Select(element=>{
                if(element.Name==W+"t")return element.Value;
                if(element.Name==W+"br" || element.Name==W+"cr")return "\n";
                return element.Name==W+"tab"?"　":"";
            }))));
        }

        string RenderInline(XElement para,bool inTable) {
            var output=new StringBuilder();
            foreach(var child in para.Elements()) {
                if(child.Name==W+"pPr")continue;
                output.Append(RenderRunTree(child,inTable));
            }
            return output.ToString();
        }

        string RenderRunTree(XElement node,bool inTable) {
            if(node.Name==W+"t") {
                var compact=Compact(node.Value);
                text.Append(compact);
                originalText.Append(compact);
                return WebUtility.HtmlEncode(node.Value);
            }
            if(node.Name==W+"tab")return "　";
            if(node.Name==W+"br" || node.Name==W+"cr")return "<br>";
            if(node.Name==A+"blip")return ReadImage(node,inTable);
            if(node.Name==W+"rPr" || node.Name==W+"instrText")return "";
            string inner=String.Concat(node.Elements().Select(e=>RenderRunTree(e,inTable)));
            if(node.Name==W+"r") {
                var props=node.Element(W+"rPr");
                if(props==null)return inner;
                if(Enabled(props,"b"))inner="<strong>"+inner+"</strong>";
                if(Enabled(props,"i"))inner="<em>"+inner+"</em>";
                if(Enabled(props,"u"))inner="<u>"+inner+"</u>";
                if(Enabled(props,"strike"))inner="<s>"+inner+"</s>";
            }
            if(node.Name==W+"hyperlink") {
                XElement link;
                string id=(string)node.Attribute(R+"id");
                Uri target;
                if(id!=null && links.TryGetValue(id,out link) &&
                    Uri.TryCreate((string)link.Attribute("Target"),UriKind.Absolute,out target) &&
                    (target.Scheme=="https" || target.Scheme=="http" || target.Scheme=="mailto"))
                    inner="<a href=\""+WebUtility.HtmlEncode(target.AbsoluteUri)+"\">"+inner+"</a>";
            }
            return inner;
        }

        static bool Enabled(XElement props,string name) {
            var element=props.Element(W+name);
            string value=Value(element);
            return element!=null && value!="0" && value!="false" && value!="off" && value!="none";
        }

        string ReadImage(XElement node,bool inTable) {
            if(inTable)throw new InvalidDataException("暂时无法验证表格内的图片。");
            string id=(string)node.Attribute(R+"embed");
            XElement link;
            Uri image;
            if(id==null || !links.TryGetValue(id,out link) || (string)link.Attribute("TargetMode")=="External" ||
                !Uri.TryCreate(new Uri("https://docx.invalid/word/document.xml"),(string)link.Attribute("Target"),out image) ||
                image.Host!="docx.invalid" || !image.AbsolutePath.StartsWith("/word/media/",StringComparison.Ordinal))
                throw new InvalidDataException("Word 中引用了一张外部或无法读取的图片。");
            string path=Uri.UnescapeDataString(image.AbsolutePath.TrimStart('/'));
            var entry=archive.GetEntry(path);
            string extension=Path.GetExtension(path).ToLowerInvariant();
            string mime=extension==".jpg" || extension==".jpeg"?"image/jpeg":extension==".png"?"image/png":
                extension==".gif"?"image/gif":extension==".webp"?"image/webp":null;
            if(entry==null || entry.Length<=0 || entry.Length>5L*1024*1024 || mime==null || Images.Count>=40)
                throw new InvalidDataException("图文导入支持最多 40 张 PNG、JPEG、GIF 或 WebP，每张不超过 5 MB。");
            var details=new CreatorImage { Offset=text.Length, Token=markerPrefix+Images.Count+"END",
                Name=Path.GetFileName(path), Mime=mime, Part=path };
            Images.Add(details);
            return details.Token;
        }
    }

    sealed class CreatorImage {
        public int Offset;
        public string Token, Name, Mime, Part, SavedUrl;
        public byte[] Data;
    }
    sealed class CreatorTable {
        public int offset { get; set; }
        public string[][] cells { get; set; }
    }
    sealed class CreatorList {
        public int offset { get; set; }
        public string tag { get; set; }
        public int start { get; set; }
        public int count { get; set; }
    }
}
