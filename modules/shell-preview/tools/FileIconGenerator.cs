using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MoriDesk {
 // Code-native artwork. These are fallback file icons, not thumbnails or a
 // change to the application's brand. Render every size separately so the
 // 16 px design never contains illegible miniature extension text.
 public static class FileIconGenerator {
  static readonly int[] Sizes={16,24,32,48,64,128,256};
  sealed class Style { internal string Color,Kind; internal Style(string color,string kind){Color=color;Kind=kind;} }
  static Style For(string extension){
   string ext="."+extension.ToLowerInvariant();
   if(extension=="file")return new Style("#6B778A","text");
   if(ext==".pdf")return new Style("#BC4D57","text");
   if(ext==".svg")return new Style("#8062B5","vector");
   if(ext==".ai")return new Style("#B77729","vector");
   if(ext==".psd"||ext==".psb")return new Style("#3A75A6","image");
   if(FileFormats.Pictures.Split(' ').Contains(ext))return new Style("#357F91","image");
   if(FileFormats.Videos.Split(' ').Contains(ext))return new Style("#4B7BBC","video");
   if(FileFormats.Audio.Split(' ').Contains(ext))return new Style("#8B64AE","audio");
   if(FileFormats.Models.Split(' ').Contains(ext))return new Style("#6A65AE","model");
   if(new[]{".xlsx",".ods",".csv",".tsv"}.Contains(ext))return new Style("#3E8065","table");
   if(new[]{".pptx",".odp"}.Contains(ext))return new Style("#AC7150","presentation");
   if(new[]{".docx",".odt"}.Contains(ext))return new Style("#527AA6","text");
   return new Style("#718093","text");
  }
  static Color ColorOf(string hex){return ColorTranslator.FromHtml(hex);}
  static GraphicsPath Page(){
   var p=new GraphicsPath();p.AddLines(new[]{new PointF(14,4),new PointF(41,4),new PointF(54,17),new PointF(54,56)});
   p.AddArc(46,52,8,8,0,90);p.AddLine(50,60,14,60);p.AddArc(10,52,8,8,90,90);p.AddLine(10,56,10,8);p.AddArc(10,4,8,8,180,90);p.CloseFigure();return p;
  }
  static GraphicsPath Rounded(float x,float y,float w,float h,float radius){
   var p=new GraphicsPath();float d=radius*2;p.AddArc(x,y,d,d,180,90);p.AddArc(x+w-d,y,d,d,270,90);p.AddArc(x+w-d,y+h-d,d,d,0,90);p.AddArc(x,y+h-d,d,d,90,90);p.CloseFigure();return p;
  }
  static void Line(Graphics g,Pen p,float x1,float y1,float x2,float y2){g.DrawLine(p,x1,y1,x2,y2);}
  static void Glyph(Graphics g,Style style,bool small){
   // 64-unit coordinate system. The single-family symbol remains legible in
   // small Explorer list views, where there is no room for a format label.
   float shift=small?5:0;g.TranslateTransform(0,shift);
   using(var pen=new Pen(ColorOf(style.Color),small?4:2.8f))using(var brush=new SolidBrush(ColorOf(style.Color))){
    pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
    if(style.Kind=="image"){
     g.DrawRectangle(pen,20,18,24,18);g.FillEllipse(brush,35,21,4,4);g.DrawLines(pen,new[]{new PointF(21,33),new PointF(28,26),new PointF(34,32),new PointF(38,28),new PointF(43,33)});
    }else if(style.Kind=="video"){
     g.FillPolygon(brush,new[]{new PointF(25,17),new PointF(42,27),new PointF(25,37)});
    }else if(style.Kind=="audio"){
     Line(g,pen,29,32,29,19);Line(g,pen,29,19,42,16);Line(g,pen,42,16,42,29);g.FillEllipse(brush,20,29,10,7);g.FillEllipse(brush,33,26,10,7);
    }else if(style.Kind=="model"){
     g.DrawPolygon(pen,new[]{new PointF(32,15),new PointF(44,21),new PointF(44,33),new PointF(32,39),new PointF(20,33),new PointF(20,21)});
     Line(g,pen,20,21,32,27);Line(g,pen,44,21,32,27);Line(g,pen,32,27,32,39);
    }else if(style.Kind=="vector"){
     g.DrawBezier(pen,20,33,20,13,44,13,44,33);Line(g,pen,22,19,42,19);g.FillEllipse(brush,18,29,6,6);g.FillEllipse(brush,40,29,6,6);g.FillRectangle(brush,28.5f,15.5f,7,7);
    }else if(style.Kind=="table"){
     g.DrawRectangle(pen,20,17,24,20);Line(g,pen,20,24,44,24);Line(g,pen,20,31,44,31);Line(g,pen,28,17,28,37);Line(g,pen,36,17,36,37);
    }else if(style.Kind=="presentation"){
     g.DrawRectangle(pen,20,17,24,16);Line(g,pen,32,33,32,39);Line(g,pen,25,39,39,39);g.DrawLines(pen,new[]{new PointF(24,28),new PointF(30,24),new PointF(35,27),new PointF(40,21)});
    }else{
     Line(g,pen,21,20,40,20);Line(g,pen,21,27,43,27);Line(g,pen,21,34,36,34);
    }
   }
   g.TranslateTransform(0,-shift);
  }
  static string Label(string extension){return extension=="file"?"FILE":extension.ToUpperInvariant();}
  static Bitmap Draw(string extension,int size){
   var big=new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb);Style style=For(extension);
   using(var g=Graphics.FromImage(big)){
    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.CompositingQuality=CompositingQuality.HighQuality;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.ScaleTransform(big.Width/64f,big.Height/64f);
    using(var page=Page())using(var paper=new SolidBrush(ColorOf("#FBFCFE")))using(var border=new Pen(ColorOf("#B8C1D0"),size<=24?2:1.3f)){g.FillPath(paper,page);g.DrawPath(border,page);}
    using(var fold=new SolidBrush(ColorOf("#E5EAF2")))g.FillPolygon(fold,new[]{new PointF(41,4),new PointF(41,17),new PointF(54,17)});
    using(var seam=new Pen(ColorOf("#C5CDD9"),1.2f))g.DrawLines(seam,new[]{new PointF(41,5),new PointF(41,17),new PointF(53,17)});
    bool tiny=size==16;Glyph(g,style,tiny);
    using(var color=new SolidBrush(ColorOf(style.Color))){
     if(tiny){using(var line=Rounded(18,49,28,5,2.5f))g.FillPath(color,line);}
     else{
      using(var badge=Rounded(5,40,54,18,4))g.FillPath(color,badge);
      string label=Label(extension);bool twoLines=label.Length>5;float fontSize=label.Length>4?11.2f:13.8f;
      if(twoLines){fontSize=8.6f;label=label.Substring(0,4)+"\n"+label.Substring(4);}
      using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoClip})using(var white=new SolidBrush(Color.White))using(var font=new Font("Segoe UI",fontSize,FontStyle.Bold,GraphicsUnit.Pixel)){
       g.DrawString(label,font,white,new RectangleF(6,39.8f,52,18.5f),format);
      }
     }
    }
   }
   var result=new Bitmap(size,size,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(result)){g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.DrawImage(big,new Rectangle(0,0,size,size),0,0,big.Width,big.Height,GraphicsUnit.Pixel);}big.Dispose();return result;
  }
  static void WriteIcon(string path,string extension){
   var images=new List<byte[]>();foreach(int size in Sizes)using(var bitmap=Draw(extension,size))using(var stream=new MemoryStream()){bitmap.Save(stream,ImageFormat.Png);images.Add(stream.ToArray());}
   using(var output=File.Create(path))using(var writer=new BinaryWriter(output)){
    writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)Sizes.Length);uint offset=(uint)(6+16*Sizes.Length);
    for(int n=0;n<Sizes.Length;n++){writer.Write((byte)(Sizes[n]==256?0:Sizes[n]));writer.Write((byte)(Sizes[n]==256?0:Sizes[n]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write((uint)images[n].Length);writer.Write(offset);offset+=(uint)images[n].Length;}
    foreach(byte[] bytes in images)writer.Write(bytes);
   }
  }
  static string Escape(string value){return value.Replace("&","&amp;").Replace("<","&lt;").Replace("\"","&quot;");}
  static string SvgGlyph(Style style){
   string content;
   switch(style.Kind){
    case "image":content="<rect x='20' y='18' width='24' height='18'/><circle cx='37' cy='23' r='2' fill='currentColor' stroke='none'/><path d='M21 33 28 26 34 32 38 28 43 33'/>";break;
    case "video":content="<path d='M25 17 42 27 25 37Z' fill='currentColor' stroke='none'/>";break;
    case "audio":content="<path d='M29 32V19L42 16V29'/><ellipse cx='25' cy='32.5' rx='5' ry='3.5' fill='currentColor'/><ellipse cx='38' cy='29.5' rx='5' ry='3.5' fill='currentColor'/>";break;
    case "model":content="<path d='m32 15 12 6v12l-12 6-12-6V21Zm-12 6 12 6 12-6M32 27v12'/>";break;
    case "vector":content="<path d='M20 33C20 13 44 13 44 33M22 19h20'/><circle cx='21' cy='32' r='3' fill='currentColor'/><circle cx='43' cy='32' r='3' fill='currentColor'/><rect x='28.5' y='15.5' width='7' height='7' fill='currentColor'/>";break;
    case "table":content="<path d='M20 17h24v20H20ZM20 24h24M20 31h24M28 17v20M36 17v20'/>";break;
    case "presentation":content="<path d='M20 17h24v16H20ZM32 33v6M25 39h14M24 28l6-4 5 3 5-6'/>";break;
    default:content="<path d='M21 20h19M21 27h22M21 34h15'/>";break;
   }
   return "<g color='"+style.Color+"' stroke='currentColor' fill='none' stroke-width='2.8' stroke-linecap='round' stroke-linejoin='round'>"+content+"</g>";
  }
  static string Svg(string extension){
   Style style=For(extension);string label=Label(extension);
   string text=label.Length>5?"<tspan x='32' y='47'>"+Escape(label.Substring(0,4))+"</tspan><tspan x='32' y='55'>"+Escape(label.Substring(4))+"</tspan>":Escape(label);
   return "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64' width='64' height='64'><title>"+Escape(label)+" file fallback icon</title><path d='M14 4H41L54 17V56Q54 60 50 60H14Q10 60 10 56V8Q10 4 14 4Z' fill='#FBFCFE' stroke='#B8C1D0' stroke-width='1.3'/><path d='M41 4V17H54' fill='#E5EAF2' stroke='#C5CDD9' stroke-width='1.2'/>"+SvgGlyph(style)+"<rect x='5' y='40' width='54' height='18' rx='4' fill='"+style.Color+"'/><text x='32' y='54' text-anchor='middle' font-family='Segoe UI,Arial,sans-serif' font-weight='700' font-size='"+(label.Length>5?"8.6":label.Length>4?"11.2":"13.8")+"' fill='white'>"+text+"</text></svg>\n";
  }
  static string Hash(string path){using(var sha=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
  static Bitmap ReadIconPng(string path,int size){
   // PNG-backed ICO frames are supported by Explorer, while the old
   // System.Drawing.Icon.DrawIcon implementation assumes a BMP frame.
   byte[] bytes=File.ReadAllBytes(path);using(var stream=new MemoryStream(bytes))using(var reader=new BinaryReader(stream)){
    reader.ReadUInt16();reader.ReadUInt16();int count=reader.ReadUInt16();
    for(int n=0;n<count;n++){
     int width=reader.ReadByte();int height=reader.ReadByte();reader.ReadByte();reader.ReadByte();reader.ReadUInt16();reader.ReadUInt16();int length=reader.ReadInt32();int offset=reader.ReadInt32();
     if((width==0?256:width)!=size||(height==0?256:height)!=size)continue;
     using(var png=new MemoryStream(bytes,offset,length))using(var bitmap=Image.FromStream(png))return new Bitmap(bitmap);
    }
   }throw new InvalidDataException("The application icon is missing its "+size+" px PNG frame.");
  }
  static void ContactSheet(string path){
   string[] examples={"SVG","PNG","JPG","PSD","AI","PDF","MP4","WEBM","MP3","STL","STEP","DOCX","XLSX","MARKDOWN","file","app"};
   using(var image=new Bitmap(1180,700,PixelFormat.Format32bppArgb))using(var g=Graphics.FromImage(image)){
    g.Clear(ColorOf("#EEF1F6"));g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAlias;
    using(var title=new Font("Segoe UI",23,FontStyle.Bold))using(var note=new Font("Segoe UI",12))using(var dark=new SolidBrush(ColorOf("#253247"))){
     g.DrawString("Nook file icons — thumbnails remain first",title,dark,28,23);g.DrawString("Fallbacks at native 16, 24, 32, 48 and 64 px. Application icon is unchanged.",note,dark,30,68);
     for(int index=0;index<examples.Length;index++){
      int x=28+(index%4)*288,y=112+(index/4)*142;
      using(var panel=new SolidBrush(Color.White))using(var rect=Rounded(x,y,270,126,8))g.FillPath(panel,rect);
      g.DrawString(Label(examples[index]),note,dark,x+12,y+11);int[] sizes={16,24,32,48,64};int cursor=x+13;
      foreach(int size in sizes){if(examples[index]=="app"){using(var icon=ReadIconPng(Path.Combine(Path.GetDirectoryName(path),"app.ico"),size))g.DrawImageUnscaled(icon,cursor,y+43+(64-size)/2);}else using(var bitmap=Draw(examples[index],size))g.DrawImageUnscaled(bitmap,cursor,y+43+(64-size)/2);cursor+=size+10;}
     }
    }
    image.Save(path,ImageFormat.Png);
   }
  }
  public static void Generate(string directory,string applicationIcon){
   Directory.CreateDirectory(directory);string sources=Path.Combine(directory,"svg");Directory.CreateDirectory(sources);
   var names=FileFormats.PreviewExtensions().Select(ext=>ext.Substring(1).ToUpperInvariant()).Distinct(StringComparer.Ordinal).Concat(new[]{"file"}).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
   var utf8=new UTF8Encoding(false);
   foreach(string name in names){WriteIcon(Path.Combine(directory,name+".ico"),name);File.WriteAllText(Path.Combine(sources,name+".svg"),Svg(name),utf8);}
   File.Copy(applicationIcon,Path.Combine(directory,"app.ico"),true);
   ContactSheet(Path.Combine(directory,"preview-contact-sheet.png"));
   var records=names.Concat(new[]{"app"}).OrderBy(x=>x,StringComparer.Ordinal).Select(name=>"    {\"file\":\""+name+".ico\",\"sha256\":\""+Hash(Path.Combine(directory,name+".ico"))+"\",\"bytes\":"+new FileInfo(Path.Combine(directory,name+".ico")).Length.ToString(CultureInfo.InvariantCulture)+"}");
   string manifest="{\n  \"schemaVersion\": 1,\n  \"purpose\": \"File fallback artwork; native thumbnails have priority\",\n  \"generator\": \"tools/Build-FileIcons.ps1\",\n  \"sizes\": [16,24,32,48,64,128,256],\n  \"files\": [\n"+string.Join(",\n",records)+"\n  ]\n}\n";
   File.WriteAllText(Path.Combine(directory,"icons-manifest.json"),manifest,utf8);
   File.WriteAllText(Path.Combine(directory,"README.txt"),"File fallback icons for Nook\r\n\r\nReal Windows thumbnails remain the primary presentation. These icons are shown only when a thumbnail is unavailable.\r\nThe application brand remains app.ico (an unchanged copy of assets/nook.ico).\r\nEvery advertised preview extension has an uppercase-named ICO. Each file contains 16, 24, 32, 48, 64, 128 and 256 px PNG-backed 32-bit RGBA entries.\r\n16 px uses a family glyph and color bar instead of unreadable text. 24 px and above show the exact extension; MARKDOWN uses two lines.\r\nRebuild: powershell -NoProfile -File tools\\Build-FileIcons.ps1\r\nEditable SVG designs are in svg\\. The generator is authoritative for size-specific raster artwork.\r\nArtwork is original to this project, distributed under the repository license. No external fonts or third-party icons are bundled. Segoe UI is used from Windows during generation.\r\n",utf8);
  }
 }
}
