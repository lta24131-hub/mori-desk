using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Mori {
 // This is a read-only CONTENT preview, never an Office/HTML renderer. All
 // archive parts remain in memory; links, formulas, scripts and macros are not
 // executed, fetched or extracted onto disk.
 internal sealed class DocumentContentResult {
  internal string SourcePath {get;set;}
  internal string Kind {get;set;}
  internal string Text {get;set;}
  internal bool Truncated {get;set;}
  internal string Limitation {get;set;}
  internal string EncodingName {get;set;}
 }

 internal static class DocumentContent {
  internal const int MaxOutputCharacters=160000;
  internal const int MaxTextReadBytes=1024*1024;
  internal const long MaxTextFileBytes=8L*1024*1024;
  internal const long MaxPackageFileBytes=64L*1024*1024;
  internal const long MaxXmlPartBytes=4L*1024*1024;
  internal const long MaxXmlTotalBytes=16L*1024*1024;
  internal const int MaxPackageEntries=2048;
  internal const int MaxPages=64;
  internal const int MaxSharedStrings=20000;
  static readonly HashSet<string> TextExtensions=new HashSet<string>(MoriDesk.FileFormats.PlainDocuments.Split(' '),StringComparer.OrdinalIgnoreCase);
  static readonly HashSet<string> PackageExtensions=new HashSet<string>(MoriDesk.FileFormats.PackageDocuments.Split(' '),StringComparer.OrdinalIgnoreCase);
  static readonly SemaphoreSlim ReadSlots=new SemaphoreSlim(2,2);
  static readonly Regex SlideName=new Regex(@"^ppt/slides/slide([1-9][0-9]*)\.xml$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
  internal static bool Supports(string pathOrExtension){
   if(string.IsNullOrWhiteSpace(pathOrExtension))return false;
   string extension=pathOrExtension.StartsWith(".",StringComparison.Ordinal)&&pathOrExtension.IndexOfAny(new[]{'\\','/'})<0?pathOrExtension:Path.GetExtension(pathOrExtension);
   return TextExtensions.Contains(extension)||PackageExtensions.Contains(extension);
  }
  internal static async Task<DocumentContentResult> OpenAsync(string path,CancellationToken cancellation){
   await ReadSlots.WaitAsync(cancellation).ConfigureAwait(false);
   try{return await Task.Run(()=>Read(path,cancellation),cancellation).ConfigureAwait(false);}
   finally{ReadSlots.Release();}
  }
  internal static DocumentContentResult Read(string path){return Read(path,CancellationToken.None);}
  internal static DocumentContentResult Read(string path,CancellationToken cancellation){
   cancellation.ThrowIfCancellationRequested();
   if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("文件地址为空。","path");
   string extension=Path.GetExtension(path).ToLowerInvariant();
   if(!Supports(extension))throw new NotSupportedException("此格式尚无内置内容预览，请使用默认程序打开。");
   var result=new DocumentContentResult{SourcePath=Path.GetFullPath(path),Kind=extension.TrimStart('.').ToUpperInvariant(),Text="",EncodingName=""};
   using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)){
    if(TextExtensions.Contains(extension)){
     if(file.Length>MaxTextFileBytes)throw new NotSupportedException("文本文件较大，请使用默认程序打开（内置预览上限 8 MB）。");
     ReadText(file,result,cancellation);
     result.Limitation="纯文本内容预览；Markdown、代码和表格不执行或渲染，原文件不变。";
    }else{
     if(file.Length>MaxPackageFileBytes)throw new NotSupportedException("文档较大，请使用默认程序打开（内置内容预览上限 64 MB）。");
     try{
      using(var package=new Package(file,cancellation)){
       var sink=new TextSink();
       if(extension==".docx")ReadWord(package,sink);
       else if(extension==".xlsx")ReadExcel(package,sink);
       else if(extension==".pptx")ReadSlides(package,sink);
       else ReadOdf(package,sink,extension);
       cancellation.ThrowIfCancellationRequested();result.Text=sink.Value.TrimEnd();result.Truncated=sink.Truncated;
      }
     }catch(OperationCanceledException){throw;}
     catch(XmlException ex){throw new InvalidDataException("文档内容无法安全读取：XML 无效或包含不允许的实体。",ex);}
     catch(InvalidDataException){throw;}
     catch(NotSupportedException){throw;}
     catch(ArgumentException ex){throw new InvalidDataException("文档内容损坏或不是此格式的文件。",ex);}
     result.Limitation=extension==".xlsx"||extension==".ods"?"只预览单元格内容及已保存的计算结果；不保留版式，不重新计算公式，不执行宏或联网。":"只预览可读文字；不保留版式、图片或动画，不执行宏或联网。";
    }
   }
   if(result.Truncated)result.Limitation+=" 内容超出预览限制，已显示前面部分。";
   return result;
  }
  static void ReadText(FileStream file,DocumentContentResult result,CancellationToken cancellation){
   int count=(int)Math.Min(file.Length,MaxTextReadBytes);var bytes=new byte[count];int read=0;
   while(read<count){cancellation.ThrowIfCancellationRequested();int next=file.Read(bytes,read,Math.Min(8192,count-read));if(next==0)break;read+=next;}
   bool clipped=file.Length>read;
   int skip;Encoding encoding=DetectEncoding(bytes,read,out skip);
   string content;
   try{content=DecodePrefix(encoding,bytes,skip,read-skip,clipped);}
   catch(DecoderFallbackException){
    if(skip!=0||encoding.CodePage!=Encoding.UTF8.CodePage)throw new InvalidDataException("文本编码无效，无法安全预览。");
    encoding=Encoding.GetEncoding(Encoding.Default.CodePage,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
    try{content=DecodePrefix(encoding,bytes,0,read,clipped);}catch(DecoderFallbackException ex){throw new InvalidDataException("无法识别文本编码。",ex);}
   }
   cancellation.ThrowIfCancellationRequested();
   int controls=0,sample=Math.Min(content.Length,8192);
   for(int i=0;i<sample;i++){char c=content[i];if(c=='\0')throw new InvalidDataException("这个文件似乎是二进制内容，不能作为文本预览。");if(char.IsControl(c)&&c!='\r'&&c!='\n'&&c!='\t'&&c!='\f')controls++;}
   if(sample>0&&controls>Math.Max(2,sample/50))throw new InvalidDataException("这个文件似乎是二进制内容，不能作为文本预览。");
   var sink=new TextSink();sink.Add(content);result.Text=sink.Value;result.Truncated=clipped||sink.Truncated;result.EncodingName=encoding.WebName;
  }
  static string DecodePrefix(Encoding encoding,byte[] bytes,int offset,int count,bool clipped){
   if(!clipped)return encoding.GetString(bytes,offset,count);
   // A prefix can end between UTF-8/UTF-16 or legacy multibyte characters.
   // Do not flush that trailing decoder state or misidentify valid UTF-8 as ANSI.
   var decoder=encoding.GetDecoder();var chars=new char[encoding.GetMaxCharCount(count)];int used=decoder.GetChars(bytes,offset,count,chars,0,false);return new string(chars,0,used);
  }
  static Encoding DetectEncoding(byte[] bytes,int count,out int skip){
   skip=0;
   if(count>=4&&bytes[0]==0xff&&bytes[1]==0xfe&&bytes[2]==0&&bytes[3]==0){skip=4;return new UTF32Encoding(false,true,true);}
   if(count>=4&&bytes[0]==0&&bytes[1]==0&&bytes[2]==0xfe&&bytes[3]==0xff){skip=4;return new UTF32Encoding(true,true,true);}
   if(count>=3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf){skip=3;return new UTF8Encoding(false,true);}
   if(count>=2&&bytes[0]==0xff&&bytes[1]==0xfe){skip=2;return new UnicodeEncoding(false,true,true);}
   if(count>=2&&bytes[0]==0xfe&&bytes[1]==0xff){skip=2;return new UnicodeEncoding(true,true,true);}
   int evenZero=0,oddZero=0,pairs=Math.Min(count/2,512);
   for(int i=0;i<pairs;i++){if(bytes[i*2]==0)evenZero++;if(bytes[i*2+1]==0)oddZero++;}
   if(pairs>=8&&oddZero>pairs*3/4&&evenZero<pairs/8)return new UnicodeEncoding(false,false,true);
   if(pairs>=8&&evenZero>pairs*3/4&&oddZero<pairs/8)return new UnicodeEncoding(true,false,true);
   return new UTF8Encoding(false,true);
  }
  sealed class TextSink {
   readonly StringBuilder value=new StringBuilder();
   internal bool Truncated;
   internal bool Full {get{return value.Length>=MaxOutputCharacters;}}
   internal string Value {get{return value.ToString();}}
   internal void Add(string text){
    if(string.IsNullOrEmpty(text))return;int available=MaxOutputCharacters-value.Length;
    if(text.Length>available){Truncated=true;int take=available;if(take>0&&char.IsHighSurrogate(text[take-1]))take--;if(take>0)value.Append(text,0,take);return;}
    value.Append(text);
   }
   internal void NewLine(){if(value.Length>0&&value[value.Length-1]!='\n')Add("\n");}
  }
  sealed class Package : IDisposable {
   readonly ZipArchive archive;readonly Dictionary<string,ZipArchiveEntry> entries;readonly CancellationToken cancellation;long total;
   internal Package(Stream stream,CancellationToken token){
    cancellation=token;
    try{
     ValidateDirectory(stream,token);
     archive=new ZipArchive(stream,ZipArchiveMode.Read,true);
     if(archive.Entries.Count>MaxPackageEntries)throw new NotSupportedException("文档包含太多项目，请使用默认程序打开。");
     entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
     foreach(var entry in archive.Entries){cancellation.ThrowIfCancellationRequested();if(entries.ContainsKey(entry.FullName))throw new InvalidDataException("文档包包含重复的内容项目。");entries.Add(entry.FullName,entry);}
    }catch{if(archive!=null)archive.Dispose();throw;}
   }
   internal IEnumerable<string> Names {get{return entries.Keys;}}
   internal bool Contains(string name){return entries.ContainsKey(name);}
   internal XmlReader Open(string name){
    cancellation.ThrowIfCancellationRequested();ZipArchiveEntry entry;
    if(!entries.TryGetValue(name,out entry))throw new InvalidDataException("文档缺少必需的内容项目："+name);
    if(entry.Length>MaxXmlPartBytes)throw new NotSupportedException("文档内容项目过大，请使用默认程序打开。");
    if(entry.Length>512*1024&&entry.CompressedLength>0&&entry.Length/entry.CompressedLength>200)throw new InvalidDataException("文档压缩率异常，已停止预览以保护内存。");
    if(total+entry.Length>MaxXmlTotalBytes)throw new NotSupportedException("文档内容超出轻量预览限制，请使用默认程序打开。");
    total+=entry.Length;
    var bounded=new BoundedReadStream(entry.Open(),entry.Length,MaxXmlPartBytes,cancellation);
    try{return XmlReader.Create(bounded,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxXmlPartBytes,MaxCharactersFromEntities=1024,CloseInput=true,IgnoreComments=true,IgnoreProcessingInstructions=true});}
    catch{bounded.Dispose();throw;}
   }
   internal void Check(){cancellation.ThrowIfCancellationRequested();}
   public void Dispose(){archive.Dispose();}
  }
  // ZipArchive otherwise materializes the whole central directory before its
  // Entries.Count can be checked. Preflight that directory with a 64 KB tail
  // buffer and fixed-size headers, including the real (not merely advertised)
  // number of entries. This also bounds filename/extra/comment metadata memory.
  static void ValidateDirectory(Stream stream,CancellationToken cancellation){
   long saved=stream.Position;
   try{
    if(!stream.CanSeek||stream.Length<22)throw new InvalidDataException("文档包不是有效的 ZIP 文件。");
    int tailLength=(int)Math.Min(stream.Length,65557);var tail=new byte[tailLength];stream.Position=stream.Length-tailLength;ReadExactly(stream,tail,tailLength,cancellation);
    int end=-1;
    for(int i=tail.Length-22;i>=0;i--){if(U32(tail,i)==0x06054b50&&i+22+U16(tail,i+20)==tail.Length){end=i;break;}}
    if(end<0)throw new InvalidDataException("文档包缺少有效的 ZIP 目录。");
    uint directoryBytes=U32(tail,end+12),directoryOffset=U32(tail,end+16);int advertised=U16(tail,end+10);
    if(U16(tail,end+4)!=0||U16(tail,end+6)!=0||U16(tail,end+8)!=advertised)throw new NotSupportedException("分卷文档包不能在内置预览中打开。");
    if(advertised>MaxPackageEntries||directoryBytes==uint.MaxValue||directoryOffset==uint.MaxValue)throw new NotSupportedException("文档包含太多项目或使用超大 ZIP 格式，请使用默认程序打开。");
    if(directoryBytes>4*1024*1024)throw new NotSupportedException("文档目录信息过大，请使用默认程序打开。");
    long directoryEnd=(long)directoryOffset+directoryBytes,recordPosition=stream.Length-tailLength+end;
    if(directoryEnd>recordPosition||directoryEnd>stream.Length)throw new InvalidDataException("文档包目录范围无效。");
    stream.Position=directoryOffset;var header=new byte[46];int actual=0;
    while(stream.Position<directoryEnd){
     cancellation.ThrowIfCancellationRequested();
     if(directoryEnd-stream.Position<46)throw new InvalidDataException("文档包目录不完整。");
     ReadExactly(stream,header,46,cancellation);
     if(U32(header,0)!=0x02014b50)throw new InvalidDataException("文档包目录项目无效。");
     if(++actual>MaxPackageEntries)throw new NotSupportedException("文档包含太多项目，请使用默认程序打开。");
     int metadata=U16(header,28)+U16(header,30)+U16(header,32);
     if(metadata>directoryEnd-stream.Position)throw new InvalidDataException("文档包目录项目超出边界。");
     stream.Position+=metadata;
    }
    if(actual!=advertised)throw new InvalidDataException("文档包目录项目数量不一致。");
   }finally{stream.Position=saved;}
  }
  static int U16(byte[] bytes,int offset){return bytes[offset]|(bytes[offset+1]<<8);}
  static uint U32(byte[] bytes,int offset){return (uint)(bytes[offset]|(bytes[offset+1]<<8)|(bytes[offset+2]<<16)|(bytes[offset+3]<<24));}
  static void ReadExactly(Stream stream,byte[] bytes,int count,CancellationToken cancellation){int read=0;while(read<count){cancellation.ThrowIfCancellationRequested();int next=stream.Read(bytes,read,count-read);if(next==0)throw new InvalidDataException("文档包读取不完整。");read+=next;}}
  sealed class BoundedReadStream : Stream {
   readonly Stream source;readonly long expected,limit;readonly CancellationToken cancellation;long count;
   internal BoundedReadStream(Stream source,long expected,long limit,CancellationToken cancellation){this.source=source;this.expected=expected;this.limit=limit;this.cancellation=cancellation;}
   public override int Read(byte[] buffer,int offset,int length){cancellation.ThrowIfCancellationRequested();int read=source.Read(buffer,offset,(int)Math.Min(length,Math.Max(1,limit-count+1)));count+=read;if(count>limit||count>expected)throw new InvalidDataException("文档解压内容超过安全限制。");return read;}
   public override bool CanRead {get{return true;}}public override bool CanSeek {get{return false;}}public override bool CanWrite {get{return false;}}
   public override long Length {get{return expected;}}public override long Position {get{return count;}set{throw new NotSupportedException();}}
   public override void Flush(){}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int length){throw new NotSupportedException();}
   protected override void Dispose(bool disposing){if(disposing)source.Dispose();base.Dispose(disposing);}
  }
  static bool IsText(XmlReader reader){return reader.NodeType==XmlNodeType.Text||reader.NodeType==XmlNodeType.CDATA||reader.NodeType==XmlNodeType.SignificantWhitespace||reader.NodeType==XmlNodeType.Whitespace;}
  static void ReadWord(Package package,TextSink sink){
   using(var reader=package.Open("word/document.xml"))ReadParagraphs(package,reader,sink);
  }
  static void ReadParagraphs(Package package,XmlReader reader,TextSink sink){
   int textDepth=-1;
   while(reader.Read()){
    package.Check();
    if(sink.Full){sink.Truncated=true;break;}
    if(reader.NodeType==XmlNodeType.Element){if(reader.LocalName=="t"&&!reader.IsEmptyElement)textDepth=reader.Depth;else if(reader.LocalName=="tab")sink.Add("\t");else if(reader.LocalName=="br"||reader.LocalName=="cr")sink.NewLine();}
    else if(IsText(reader)&&textDepth>=0)sink.Add(reader.Value);
    else if(reader.NodeType==XmlNodeType.EndElement){if(reader.Depth==textDepth)textDepth=-1;if(reader.LocalName=="p")sink.NewLine();}
   }
  }
  sealed class Sheet {internal string Name;internal string Path;}
  static string PackageTarget(string baseName,string target){
   if(string.IsNullOrEmpty(target)||target.Length>1024||target.IndexOfAny(new[]{':','?','#','\0'})>=0)return null;
   target=target.Replace('\\','/');string source=target.StartsWith("/",StringComparison.Ordinal)?target.TrimStart('/'):baseName.Substring(0,baseName.LastIndexOf('/')+1)+target;
   var parts=new List<string>();foreach(string part in source.Split('/')){if(part==""||part==".")continue;if(part==".."){if(parts.Count==0)return null;parts.RemoveAt(parts.Count-1);}else parts.Add(part);}
   return string.Join("/",parts);
  }
  static void ReadExcel(Package package,TextSink sink){
   var relations=new Dictionary<string,string>(StringComparer.Ordinal);
   using(var reader=package.Open("xl/_rels/workbook.xml.rels")){
    while(reader.Read()){package.Check();if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="Relationship"&&!string.Equals(reader.GetAttribute("TargetMode"),"External",StringComparison.OrdinalIgnoreCase)){
     string id=reader.GetAttribute("Id"),path=PackageTarget("xl/workbook.xml",reader.GetAttribute("Target"));
     if(!string.IsNullOrEmpty(id)&&id.Length<=128&&path!=null&&path.StartsWith("xl/worksheets/",StringComparison.OrdinalIgnoreCase)){if(relations.Count>=MaxPages*4)throw new NotSupportedException("文档包含太多工作表关系。");relations[id]=path;}
    }}
   }
   var sheets=new List<Sheet>();
   using(var reader=package.Open("xl/workbook.xml")){
    while(reader.Read()){package.Check();if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="sheet"){
     if(sheets.Count>=MaxPages){sink.Truncated=true;break;}
     string name=reader.GetAttribute("name")??"工作表",id=reader.GetAttribute("id","http://schemas.openxmlformats.org/officeDocument/2006/relationships"),path;
     if(id!=null&&relations.TryGetValue(id,out path))sheets.Add(new Sheet{Name=name.Length>160?name.Substring(0,160):name,Path=path});
    }}
   }
   if(sheets.Count==0)throw new InvalidDataException("文档中没有可读取的工作表。");
   var strings=ReadSharedStrings(package,sink);
   foreach(var sheet in sheets){package.Check();if(sink.Full){sink.Truncated=true;break;}sink.NewLine();sink.Add("【"+sheet.Name+"】\n");ReadSheet(package,sheet.Path,strings,sink);sink.NewLine();}
  }
  static List<string> ReadSharedStrings(Package package,TextSink sink){
   var strings=new List<string>();if(!package.Contains("xl/sharedStrings.xml"))return strings;
   using(var reader=package.Open("xl/sharedStrings.xml")){
    StringBuilder current=null;int textDepth=-1,totalCharacters=0;bool clipped=false;
    while(reader.Read()){
     package.Check();
     if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="si")current=new StringBuilder();
     else if(reader.NodeType==XmlNodeType.Element&&reader.LocalName=="t"&&!reader.IsEmptyElement)textDepth=reader.Depth;
     else if(IsText(reader)&&textDepth>=0&&current!=null){int available=MaxOutputCharacters*4-totalCharacters-current.Length;if(available<=0){clipped=true;continue;}int take=Math.Min(reader.Value.Length,Math.Min(available,MaxOutputCharacters-current.Length));if(take>0)current.Append(reader.Value,0,take);if(take<reader.Value.Length)clipped=true;}
     else if(reader.NodeType==XmlNodeType.EndElement){
      if(reader.Depth==textDepth)textDepth=-1;
      if(reader.LocalName=="si"&&current!=null){if(strings.Count>=MaxSharedStrings){sink.Truncated=true;break;}string value=current.ToString();strings.Add(value);totalCharacters+=value.Length;current=null;if(clipped){sink.Truncated=true;break;}}
     }
    }
   }
   return strings;
  }
  static void ReadSheet(Package package,string path,List<string> strings,TextSink sink){
   using(var reader=package.Open(path)){
    string coordinate=null,type=null;StringBuilder cell=null;int valueDepth=-1;int cells=0;
    while(reader.Read()){
     package.Check();if(sink.Full||cells>=20000){sink.Truncated=true;break;}
     if(reader.NodeType==XmlNodeType.Element){
      if(reader.LocalName=="c"){coordinate=reader.GetAttribute("r")??"?";if(coordinate.Length>32)throw new InvalidDataException("单元格地址无效。");type=reader.GetAttribute("t")??"";cell=new StringBuilder();}
      else if(cell!=null&&(reader.LocalName=="v"||reader.LocalName=="t")&&!reader.IsEmptyElement)valueDepth=reader.Depth;
     }else if(IsText(reader)&&valueDepth>=0&&cell!=null){int take=Math.Min(reader.Value.Length,MaxOutputCharacters-cell.Length);if(take>0)cell.Append(reader.Value,0,take);if(take<reader.Value.Length)sink.Truncated=true;}
     else if(reader.NodeType==XmlNodeType.EndElement){
      if(reader.Depth==valueDepth)valueDepth=-1;
      if(reader.LocalName=="c"&&cell!=null){
       string value=cell.ToString();if(type=="s"){int index;if(int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out index)&&index>=0&&index<strings.Count)value=strings[index];else value="[文字索引超出预览范围]";}
       else if(type=="b")value=value=="1"?"TRUE":value=="0"?"FALSE":value;
       if(value.Length>0){sink.Add(coordinate+": "+value+"\t");cells++;}cell=null;coordinate=null;type=null;
      }
      else if(reader.LocalName=="row")sink.NewLine();
     }
    }
   }
  }
  static void ReadSlides(Package package,TextSink sink){
   var slides=new List<KeyValuePair<long,string>>();
   foreach(string name in package.Names){package.Check();var match=SlideName.Match(name);long number;if(match.Success&&long.TryParse(match.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out number))slides.Add(new KeyValuePair<long,string>(number,name));}
   if(slides.Count==0)throw new InvalidDataException("文档中没有可读取的幻灯片。");
   int count=0;
   foreach(var slide in slides.OrderBy(x=>x.Key)){
    package.Check();if(count>=MaxPages||sink.Full){sink.Truncated=true;break;}count++;sink.NewLine();sink.Add("【第 "+slide.Key.ToString(CultureInfo.InvariantCulture)+" 页】\n");using(var reader=package.Open(slide.Value))ReadParagraphs(package,reader,sink);sink.NewLine();
   }
  }
  static void ReadOdf(Package package,TextSink sink,string extension){
   const string Office="urn:oasis:names:tc:opendocument:xmlns:office:1.0",Text="urn:oasis:names:tc:opendocument:xmlns:text:1.0",Table="urn:oasis:names:tc:opendocument:xmlns:table:1.0",Draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
   using(var reader=package.Open("content.xml")){
    int bodyDepth=-1,paragraphDepth=-1,pages=0;bool sawBody=false;
    while(reader.Read()){
     package.Check();if(sink.Full){sink.Truncated=true;break;}
     if(reader.NodeType==XmlNodeType.Element){
      if(reader.NamespaceURI==Office&&reader.LocalName=="body"){sawBody=true;if(!reader.IsEmptyElement)bodyDepth=reader.Depth;}
      else if(bodyDepth>=0){
       if(reader.NamespaceURI==Text&&(reader.LocalName=="p"||reader.LocalName=="h")&&!reader.IsEmptyElement)paragraphDepth=reader.Depth;
       else if(reader.NamespaceURI==Text&&reader.LocalName=="s"){int spaces;string requested=reader.GetAttribute("c",Text);if(!int.TryParse(requested,out spaces))spaces=1;sink.Add(new string(' ',Math.Max(1,Math.Min(spaces,256))));}
       else if(reader.NamespaceURI==Text&&reader.LocalName=="tab")sink.Add("\t");
       else if(reader.NamespaceURI==Text&&reader.LocalName=="line-break")sink.NewLine();
       else if(reader.NamespaceURI==Table&&reader.LocalName=="table"){sink.NewLine();string name=reader.GetAttribute("name",Table);if(!string.IsNullOrWhiteSpace(name))sink.Add("【"+(name.Length>160?name.Substring(0,160):name)+"】\n");}
       else if(reader.NamespaceURI==Draw&&reader.LocalName=="page"){if(++pages>MaxPages){sink.Truncated=true;break;}sink.NewLine();sink.Add("【第 "+pages.ToString(CultureInfo.InvariantCulture)+" 页】\n");}
      }
     }else if(IsText(reader)&&paragraphDepth>=0)sink.Add(reader.Value);
     else if(reader.NodeType==XmlNodeType.EndElement){if(reader.Depth==paragraphDepth){paragraphDepth=-1;sink.NewLine();}if(reader.NamespaceURI==Table&&reader.LocalName=="table-cell")sink.Add("\t");if(reader.NamespaceURI==Table&&reader.LocalName=="table-row")sink.NewLine();if(reader.Depth==bodyDepth)bodyDepth=-1;}
    }
    if(!sawBody)throw new InvalidDataException("文档缺少可读取的正文。");
   }
  }
 }
}
