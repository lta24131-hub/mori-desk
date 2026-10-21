using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Run uses generated originals and injected replies without a worker.
 // RunReal starts only the contained conversion worker, on synthetic files.
 // Neither entry creates an application/window or uses pickers/the clipboard.
 internal static class QuickConversionTests {
  delegate Task<ConversionBatchResult> ConvertFiles(string[] inputs,string target,string output,int quality,IProgress<ConversionProgress> progress,CancellationToken token);
  static Action<string> realTrace;
  static int realWaitSeconds=20;
  static void Drain(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static T Await<T>(Task<T> task){DateTime until=DateTime.UtcNow.AddSeconds(20);while(!task.IsCompleted){if(DateTime.UtcNow>until)throw new TimeoutException("Quick conversion background test timed out.");Drain();Thread.Sleep(1);}var result=task.GetAwaiter().GetResult();Drain();return result;}
  static void Await(Task task){DateTime until=DateTime.UtcNow.AddSeconds(20);while(!task.IsCompleted){if(DateTime.UtcNow>until)throw new TimeoutException("Quick conversion panel background test timed out.");Drain();Thread.Sleep(1);}task.GetAwaiter().GetResult();Drain();}
  static void Until(Func<bool> condition){DateTime until=DateTime.UtcNow.AddSeconds(20);while(!condition()){if(DateTime.UtcNow>until)throw new TimeoutException("Quick conversion injected callback did not start.");Drain();Thread.Sleep(1);}Drain();}
  static string Picture(string root,string name,int marker){
   string path=Path.Combine(root,name);Directory.CreateDirectory(Path.GetDirectoryName(path));
   using(var bitmap=new System.Drawing.Bitmap(48,32)){
    using(var drawing=System.Drawing.Graphics.FromImage(bitmap)){
     drawing.Clear(System.Drawing.Color.FromArgb(255,marker,marker*3%256,marker*7%256));
     using(var brush=new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255,marker*11%256,marker*13%256,marker*17%256)))drawing.FillRectangle(brush,9,7,23,17);
    }
    bitmap.Save(path,string.Equals(Path.GetExtension(path),".jpg",StringComparison.OrdinalIgnoreCase)||string.Equals(Path.GetExtension(path),".jpeg",StringComparison.OrdinalIgnoreCase)?System.Drawing.Imaging.ImageFormat.Jpeg:System.Drawing.Imaging.ImageFormat.Png);
   }
   return path;
  }
  static string Hash(string path){using(var stream=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream));}
  static bool SameDecodedPixels(System.Drawing.Bitmap first,System.Drawing.Bitmap second){
   if(first.Width!=second.Width||first.Height!=second.Height)return false;
   for(int y=0;y<first.Height;y++)for(int x=0;x<first.Width;x++)if(first.GetPixel(x,y).ToArgb()!=second.GetPixel(x,y).ToArgb())return false;
   return true;
  }
  static string TwoPagePdf(string root,string name,bool identical){
   Directory.CreateDirectory(root);string path=Path.Combine(root,name);
   string first="1 0 0 rg\n0 0 144 96 re f\n",second=identical?first:"0 0 1 rg\n0 0 144 96 re f\n";
   string[] objects={
    "<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 96] /Contents 5 0 R /Resources << >> >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 96] /Contents 6 0 R /Resources << >> >>",
    "<< /Length "+first.Length+" >>\nstream\n"+first+"endstream",
    "<< /Length "+second.Length+" >>\nstream\n"+second+"endstream"};
   var text=new StringBuilder("%PDF-1.4\n");var offsets=new int[objects.Length];
   for(int index=0;index<objects.Length;index++){offsets[index]=text.Length;text.Append((index+1)+" 0 obj\n"+objects[index]+"\nendobj\n");}
   int xref=text.Length;text.Append("xref\n0 "+(objects.Length+1)+"\n0000000000 65535 f \n");
   foreach(int offset in offsets)text.Append(offset.ToString("D10")+" 00000 n \n");
   text.Append("trailer\n<< /Size "+(objects.Length+1)+" /Root 1 0 R >>\nstartxref\n"+xref+"\n%%EOF\n");
   File.WriteAllText(path,text.ToString(),Encoding.ASCII);return path;
  }
  static string TwoFrameImage(string root,string name){
   Directory.CreateDirectory(root);string path=Path.Combine(root,name);
   using(var frames=new ImageMagick.MagickImageCollection()){
    AddMagickFrame(frames,ImageMagick.MagickColors.Red,12);
    AddMagickFrame(frames,ImageMagick.MagickColors.Blue,24);frames.Write(path);
   }
   return path;
  }
  static string LayeredPsd(string root){
   Directory.CreateDirectory(root);string path=Path.Combine(root,"layered-composite.psd");
   using(var images=new ImageMagick.MagickImageCollection()){
    AddMagickFrame(images,ImageMagick.MagickColors.Lime,0);
    AddMagickFrame(images,ImageMagick.MagickColors.Red,0);
    AddMagickFrame(images,ImageMagick.MagickColors.Blue,0);
    images.Write(path);
   }
   return path;
  }
  static uint Big32(byte[] bytes,int offset){return (uint)bytes[offset]<<24|(uint)bytes[offset+1]<<16|(uint)bytes[offset+2]<<8|bytes[offset+3];}
  static byte[] Big32(uint value){return new[]{(byte)(value>>24),(byte)(value>>16),(byte)(value>>8),(byte)value};}
  static byte[] SolidPng(System.Drawing.Color colour){
   using(var image=new System.Drawing.Bitmap(48,32))using(var graphics=System.Drawing.Graphics.FromImage(image))using(var output=new MemoryStream()){
    graphics.Clear(colour);image.Save(output,System.Drawing.Imaging.ImageFormat.Png);return output.ToArray();
   }
  }
  static byte[][] PngParts(byte[] png,string type){
   var result=new List<byte[]>();for(int offset=8;offset<png.Length;){
    int count=(int)Big32(png,offset);if(Encoding.ASCII.GetString(png,offset+4,4)==type){var data=new byte[count];Buffer.BlockCopy(png,offset+8,data,0,count);result.Add(data);}offset+=count+12;
   }return result.ToArray();
  }
  static void PngChunk(Stream output,string type,byte[] data){
   byte[] name=Encoding.ASCII.GetBytes(type),length=Big32((uint)data.Length);output.Write(length,0,4);output.Write(name,0,4);output.Write(data,0,data.Length);
   uint crc=0xffffffff;foreach(byte value in name.Concat(data)){crc^=value;for(int bit=0;bit<8;bit++)crc=(crc&1)!=0?0xedb88320^(crc>>1):crc>>1;}byte[] checksum=Big32(crc^0xffffffff);output.Write(checksum,0,4);
  }
  static string TwoFrameApng(string root){
   Directory.CreateDirectory(root);string path=Path.Combine(root,"animated-with-png-suffix.png");
   byte[] red=SolidPng(System.Drawing.Color.Red),blue=SolidPng(System.Drawing.Color.Blue);
   using(var output=File.Create(path)){
    output.Write(red,0,8);PngChunk(output,"IHDR",PngParts(red,"IHDR").Single());PngChunk(output,"acTL",Big32(2).Concat(Big32(0)).ToArray());
    uint sequence=0;
    foreach(var frame in new[]{red,blue}){
     var control=Big32(sequence++).Concat(Big32(48)).Concat(Big32(32)).Concat(Big32(0)).Concat(Big32(0)).Concat(new byte[]{0,12,0,100,0,0}).ToArray();
     PngChunk(output,"fcTL",control);
     foreach(var data in PngParts(frame,"IDAT"))if(ReferenceEquals(frame,red))PngChunk(output,"IDAT",data);else PngChunk(output,"fdAT",Big32(sequence++).Concat(data).ToArray());
    }PngChunk(output,"IEND",new byte[0]);
   }return path;
  }
  static void AddMagickFrame(ImageMagick.MagickImageCollection frames,object colour,uint delay){
   // Framework's legacy C# compiler cannot consume selected newer generic
   // Magick signatures directly; reflection uses the same packaged codec.
   object image=Activator.CreateInstance(typeof(ImageMagick.MagickImage),new object[]{colour,(uint)48,(uint)32});
   image.GetType().GetProperty("AnimationDelay").SetValue(image,delay,null);
   frames.GetType().GetMethods().Single(method=>method.Name=="Add"&&method.GetParameters().Length==1&&method.GetParameters()[0].ParameterType.IsInstanceOfType(image)).Invoke(frames,new[]{image});
  }
  static uint MagickDelay(ImageMagick.MagickImageCollection frames,int index){
   object image=frames.GetType().GetProperty("Item").GetValue(frames,new object[]{index});
   return Convert.ToUInt32(image.GetType().GetProperty("AnimationDelay").GetValue(image,null));
  }
  static ConvertedFile Success(string input,params string[] outputs){return new ConvertedFile{Source=input,Outputs=outputs,Error=""};}
  static ConvertedFile Failure(string input,string message){return new ConvertedFile{Source=input,Outputs=new string[0],Error=message};}
  static ConversionBatchResult Batch(params ConvertedFile[] files){return new ConversionBatchResult{Files=files.ToList()};}
  static Task<string> Extension(string path,CancellationToken token){token.ThrowIfCancellationRequested();string extension=Path.GetExtension(path).TrimStart('.').ToLowerInvariant();return Task.FromResult(extension=="jpeg"?"jpg":extension);}
  static Task<ConversionCollectionResult> Collected(IList<ConvertedFile> files){return Task.FromResult(new ConversionCollectionResult{CollectedPaths=files.SelectMany(file=>file.Outputs).ToArray(),Message="合成收藏完成"});}
  static Task<QuickConversionOutcome> Run(string[] inputs,string target,string destination,ConvertFiles converter,Func<IList<ConvertedFile>,Task<ConversionCollectionResult>> collector=null,Func<string,CancellationToken,Task<string>> validate=null,CancellationToken? token=null){
   return QuickConversionJob.RunAsync(inputs,target,destination,validate??Extension,(paths,format,folder,quality,progress,cancel)=>converter(paths,format,folder,quality,progress,cancel),collector??Collected,null,token??CancellationToken.None);
  }
  internal static void Run(string root,Action<bool,string> check){
   string sandbox=Path.Combine(root,"quick-conversion");Directory.CreateDirectory(sandbox);
   var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{MenuAndSelection(check);CapabilitiesValidation(check);EntryContracts(check);StaticSources(sandbox,check);ReadLockLifecycle(sandbox,check);SingleAndBatch(sandbox,check);FrozenInputs(sandbox,check);FailuresAndCancellation(sandbox,check);UntrustedReplies(sandbox,check);MultipleOutputJobs(sandbox,check);MetadataAndOriginals(sandbox,check);MultipleOutputCollection(sandbox,check);ExternalPanel(sandbox,check);ExternalPanelLifecycle(sandbox,check);BroaderFormatCollection(sandbox,check);RenderExternalPanel(sandbox,check);RenderVideoFormats(sandbox,check);}
   finally{SynchronizationContext.SetSynchronizationContext(previous);}
   check(Application.Current==null,"quick conversion checks never create an Application or a desktop window");
  }
  static void MenuAndSelection(Action<bool,string> check){
   var supported=new[]{"jpg","png","webp","tiff","gif","bmp"};
   var capabilities=new ConversionCapabilities{
    Targets=supported.Select(format=>new ConversionTarget{Id=format,Label=format.ToUpperInvariant(),Extension=format,Warning="每页生成独立副本；原文件保留"}).ToArray(),
    Files=new[]{new ConversionInputInfo{Source=@"C:\synthetic\source.pdf",Format="pdf",Targets=supported,Warnings=new[]{"PDF 每页生成一个图片文件"}}}};
   var chosen=new List<string>();var menu=MainWindow.BuildQuickConversionMenu(format=>chosen.Add(format),true,capabilities);
   var items=MenuTargets(menu).ToArray();
   check(Convert.ToString(menu.Header)=="转为"&&menu.Items.OfType<MenuItem>().Count(item=>item.Tag is string)==5&&menu.Items.OfType<MenuItem>().Single(item=>item.Items.Count>0).Header.ToString()=="更多格式","quick action exposes five common outputs plus one More formats submenu without opening a conversion panel");
   check(new HashSet<string>(items.Select(item=>Convert.ToString(item.Tag))).SetEquals(supported)&&items.Length==supported.Length,"PDF quick menu retains every advertised output exactly once across both levels");
   check(items.All(item=>ToolTipService.GetShowOnDisabled(item))&&ToolTipService.GetShowOnDisabled(menu),"disabled quick choices retain their explanatory tooltips");
   foreach(var item in items)item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,item));
   check(chosen.SequenceEqual(items.Select(item=>Convert.ToString(item.Tag)))&&chosen.Count==supported.Length,"each enabled dynamic target dispatches its exact format once");
   var disabled=MainWindow.BuildQuickConversionMenu(format=>chosen.Add(format),false,capabilities);int beforeDisabled=chosen.Count;
   foreach(var item in MenuTargets(disabled))item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,item));
   check(!disabled.IsEnabled&&chosen.Count==beforeDisabled,"busy quick conversion menu blocks dispatch from both common and nested outputs");
   string[] many=ConversionFormats.All.Where(format=>format.Writable).Take(15).Select(format=>format.Id).ToArray();
   var manyCaps=new ConversionCapabilities{Targets=many.Select(format=>new ConversionTarget{Id=format,Extension=ConversionFormats.Extension(format),Label=ConversionFormats.Label(format)}).ToArray(),
    Files=new[]{new ConversionInputInfo{Source=@"C:\synthetic\source.pdf",Format="pdf",Targets=many,Warnings=new string[0]}}};
   var broad=MainWindow.BuildQuickConversionMenu(format=>{},true,manyCaps);
   check(MenuTargets(broad).Count()==many.Length&&broad.Items.OfType<MenuItem>().Count(item=>item.Tag is string)==5&&broad.Items.OfType<MenuItem>().Count(item=>item.Items.Count>0)==1,"large actual capability sets retain all formats while limiting the first-level menu to five choices");
   var unavailable=MainWindow.BuildQuickConversionMenu(format=>chosen.Add(format),true,new ConversionCapabilities{Targets=new ConversionTarget[0],Files=new ConversionInputInfo[0],MissingEngines=new[]{"缺少 Ghostscript"}});
   check(!MenuTargets(unavailable).Any()&&unavailable.Items.OfType<MenuItem>().All(item=>!item.IsEnabled),"missing engines leave explanatory disabled items instead of advertising dead conversion actions");
   var mixedCaps=new ConversionCapabilities{Targets=new[]{new ConversionTarget{Id="png",Label="PNG",Extension="png"},new ConversionTarget{Id="jpg",Label="JPG",Extension="jpg"}},
    Files=new[]{new ConversionInputInfo{Source=@"C:\synthetic\one.pdf",Format="pdf",Targets=new[]{"png","jpg"}},new ConversionInputInfo{Source=@"C:\synthetic\two.txt",Format="txt",Targets=new[]{"png"}}}};
   var mixed=MainWindow.BuildQuickConversionMenu(format=>{},true,mixedCaps);
   check(MenuTargets(mixed).Select(item=>Convert.ToString(item.Tag)).SequenceEqual(new[]{"png"}),"a mixed selection only shows routes supported by every source");
   SourceAwareMenus(check);

   var first=new Asset{Id="first",Ext=".png"};var second=new Asset{Id="second",Ext=".jpg"};var changed=new Asset{Id="changed",Ext=".png"};
   check(MainWindow.QuickConversionSelectionMatches(new[]{first.Id,second.Id},new[]{second,first}),"unchanged multi-selection is accepted independent of gallery ordering");
   check(!MainWindow.QuickConversionSelectionMatches(new[]{first.Id},new[]{changed}),"an old menu cannot convert the newly selected different image");
   check(!MainWindow.QuickConversionSelectionMatches(new[]{first.Id},new[]{first,second}),"a menu opened for one image cannot silently include later additional selections");
   check(!MainWindow.QuickConversionSelectionMatches(new[]{first.Id,second.Id},new[]{first}),"a menu opened for two images cannot silently operate on an incomplete selection");
   first.Deleted=true;check(!MainWindow.QuickConversionSelectionMatches(new[]{first.Id},new[]{first}),"deleted captured record invalidates its old conversion menu");
  }
  static ConversionCapabilities MenuCapabilities(string source,params string[] targets){
   return new ConversionCapabilities{Targets=targets.Select(id=>new ConversionTarget{Id=id,Label=ConversionFormats.Label(id),Extension=ConversionFormats.Extension(id)}).ToArray(),
    Files=new[]{new ConversionInputInfo{Source=@"C:\synthetic\source."+source,Format=source,Targets=targets,Warnings=new string[0]}},MissingEngines=new string[0]};
  }
  static void SourceAwareMenus(Action<bool,string> check){
   var cases=new[]{
    new[]{"jpg","png","webp","pdf","tiff","bmp","gif","jpg"},
    new[]{"mov","mp4","webm","mkv","gif","mp3","wav","png","jpg","mov"},
    new[]{"flac","mp3","wav","m4a","ogg","aac","png","flac"},
    new[]{"doc","pdf","docx","txt","odt","rtf","png","doc"},
    new[]{"xls","xlsx","csv","pdf","ods","tsv","png","xls"},
    new[]{"ppt","pdf","pptx","png","odp","jpg","txt","ppt"},
    new[]{"rar","zip","7z","tar","tgz","txz","gz","png"},
    new[]{"epub","azw3","mobi","pdf","txt","fb2","png","epub"},
    new[]{"otf","ttf","woff2","woff","eot","png","otf"},
    new[]{"ass","srt","vtt","smi","lrc","ttml","png","ass"},
    new[]{"obj","glb","stl","gltf","3mf","ply","png","obj"}
   };
   foreach(var scenario in cases){
    string source=scenario[0];var caps=MenuCapabilities(source,scenario.Skip(1).Reverse().ToArray());
    var menu=MainWindow.BuildQuickConversionMenu(format=>{},true,caps);var direct=menu.Items.OfType<MenuItem>().Where(item=>item.Tag is string).ToArray();
    check(direct.Length==5&&direct.Take(3).Select(item=>Convert.ToString(item.Tag)).SequenceEqual(scenario.Skip(1).Take(3)),source+" common outputs follow source-specific priorities rather than worker order or unrelated image defaults");
    check(MenuTargets(menu).Select(item=>Convert.ToString(item.Tag)).Distinct().Count()==caps.Targets.Length&&MenuTargets(menu).Count()==caps.Targets.Length,source+" keeps every advertised format exactly once after shortlisting");
    check(direct.All(item=>item.IsEnabled&&Convert.ToString(item.Tag)!=source),source+" disabled same-format output cannot displace an executable common choice");
    check(menu.Items.OfType<MenuItem>().Last().Header.ToString()=="更多格式",source+" More formats follows the common outputs even when the original format ranks first");
   }
   var one=MainWindow.BuildQuickConversionMenu(format=>{},true,MenuCapabilities("pdf","png"));
   check(one.Items.OfType<MenuItem>().Count()==1&&Convert.ToString(one.Items.OfType<MenuItem>().Single().Tag)=="png","a single available output stays direct without an empty More formats submenu");
   var same=MainWindow.BuildQuickConversionMenu(format=>{throw new Exception("Same-format menu must not dispatch");},true,MenuCapabilities("png","png"));
   var sameItem=MenuTargets(same).Single();sameItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,sameItem));
   check(!sameItem.IsEnabled&&same.Items.OfType<MenuItem>().Count()==1&&Convert.ToString(sameItem.ToolTip).Contains("无需重复转换"),"an already-matching only output remains directly explained and cannot convert");
   var missingCaps=MenuCapabilities("webm","mp4","mp3","gif","wav","mkv","mov");missingCaps.MissingEngines=new[]{"缺少 FFmpeg"};missingCaps.MissingComponentIds=new[]{"ffmpeg"};
   var missing=MainWindow.BuildQuickConversionMenu(format=>{},true,missingCaps);
   check(missing.Items.OfType<MenuItem>().Any(item=>!item.IsEnabled&&Convert.ToString(item.ToolTip).Contains("FFmpeg"))&&MenuTargets(missing).Count()==missingCaps.Targets.Length,"missing-component explanation remains visible beside the compact outputs without hiding working formats");
   var duplicates=MenuCapabilities("jpg","png","png","webp","pdf","bmp","exe");
   var clean=MainWindow.BuildQuickConversionMenu(format=>{},true,duplicates);
   check(MenuTargets(clean).Select(item=>Convert.ToString(item.Tag)).OrderBy(id=>id).SequenceEqual(new[]{"bmp","pdf","png","webp"}),"shortlisting still rejects unknown targets and deduplicates worker replies");
  }
  static IEnumerable<MenuItem> MenuTargets(MenuItem parent){
   foreach(MenuItem item in parent.Items.OfType<MenuItem>()){
    if(item.Tag is string)yield return item;
    foreach(var child in MenuTargets(item))yield return child;
   }
  }
  static void CapabilitiesValidation(Action<bool,string> check){
   string pdf=@"C:\synthetic\document.pdf",png=@"C:\synthetic\picture.png";
   Func<ConversionCapabilities> valid=()=>new ConversionCapabilities{
    Targets=new[]{new ConversionTarget{Id="png",Label="PNG",Extension="png"}},
    Files=new[]{new ConversionInputInfo{Source=pdf,Format="pdf",Targets=new[]{"png"}},new ConversionInputInfo{Source=png,Format="png",Targets=new[]{"jpg"}}}};
   var accepted=MainWindow.ValidateQuickConversionCapabilities(new[]{pdf,png},"png",valid());
   check(accepted[pdf]=="pdf"&&accepted[png]=="png","fresh capability validation accepts a real route plus an already-target source that will be skipped");
   foreach(string scenario in new[]{"unknown-source","missing-source","repeated-source","changed-format","missing-file-route","missing-global-route"}){
    var caps=valid();
    if(scenario=="unknown-source")caps.Files[0].Source=@"C:\synthetic\unselected.pdf";
    if(scenario=="missing-source")caps.Files=caps.Files.Take(1).ToArray();
    if(scenario=="repeated-source")caps.Files[1]=caps.Files[0];
    if(scenario=="changed-format")caps.Files[0].Format="gif";
    if(scenario=="missing-file-route")caps.Files[0].Targets=new[]{"jpg"};
    if(scenario=="missing-global-route")caps.Targets=new ConversionTarget[0];
    bool rejected=false;try{MainWindow.ValidateQuickConversionCapabilities(new[]{pdf,png},"png",caps);}catch(IOException){rejected=true;}
    check(rejected,"fresh capability validation rejects "+scenario+" before starting conversion");
   }
   var baseline=valid();string stamp=MainWindow.QuickConversionCapabilityStamp(baseline,"png");
   var reordered=valid();reordered.Files=reordered.Files.Reverse().ToArray();
   check(stamp==MainWindow.QuickConversionCapabilityStamp(reordered,"png"),"capability stamp is stable when equivalent source records arrive in a different order");
   var changed=valid();changed.Targets[0].Warning="仅第一帧";
   check(stamp!=MainWindow.QuickConversionCapabilityStamp(changed,"png"),"changed target consequence invalidates the previously shown conversion choice");
   changed=valid();changed.Files[0].Warnings=new[]{"图层会合并"};
   check(stamp!=MainWindow.QuickConversionCapabilityStamp(changed,"png"),"changed source consequence invalidates the previously shown conversion choice");
   foreach(var sample in new[]{new[]{"bundle.tar.gz","tgz"},new[]{"book.kepub.epub","kepub"},new[]{"photo.JPEG","jpg"},new[]{"scan.TIF","tiff"},new[]{"phone.HEIF","heic"}}){
    string path=@"C:\synthetic\"+sample[0];var aliases=PanelCapabilities(new[]{path},"png");
    var format=MainWindow.ValidateQuickConversionCapabilities(new[]{path},"png",aliases)[path];
    check(format==sample[1]&&ConversionFormats.MatchesExtension(path,sample[1]),"source format validation preserves canonical compound/alias identity for "+sample[0]);
   }
  }
  static ConversionCapabilities PanelCapabilities(string[] paths,params string[] targets){
   return new ConversionCapabilities{Targets=targets.Select(id=>new ConversionTarget{Id=id,Label=ConversionFormats.Label(id),Extension=ConversionFormats.Extension(id)}).ToArray(),
    Files=paths.Select(path=>new ConversionInputInfo{Source=path,Format=ConversionFormats.Detect(path),Targets=targets,Warnings=new string[0]}).ToArray(),MissingEngines=new string[0]};
  }
  static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));}
  static void ExternalPanel(string root,Action<bool,string> check){
   string area=Path.Combine(root,"external-panel"),source=Picture(area,"external.png",103),alternate=Picture(area,"other.png",104),before=Hash(source),destination=Path.Combine(area,"results");
   int probes=0,conversions=0;bool locked=false;string[] captured=null;string capturedFolder=null;int capturedQuality=0;
   using(var unavailable=new QuickFileConversionPanel(false,(paths,token)=>{probes++;throw new Exception("Unavailable panel must not probe");},(paths,target,folder,quality,progress,token)=>{conversions++;throw new Exception("Unavailable panel must not convert");},destination)){
    check(!Directory.Exists(destination)&&unavailable.Inputs.Length==0&&!unavailable.IsBusy&&!unavailable.IsChecking,"external panel construction creates no destination, worker or desktop surface");
    Await(unavailable.AddFilesAsync(new[]{source}));Await(unavailable.StartAsync("jpg"));
    check(probes==0&&conversions==0&&unavailable.FormatChoices.Children.Count==0&&unavailable.Status.Text.Contains("未安装"),"missing external conversion component has an honest explanation and never starts conversion");
   }
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>{Interlocked.Increment(ref probes);return Task.FromResult(PanelCapabilities(paths,"jpg","webp"));},
    (paths,target,folder,quality,progress,token)=>{
     conversions++;captured=(string[])paths.Clone();capturedFolder=folder;capturedQuality=quality;
     try{using(var writer=new FileStream(paths[0],FileMode.Open,FileAccess.Write,FileShare.Read)){} }catch(IOException){locked=true;}
     return Task.FromResult(Batch(Success(paths[0],Picture(folder,"external-copy.jpg",105))));
    },destination)){
    var supplied=new[]{source};var load=panel.AddFilesAsync(supplied);supplied[0]=alternate;Await(load);
    check(panel.Inputs.SequenceEqual(new[]{source})&&!panel.IsChecking&&panel.FormatChoices.Children.OfType<Button>().Select(button=>Convert.ToString(button.Tag)).SequenceEqual(new[]{"jpg","webp"}),"external drop freezes the supplied file and exposes real target choices directly");
    int count=probes;Await(panel.AddFilesAsync(null));Await(panel.AddFilesAsync(new string[0]));
    panel.ChooseFiles=()=>null;Click(panel.Add);Drain();
    check(panel.Inputs.SequenceEqual(new[]{source})&&probes==count,"cancelling external file selection does not clear the existing file or restart checking");
    Await(panel.StartAsync("pdf"));
    check(conversions==0&&panel.ResultPaths.Length==0,"external panel rejects targets absent from current capabilities");
    Await(panel.StartAsync("jpg"));
    check(conversions==1&&captured.SequenceEqual(new[]{source})&&capturedFolder==destination&&capturedQuality==90&&locked,"one external target click uses stable read-locked inputs, independent destination and default quality");
    check(panel.ResultPaths.Length==1&&File.Exists(panel.ResultPaths[0])&&panel.Results.Items.Count==1&&!panel.IsBusy&&Hash(source)==before,"external conversion retains a visible new file without modifying the original or importing into a library");
    string result=panel.ResultPaths[0];var exposed=panel.ResultPaths;exposed[0]="changed";
    check(panel.ResultPaths[0]==result,"result path accessor cannot mutate retained conversion history");
    string[] copied=null;string revealed=null;panel.CopyResults=paths=>copied=paths;panel.RevealResult=path=>revealed=path;
    Click(panel.CopyAll);Click(panel.OpenFolder);
    check(copied!=null&&copied.SequenceEqual(new[]{result})&&revealed==result,"result copy and reveal actions use generated files through injected callbacks without touching clipboard or Explorer");
    Await(panel.AddFilesAsync(new[]{alternate}));
    check(panel.Inputs.SequenceEqual(new[]{alternate})&&panel.ResultPaths.SequenceEqual(new[]{result}),"next external drop replaces the pending input instead of silently accumulating a batch and keeps earlier results");
    Click(panel.Clear);
    check(panel.Inputs.Length==0&&panel.ResultPaths.SequenceEqual(new[]{result})&&panel.FormatChoices.Children.Count==0,"clearing pending input preserves completed external conversion results");
    string updatedDestination=Path.Combine(area,"new-save-location");
    check(panel.TrySetOutputFolder(updatedDestination)&&panel.OutputFolder==updatedDestination&&!Directory.Exists(updatedDestination)&&panel.ResultPaths.SequenceEqual(new[]{result}),"reopening an idle converter refreshes its destination without creating a folder or losing previous results");
    Await(panel.AddFilesAsync(new[]{source}));Await(panel.StartAsync("jpg"));
    check(capturedFolder==updatedDestination&&panel.ResultPaths.Length==2&&File.Exists(result),"the next conversion uses the changed output preference while keeping older result files accessible");
   }
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>Task.FromResult(PanelCapabilities(paths,ConversionFormats.All.Where(format=>format.Writable&&format.Id!="png").Take(12).Select(format=>format.Id).ToArray())),
    (paths,target,folder,quality,progress,token)=>{throw new Exception("No conversion expected");},destination)){
    Await(panel.AddFilesAsync(new[]{source}));
    check(panel.FormatChoices.Children.OfType<Button>().Count(button=>button.Tag is string)==6&&panel.FormatChoices.Children.Contains(panel.MoreFormats),"external panel keeps common formats compact with remaining real formats behind one More action");
   }
   string pdf=TwoPagePdf(area,"external-pages.pdf",false);
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>Task.FromResult(PanelCapabilities(paths,"png")),
    (paths,target,folder,quality,progress,token)=>Task.FromResult(Batch(Success(paths[0],Picture(folder,"pages-001.png",106),Picture(folder,"pages-002.png",107)))),Path.Combine(area,"pages"))){
    Await(panel.AddFilesAsync(new[]{pdf}));Await(panel.StartAsync("png"));
    check(panel.ResultPaths.Select(Path.GetFileName).SequenceEqual(new[]{"pages-001.png","pages-002.png"})&&panel.Results.Items.Count==2,"external multi-page results retain every page in natural page order");
   }
  }
  static void ExternalPanelLifecycle(string root,Action<bool,string> check){
   string area=Path.Combine(root,"external-panel-lifecycle"),first=Picture(area,"one.png",108),second=Picture(area,"two.png",109),before=Hash(first),destination=Path.Combine(area,"results");
   var probeGate=new TaskCompletionSource<ConversionCapabilities>();int oldProbeStarted=0;
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>{
    if(paths[0]==first){Interlocked.Exchange(ref oldProbeStarted,1);return probeGate.Task;}
    return Task.FromResult(PanelCapabilities(paths,"webp"));
   },(paths,target,folder,quality,progress,token)=>{throw new Exception("No conversion expected");},destination)){
    var old=panel.AddFilesAsync(new[]{first});Until(()=>Volatile.Read(ref oldProbeStarted)==1);
    Await(panel.AddFilesAsync(new[]{second}));probeGate.SetResult(PanelCapabilities(new[]{first},"jpg"));Await(old);
    check(panel.Inputs.SequenceEqual(new[]{second})&&panel.FormatChoices.Children.OfType<Button>().Select(button=>Convert.ToString(button.Tag)).SequenceEqual(new[]{"webp"})&&!panel.IsChecking,"a late cancelled capabilities reply cannot replace the newest file or its target choices");
   }
   var convertGate=new TaskCompletionSource<ConversionBatchResult>();int started=0,engineCalls=0;CancellationToken runningToken=CancellationToken.None;
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>Task.FromResult(PanelCapabilities(paths,"jpg")),
    (paths,target,folder,quality,progress,token)=>{engineCalls++;runningToken=token;Interlocked.Exchange(ref started,1);return convertGate.Task;},destination)){
    Await(panel.AddFilesAsync(new[]{first,second}));var running=panel.StartAsync("jpg");Until(()=>Volatile.Read(ref started)==1);
    check(panel.IsBusy&&!panel.Add.IsEnabled&&!panel.FormatChoices.IsEnabled,"external running conversion disables replacing files and starting another target");
    check(!panel.TrySetOutputFolder(Path.Combine(area,"changed-while-running"))&&panel.OutputFolder==destination,"a retained busy converter cannot reroute an in-progress batch after settings change");
    Await(panel.AddFilesAsync(new[]{second}));Click(panel.Clear);Await(panel.StartAsync("jpg"));
    check(panel.Inputs.SequenceEqual(new[]{first,second})&&engineCalls==1,"drag, Clear and duplicate Start cannot change or duplicate an active external batch");
    panel.CancelOperation();string complete=Picture(destination,"completed.jpg",110);
    convertGate.SetResult(new ConversionBatchResult{Cancelled=true,Files=new List<ConvertedFile>{Success(first,complete)}});Await(running);
    check(runningToken.IsCancellationRequested&&!panel.IsBusy&&panel.ResultPaths.SequenceEqual(new[]{complete}),"external cancellation reaches the worker and retains already completed result files");
    string laterDestination=Path.Combine(area,"after-cancel-results");
    check(panel.TrySetOutputFolder(laterDestination)&&panel.OutputFolder==laterDestination&&panel.ResultPaths.SequenceEqual(new[]{complete}),"after the active batch ends the same converter can adopt a new default without dropping completed results");
    check(Hash(first)==before,"external cancellation never modifies original source bytes");
    using(var writer=new FileStream(first,FileMode.Open,FileAccess.Write,FileShare.None))check(writer.CanWrite,"external cancellation releases its stable original read lock");
   }
   int freshCalls=0,changedCalls=0;
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>{
    var caps=PanelCapabilities(paths,"jpg");if(Interlocked.Increment(ref freshCalls)>1)caps.Targets[0].Warning="新的内容损失说明";return Task.FromResult(caps);
   },(paths,target,folder,quality,progress,token)=>{changedCalls++;return Task.FromResult(Batch());},destination)){
    Await(panel.AddFilesAsync(new[]{first}));Await(panel.StartAsync("jpg"));
    check(changedCalls==0&&!panel.IsBusy&&panel.Status.Text.Contains("已变化"),"changed conversion consequences are shown again before any external file conversion starts");
   }
   int disposedProbeStarted=0;var disposedGate=new TaskCompletionSource<ConversionCapabilities>();CancellationToken disposedToken=CancellationToken.None;
   var disposedPanel=new QuickFileConversionPanel(true,(paths,token)=>{disposedToken=token;Interlocked.Exchange(ref disposedProbeStarted,1);return disposedGate.Task;},
    (paths,target,folder,quality,progress,token)=>{throw new Exception("No conversion expected");},destination);
   var pending=disposedPanel.AddFilesAsync(new[]{first});Until(()=>Volatile.Read(ref disposedProbeStarted)==1);
   disposedPanel.Dispose();disposedGate.SetResult(PanelCapabilities(new[]{first},"jpg"));Await(pending);
   check(disposedPanel.IsDisposed&&disposedToken.IsCancellationRequested&&disposedPanel.FormatChoices.Children.Count==0,"disposing external panel cancels checking and ignores delayed UI updates");
   int failedCalls=0;
   var invalidDestinationClient=new FileConversionClient(Path.Combine(area,"synthetic-worker.exe"),Path.Combine(area,"sessions"),(request,token,timeout)=>{failedCalls++;throw new Exception("Invalid destination must fail before worker");});
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>Task.FromResult(PanelCapabilities(paths,"jpg")),
    invalidDestinationClient.ConvertAsync,first)){
    Await(panel.AddFilesAsync(new[]{second}));Await(panel.StartAsync("jpg"));
    check(failedCalls==0&&panel.ResultPaths.Length==0&&!panel.IsBusy&&panel.Status.Text.Contains("不是文件夹"),"a file used as output directory is reported safely without starting conversion or claiming success");
   }
   var hiddenGate=new TaskCompletionSource<ConversionBatchResult>();int hiddenStarted=0;CancellationToken hiddenToken=CancellationToken.None;
   using(var panel=new QuickFileConversionPanel(true,(paths,token)=>Task.FromResult(PanelCapabilities(paths,"jpg")),
    (paths,target,folder,quality,progress,token)=>{hiddenToken=token;Interlocked.Exchange(ref hiddenStarted,1);return hiddenGate.Task;},destination)){
    Await(panel.AddFilesAsync(new[]{first}));var running=panel.StartAsync("jpg");Until(()=>Volatile.Read(ref hiddenStarted)==1);
    panel.Visibility=Visibility.Collapsed;
    check(panel.IsBusy&&!panel.IsDisposed&&!hiddenToken.IsCancellationRequested,"hiding the conversion view does not cancel or dispose a running operation");
    string completed=Picture(destination,"hidden-completed.jpg",117);hiddenGate.SetResult(Batch(Success(first,completed)));Await(running);
    panel.Visibility=Visibility.Visible;
    check(!panel.IsBusy&&panel.ResultPaths.SequenceEqual(new[]{completed})&&panel.Results.Items.Count==1&&File.Exists(completed),"hidden operation completes and retained results remain ready when the same view is reopened");
   }
   var disposeGate=new TaskCompletionSource<ConversionBatchResult>();int disposalStarted=0,disposalNotices=0;CancellationToken disposalToken=CancellationToken.None;
   var runningPanel=new QuickFileConversionPanel(true,(paths,token)=>Task.FromResult(PanelCapabilities(paths,"jpg")),
    (paths,target,folder,quality,progress,token)=>{disposalToken=token;Interlocked.Exchange(ref disposalStarted,1);return disposeGate.Task;},destination);
   runningPanel.Notify=message=>disposalNotices++;
   Await(runningPanel.AddFilesAsync(new[]{first}));var finishing=runningPanel.StartAsync("jpg");Until(()=>Volatile.Read(ref disposalStarted)==1);
   runningPanel.Dispose();string finalFile=Picture(destination,"completed-at-exit.jpg",118);
   disposeGate.SetResult(new ConversionBatchResult{Cancelled=true,Files=new List<ConvertedFile>{Success(first,finalFile)}});Await(finishing);
   check(runningPanel.IsDisposed&&disposalToken.IsCancellationRequested&&!runningPanel.IsBusy&&File.Exists(finalFile)&&Hash(first)==before&&disposalNotices==0,"real shutdown cancels running work without deleting already completed output or notifying a disposed view");
   using(var writer=new FileStream(first,FileMode.Open,FileAccess.Write,FileShare.None))check(writer.CanWrite,"disposal during conversion releases its original-file read lock after the worker finishes");
  }
  static void BroaderFormatCollection(string root,Action<bool,string> check){
   string area=Path.Combine(root,"broader-output"),source=Picture(area,"source.png",113),archive=Path.Combine(area,"设计原件.zip");
   File.WriteAllBytes(archive,new byte[]{0x50,0x4b,0x05,0x06,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0});
   string sourceHash=Hash(source),archiveHash=Hash(archive);
   using(var library=new Library(Path.Combine(area,"library"))){
    string category=library.CreateFolder("归档转换","");var original=library.Import(source,category,"设计原件","https://example.invalid/archive","原标签","原备注").Item;
    var collector=new FileConversionCollectionSession(library,new[]{original},true);
    var receipt=Await(collector.CollectAsync(new[]{Success(library.FullPath(original),archive)}));
    var copy=library.All().Single(asset=>asset.Id==collector.CollectedAssetId(archive));
    check(receipt.CollectedPaths.SequenceEqual(new[]{archive})&&copy.Ext==".zip"&&copy.Id!=original.Id&&!copy.Deleted,"converted ZIP output becomes a real independently addressable library record");
    check(copy.Name==original.Name&&copy.Tags==original.Tags&&copy.Source==original.Source&&copy.Notes==original.Notes&&library.AssetFolders(copy.Id).Any(folder=>folder.Id==category),"broader converted outputs inherit the source name, metadata and classification");
    check(File.Exists(library.FullPath(copy))&&Hash(library.FullPath(copy))==archiveHash&&Hash(source)==sourceHash&&library.All().Any(asset=>asset.Id==original.Id&&!asset.Deleted),"ZIP collection retains a managed new copy while preserving the source file and source record");
    check(MediaTypes.Contains(copy,"documents")&&MediaTypes.Contains(copy,"media")&&!MediaTypes.Contains(copy,"assets")&&!MediaTypes.Contains(copy,"pictures")&&copy.FileIcon,"generic ZIP result remains discoverable in documents and project categories with a file icon");
    check(!MediaTypes.CanThumbnail(copy)&&!MediaTypes.Documents.Contains(copy.Ext),"generic generated file does not falsely claim a built-in document preview");
    bool ordinaryRejected=false;try{library.Import(archive,"",null,"","","");}catch(IOException){ordinaryRejected=true;}
    check(ordinaryRejected,"conversion-only collection does not relax ordinary library import validation");
    foreach(string suffix in new[]{"tar.gz","kepub.epub"}){
     string compound=Path.Combine(area,"设计原件."+suffix);File.WriteAllBytes(compound,new byte[]{1,2,3,(byte)suffix.Length});
     var receiptCompound=Await(collector.CollectAsync(new[]{Success(library.FullPath(original),compound)}));
     var converted=library.All().SingleOrDefault(asset=>asset.Id==collector.CollectedAssetId(compound));
     check(receiptCompound.CollectedPaths.SequenceEqual(new[]{compound})&&converted!=null&&converted.Ext=="."+suffix&&ConversionFormats.Detect(library.FullPath(converted))==ConversionFormats.Detect(compound),"collection retains complete compound format suffix "+suffix+" in stored file and asset");
     check(converted!=null&&converted.Name=="设计原件","compound conversion "+suffix+" does not append a partial format suffix to its display name");
    }
    string unsupported=Path.Combine(area,"unsupported.exe");File.WriteAllBytes(unsupported,new byte[]{1,2,3});
    bool conversionRejected=false;try{library.ImportConvertedFile(unsupported,null,"","","",null,"keep");}catch(IOException){conversionRejected=true;}
    check(conversionRejected,"conversion collection still rejects executable and unknown output formats");
   }
   foreach(var sample in new[]{new[]{".mxf","videos"},new[]{".flv","videos"},new[]{".m4b","audio"},new[]{".ac3","audio"},new[]{".doc","documents"},new[]{".zip","documents"}}){
    var generated=new Asset{Id="generated-"+sample[0],Ext=sample[0]};
    check(MediaTypes.Contains(generated,sample[1])&&MediaTypes.Contains(generated,"media"),"generated "+sample[0]+" remains discoverable under "+sample[1]+" and project categories");
   }
   foreach(string extension in new[]{".qoi",".jxl",".apng",".svgz",".tga",".eps"}){
    var picture=new Asset{Id="generated-"+extension,Ext=extension};
    check(MediaTypes.IsPictureFormat(extension)&&MediaTypes.Contains(picture,"pictures")&&MediaTypes.Contains(picture,"assets")&&!MediaTypes.Contains(picture,"documents")&&!MediaTypes.CanThumbnail(picture)&&picture.FileIcon&&FileRouter.Kind("generated"+extension)=="external","generated "+extension+" is classified as a picture without inventing built-in thumbnail or preview support");
   }
  }
  static void RenderExternalPanel(string root,Action<bool,string> check){
   string area=Path.Combine(root,"external-previews"),png=Picture(area,"产品参考图.png",114),pdf=TwoPagePdf(area,"项目说明书.pdf",false);
   foreach(bool dark in new[]{false,true})foreach(string state in new[]{"empty","formats","pdf-results"}){
    using(var panel=new QuickFileConversionPanel(true,(paths,token)=>Task.FromResult(PanelCapabilities(paths,"jpg","png","webp","pdf","bmp","gif","tiff","ico")),
     (paths,target,folder,quality,progress,token)=>Task.FromResult(Batch(Success(paths[0],Picture(folder,"项目说明书-001.png",115),Picture(folder,"项目说明书-002.png",116)))),Path.Combine(area,"outputs-"+state+"-"+dark))){
     typeof(FileConversionUiTests).GetMethod("ApplyActualTheme",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{panel,dark});
     if(state!="empty")Await(panel.AddFilesAsync(new[]{state=="formats"?png:pdf}));
     if(state=="pdf-results")Await(panel.StartAsync("png"));
     var size=new Size(620,590);var host=new Border{Background=(Brush)panel.Resources["Surface"],Child=panel};
     host.Measure(size);host.Arrange(new Rect(new Point(),size));host.UpdateLayout();Drain();
     check(Application.Current==null&&PresentationSource.FromVisual(panel)==null&&panel.ActualWidth==size.Width,"external "+state+" preview uses real theme and layout without an Application, HWND or visible window");
     if(state=="formats"){
      var choice=panel.FormatChoices.Children.OfType<Button>().First(button=>button.Tag is string);
      check(choice.ActualHeight>=32&&choice.TranslatePoint(new Point(0,choice.ActualHeight),host).Y<=size.Height&&panel.MoreFormats.ActualWidth>0,"external common formats and More action remain reachable in the normal-sized panel");
     }
     if(state=="pdf-results"){
      var scroll=panel.Children.OfType<ScrollViewer>().Single();scroll.ScrollToBottom();host.UpdateLayout();Drain();
      check(panel.Results.Items.Count==2&&panel.CopyAll.ActualHeight>=32&&panel.CopyAll.TranslatePoint(new Point(0,panel.CopyAll.ActualHeight),host).Y<=size.Height&&panel.OpenFolder.IsEnabled,"external PDF results keep both pages and accessible copy/location actions within the panel");
     }
     string path=Path.Combine(area,"quick-external-"+(dark?"dark":"light")+"-"+state+".png");
     var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(host);
     var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))encoder.Save(stream);
     check(File.Exists(path)&&new FileInfo(path).Length>1000,"external conversion offscreen preview saved: "+path);
     if(state=="pdf-results"){
      var compact=new Size(480,400);host.Measure(compact);host.Arrange(new Rect(new Point(),compact));host.UpdateLayout();Drain();
      var scroll=panel.Children.OfType<ScrollViewer>().Single();scroll.ScrollToBottom();host.UpdateLayout();Drain();
      var footer=panel.Children.OfType<TextBlock>().Single();
      check(panel.CopyAll.TranslatePoint(new Point(0,0),host).Y>=0&&panel.CopyAll.TranslatePoint(new Point(0,panel.CopyAll.ActualHeight),host).Y<=compact.Height&&footer.ActualHeight<25,"compact result panel scrolls to reachable actions while keeping long destination on one line");
     }
    }
   }
  }
  static void RenderVideoFormats(string root,Action<bool,string> check){
   string area=Path.Combine(root,"external-previews"),source=Path.Combine(area,"产品演示视频.mkv");
   Directory.CreateDirectory(area);File.WriteAllText(source,"Synthetic capabilities-only fixture; no decoder is invoked.");
   foreach(bool dark in new[]{false,true})foreach(bool compact in new[]{false,true}){
    using(var panel=new QuickFileConversionPanel(true,(paths,token)=>{
     var caps=PanelCapabilities(paths,"mp4","webm","mov","avi","m4v","mp3","wav","m4a");
     foreach(var target in caps.Targets)target.Warning=target.Id=="mp3"||target.Id=="wav"||target.Id=="m4a"
      ?"仅导出音频，不包含视频画面。":"保留第一路视频和第一条音轨；其他音轨、字幕和附件不包含在结果中。";
     return Task.FromResult(caps);
    },(paths,target,folder,quality,progress,token)=>{throw new Exception("A visual capability fixture must not convert.");},Path.Combine(area,"video-output"))){
     typeof(FileConversionUiTests).GetMethod("ApplyActualTheme",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{panel,dark});
     Await(panel.AddFilesAsync(new[]{source}));
     var size=compact?new Size(480,400):new Size(620,590);
     var host=new Border{Background=(Brush)panel.Resources["Surface"],Child=panel};
     host.Measure(size);host.Arrange(new Rect(new Point(),size));host.UpdateLayout();Drain();
     var scroll=panel.Children.OfType<ScrollViewer>().Single();
     var buttons=panel.FormatChoices.Children.OfType<Button>().Where(button=>button.Tag is string).ToArray();
     string label=(dark?"dark":"light")+" / "+(compact?"narrow":"normal")+" video";
     check(buttons.Length==6&&panel.FormatChoices.Children.Contains(panel.MoreFormats),label+" shows six common targets and one More action despite lengthy real video warnings");
     check(buttons.All(button=>button.ActualWidth>=150&&button.TranslatePoint(new Point(),host).X>=0&&button.TranslatePoint(new Point(button.ActualWidth,0),host).X<=size.Width),label+" every format card remains within the viewport width without horizontal clipping");
     foreach(var button in buttons){
      scroll.ScrollToVerticalOffset(0);host.UpdateLayout();Drain();
      double y=button.TranslatePoint(new Point(),scroll).Y;
      scroll.ScrollToVerticalOffset(Math.Max(0,y-8));host.UpdateLayout();Drain();
      double top=button.TranslatePoint(new Point(),scroll).Y;
      check(top>=0&&top+button.ActualHeight<=scroll.ActualHeight,label+" target "+button.Tag+" and its complete consequence text are reachable by ordinary scrolling");
     }
     scroll.ScrollToBottom();host.UpdateLayout();Drain();
     check(panel.MoreFormats.TranslatePoint(new Point(),host).Y>=0&&panel.MoreFormats.TranslatePoint(new Point(0,panel.MoreFormats.ActualHeight),host).Y<=size.Height,label+" More formats remains reachable at the bottom");
     if(!compact){scroll.ScrollToTop();host.UpdateLayout();Drain();}
     var image=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);image.Render(host);
     var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
     string path=Path.Combine(area,"quick-external-"+(dark?"dark":"light")+"-video-formats-"+(compact?"narrow":"normal")+".png");
     using(var file=File.Create(path))encoder.Save(file);
     check(Application.Current==null&&PresentationSource.FromVisual(panel)==null&&File.Exists(path),label+" screenshot rendered without a desktop window or real video");
    }
   }
  }
  static IEnumerable<MemberInfo> ReferencedMembers(MethodInfo method){
   var body=method.GetMethodBody();if(body==null)yield break;
   var opcodes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(field=>field.FieldType==typeof(OpCode)).Select(field=>(OpCode)field.GetValue(null)).ToDictionary(code=>unchecked((ushort)code.Value));
   byte[] bytes=body.GetILAsByteArray();int offset=0;
   while(offset<bytes.Length){
    ushort value=bytes[offset++];if(value==0xfe)value=(ushort)(0xfe00|bytes[offset++]);OpCode code=opcodes[value];int length=0;
    switch(code.OperandType){
     case OperandType.InlineNone:break;
     case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:length=1;break;
     case OperandType.InlineVar:length=2;break;
     case OperandType.InlineI8:case OperandType.InlineR:length=8;break;
     case OperandType.InlineSwitch:length=4+4*BitConverter.ToInt32(bytes,offset);break;
     default:length=4;break;
    }
    if(code.OperandType==OperandType.InlineMethod||code.OperandType==OperandType.InlineField){
     MemberInfo member=null;try{member=method.Module.ResolveMember(BitConverter.ToInt32(bytes,offset));}catch(ArgumentException){}
     if(member!=null)yield return member;
    }
    offset+=length;
   }
  }
  static IEnumerable<MemberInfo> EntryMembers(string name){
   const BindingFlags flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
   var pending=new Queue<MethodInfo>();var visited=new HashSet<MethodInfo>();pending.Enqueue(typeof(MainWindow).GetMethod(name,flags));
   while(pending.Count>0){
    var method=pending.Dequeue();if(method==null||!visited.Add(method))continue;
    foreach(var member in ReferencedMembers(method)){
     yield return member;var callback=member as MethodInfo;
     if(callback!=null&&callback.Name.StartsWith("<",StringComparison.Ordinal)&&callback.DeclaringType.Assembly==typeof(MainWindow).Assembly)pending.Enqueue(callback);
    }
   }
  }
  static void EntryContracts(Action<bool,string> check){
   var asset=EntryMembers("CreateAssetMenu").ToArray();var tools=EntryMembers("CreateToolMenu").ToArray();
   check(asset.Any(member=>member.Name=="CreateQuickConversionMenu")&&!asset.Any(member=>member.Name=="ConvertSelectedFiles"||member.Name=="OpenFileConversion"),"real asset menu reaches the quick submenu and no longer opens the complex inspector");
   check(!tools.Any(member=>member.Name=="OpenFileConversion"||member.DeclaringType==typeof(FileConversionWindow)),"real tools menu no longer exposes the separate conversion window or inspector workflow");
   check(!EntryMembers("EnsureFileConversionHost").Any(member=>member.MemberType==MemberTypes.Field&&member.Name=="fileConversionTask"),"legacy host construction cannot add the old top conversion task button");
   var external=EntryMembers("OpenQuickFileConversion").ToArray();
   check(tools.Any(member=>member.Name=="OpenQuickFileConversion")&&external.Any(member=>member.Name=="Show")&&!external.Any(member=>member.Name=="ShowDialog"),"real Tools entry opens the retained quick converter non-modally instead of blocking the library");
   check(EntryMembers("Preview").Any(member=>member.Name=="OpenReference")&&new[]{"archive.zip","archive.tar.gz","book.kepub.epub","document.doc"}.All(path=>FileRouter.Kind(path)=="external"),"generic conversion results retain an external-open route without invoking a system app in tests");
   var handlers=typeof(QuickFileConversionWindow).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Where(method=>method.Name.StartsWith("<.ctor>",StringComparison.Ordinal)&&method.GetParameters().Length==2).ToArray();
   foreach(Type eventType in new[]{typeof(System.ComponentModel.CancelEventArgs),typeof(System.Windows.Input.KeyEventArgs)}){
    var handler=handlers.Single(method=>method.GetParameters()[1].ParameterType==eventType);var members=ReferencedMembers(handler).ToArray();
    check(members.Any(member=>member.Name=="Hide")&&!members.Any(member=>member.Name=="Dispose"||member.Name=="CancelOperation"),"window "+eventType.Name+" handler hides the retained converter without cancelling its job");
   }
  }
  static void StaticSources(string root,Action<bool,string> check){
   // Retained legacy helper tests: the expanded menu must use actual worker
   // capabilities, not loosen this deliberately still-image-only validator.
   string png=Picture(root,"validated.png",11),jpeg=Picture(root,"validated.jpeg",12);
   check(QuickImageConversion.ValidateStaticFile(png)=="png"&&QuickImageConversion.ValidateStaticFile(jpeg)=="jpg","real generated PNG and JPEG pass still-image validation without decoding pixels into a window");
   foreach(string extension in new[]{".png",".PNG",".jpg",".jpeg",".webp",".bmp"})check(QuickImageConversion.SupportsExtension(extension),"legacy still-image helper supports "+extension);
   foreach(string extension in new[]{".gif",".apng",".pdf",".psd",".ai",".mp4",".svg",".exe",".txt",""})check(!QuickImageConversion.SupportsExtension(extension),"legacy still-image helper stays deliberately restricted for "+extension);
   check(QuickImageConversion.TargetsFor(".jpeg").SequenceEqual(new[]{"png","webp"})&&!QuickImageConversion.TargetsFor(".png").Contains("png"),"same-format menu targets use normalized JPEG aliases");
   string disguised=Path.Combine(root,"jpeg-disguised-as.png");File.Copy(jpeg,disguised);
   bool rejected=false;try{QuickImageConversion.ValidateStaticFile(disguised);}catch(IOException){rejected=true;}
   check(rejected,"extension alone cannot make nonmatching content a valid conversion source");
   string broken=Path.Combine(root,"truncated.png");File.WriteAllBytes(broken,File.ReadAllBytes(png).Take(18).ToArray());
   rejected=false;try{QuickImageConversion.ValidateStaticFile(broken);}catch(IOException){rejected=true;}
   check(rejected,"truncated image is rejected before a conversion or partial result can be claimed");
   string apng=Path.Combine(root,"animated.png");byte[] staticPng=File.ReadAllBytes(png);
   var animation=new byte[]{0,0,0,8,97,99,84,76,0,0,0,2,0,0,0,0,0,0,0,0};
   File.WriteAllBytes(apng,staticPng.Take(33).Concat(animation).Concat(staticPng.Skip(33)).ToArray());
   string reason=null;try{QuickImageConversion.ValidateStaticFile(apng);}catch(IOException error){reason=error.Message;}
   check(reason!=null&&reason.Contains("动图"),"APNG hidden behind a normal PNG suffix is explicitly refused instead of flattening its first frame");
   string animatedWebP=Path.Combine(root,"animated.webp");
   using(var stream=File.Create(animatedWebP))using(var writer=new BinaryWriter(stream)){
    writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(22u);writer.Write(Encoding.ASCII.GetBytes("WEBPVP8X"));writer.Write(10u);writer.Write(new byte[]{2,0,0,0,0,0,0,0,0,0});
   }
   reason=null;try{QuickImageConversion.ValidateStaticFile(animatedWebP);}catch(IOException error){reason=error.Message;}
   check(reason!=null&&reason.Contains("动图"),"animated WebP container flag is explicitly refused even with an otherwise allowed suffix");
  }
  static void ReadLockLifecycle(string root,Action<bool,string> check){
   string first=Picture(root,"locked-first.png",15),second=Picture(root,"locked-second.png",16),before=Hash(first);
   var locks=MainWindow.OpenQuickInputReadLocks(new[]{first,second},CancellationToken.None);
   try{
    bool blocked=false;try{using(var writer=new FileStream(first,FileMode.Open,FileAccess.Write,FileShare.Read)){} }catch(IOException){blocked=true;}
    using(var reader=new FileStream(first,FileMode.Open,FileAccess.Read,FileShare.Read))check(blocked&&reader.Length>0,"quick conversion source lock allows worker readers but prevents replacing validated pixels during conversion");
   }finally{foreach(var item in locks)item.Dispose();}
   using(var writer=new FileStream(first,FileMode.Open,FileAccess.Write,FileShare.None))check(writer.CanWrite,"completed quick conversion releases the original source lock");
   bool failed=false;try{MainWindow.OpenQuickInputReadLocks(new[]{first,Path.Combine(root,"missing-lock-source.png")},CancellationToken.None);}catch(IOException){failed=true;}
   using(var writer=new FileStream(first,FileMode.Open,FileAccess.Write,FileShare.None))check(failed&&writer.CanWrite,"failure opening a later source releases all earlier source locks");
   using(var cancellation=new CancellationTokenSource()){
    cancellation.Cancel();bool cancelled=false;try{MainWindow.OpenQuickInputReadLocks(new[]{first},cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
    using(var writer=new FileStream(first,FileMode.Open,FileAccess.Write,FileShare.None))check(cancelled&&writer.CanWrite,"cancelled quick conversion lock acquisition leaves no retained handles");
   }
   check(Hash(first)==before,"read-lock lifecycle testing never alters the original image bytes");
  }
  static void SingleAndBatch(string root,Action<bool,string> check){
   string input=Picture(root,"quick-source.png",21),directory=Path.Combine(root,"managed-results"),output=Picture(directory,"quick-result.jpg",22);string before=Hash(input);
   int conversions=0,collections=0;string[] received=null;string target=null,folder=null;int quality=0;
   var one=Await(Run(new[]{input},"jpg",directory,(paths,format,destination,q,progress,token)=>{
    conversions++;received=paths;target=format;folder=destination;quality=q;return Task.FromResult(Batch(Success(input,output)));
   },files=>{collections++;return Collected(files);}));
   check(conversions==1&&collections==1&&received.SequenceEqual(new[]{input})&&target=="jpg"&&folder==directory&&quality==90,"one target click performs one immediate conversion with fixed sensible defaults");
   check(one.Converted==1&&one.Collected==1&&one.Failed==0&&!one.Cancelled,"one-click success reports both converted and actually collected counts");
   check(Hash(input)==before&&File.Exists(output),"one-click success preserves original bytes and its separate result file");

   conversions=collections=0;
   var same=Await(Run(new[]{input},"png",directory,(paths,format,destination,q,progress,token)=>{conversions++;return Task.FromResult(new ConversionBatchResult());},files=>{collections++;return Collected(files);}));
   check(same.Skipped==1&&same.Converted==0&&same.Collected==0&&conversions==0&&collections==0,"same-format source is skipped without starting conversion or creating a duplicate");

   string alreadyJpeg=Picture(root,"already.jpeg",23),other=Picture(root,"other.png",24),otherResult=Picture(directory,"other.jpg",25);conversions=0;
   var mixed=Await(Run(new[]{input,alreadyJpeg,other},"jpg",directory,(paths,format,destination,q,progress,token)=>{
    conversions++;received=paths;return Task.FromResult(Batch(Success(input,output),Success(other,otherResult)));
   }));
   check(conversions==1&&received.SequenceEqual(new[]{input,other})&&mixed.Skipped==1,"mixed multi-select excludes JPEG alias already in the JPG target format");
   check(mixed.Converted==2&&mixed.Collected==2&&mixed.Failed==0,"multi-select remains one operation and automatically collects each converted result");

   var invalid=Picture(root,"unsupported-source.png",26);conversions=collections=0;QuickConversionOutcome rejected=null;Exception error=null;
   try{rejected=Await(Run(new[]{invalid},"exe",directory,(paths,format,destination,q,progress,token)=>{conversions++;return Task.FromResult(new ConversionBatchResult());},files=>{collections++;return Collected(files);}));}catch(Exception problem){error=problem;}
   check(conversions==0&&collections==0&&(error!=null||rejected==null||rejected.Collected==0),"unsupported direct target cannot reach the engine or collection");
  }
  static void FrozenInputs(string root,Action<bool,string> check){
   string first=Picture(root,"frozen-source.png",31),replacement=Picture(root,"later-selection.png",32),output=Picture(root,"frozen-result.jpg",33),second=Picture(root,"frozen-second.png",34),secondOutput=Picture(root,"frozen-second.jpg",35);
   string[] supplied={first,second};var validation=new TaskCompletionSource<string>();string[] received=null;int checks=0;
   var pending=Run(supplied,"jpg",root,(paths,format,destination,q,progress,token)=>{received=paths;return Task.FromResult(Batch(Success(first,output),Success(second,secondOutput)));},null,(path,token)=>{checks++;return path==first?validation.Task:Extension(path,token);});
   supplied[0]=replacement;supplied[1]=replacement;validation.SetResult("png");var result=Await(pending);
   check(checks==2&&received!=null&&received.SequenceEqual(new[]{first,second})&&result.Collected==2,"submitted input list is frozen before awaiting validation and cannot follow a later gallery selection");

   var empty=Run(new string[0],"jpg",root,(paths,format,destination,q,progress,token)=>{throw new Exception("empty quick input must not convert");});
   bool rejected=false;try{var value=Await(empty);rejected=value.Collected==0&&value.Converted==0;}catch(ArgumentException){rejected=true;}
   check(rejected,"empty selection cannot start a quick conversion");
  }
  static void FailuresAndCancellation(string root,Action<bool,string> check){
   string first=Picture(root,"failure-source.png",41),second=Picture(root,"partial-source.png",42),success=Picture(root,"partial-result.jpg",43);
   int conversions=0,collections=0;
   var validation=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>{conversions++;return Task.FromResult(new ConversionBatchResult());},files=>{collections++;return Collected(files);},
    (path,token)=>{throw new InvalidDataException("不是支持的静态图片");}));
   check(conversions==0&&collections==0&&validation.Collected==0&&validation.Failed>0,"invalid source fails before conversion and cannot be reported as saved");

   var engine=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>{throw new IOException("synthetic engine failure");},files=>{collections++;return Collected(files);}));
   check(engine.Collected==0&&engine.Failed>0&&collections==0&&!string.IsNullOrWhiteSpace(engine.Message),"engine exception returns a visible failure, not an unhandled callback or false success");

   IList<ConvertedFile> imported=null;
   var partial=Await(Run(new[]{first,second},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Failure(first,"synthetic failure"),Success(second,success))),
    files=>{imported=files;return Collected(files);}));
   check(imported!=null&&imported.Count==1&&imported[0].Source==second&&imported[0].Outputs.SequenceEqual(new[]{success}),"only successful outputs from a partially failed conversion reach automatic collection");
   check(partial.Collected==1&&partial.Failed>0&&File.Exists(success),"partial success retains its new file while still reporting the failed source");

   var noCollection=Await(Run(new[]{second},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(second,success))),
    files=>{throw new IOException("synthetic library unavailable");}));
   check(noCollection.Collected==0&&noCollection.Failed>0&&File.Exists(success),"collection failure never claims success or deletes the independently generated file");

   var emptyReceipt=Await(Run(new[]{second},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(second,success))),
    files=>Task.FromResult(new ConversionCollectionResult{CollectedPaths=new string[0],Message="合成收藏失败"})));
   check(emptyReceipt.Collected==0&&emptyReceipt.Failed>0,"a successful engine reply alone cannot masquerade as a saved library asset");

   using(var cancel=new CancellationTokenSource()){
    cancel.Cancel();conversions=collections=0;
    var stopped=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>{conversions++;return Task.FromResult(new ConversionBatchResult());},files=>{collections++;return Collected(files);},null,cancel.Token));
    check(stopped.Cancelled&&stopped.Collected==0&&conversions==0&&collections==0,"pre-cancelled quick action touches neither engine nor library");
   }
   using(var cancel=new CancellationTokenSource()){
    imported=null;
    var completed=Await(Run(new[]{first,second},"jpg",root,(paths,target,destination,quality,progress,token)=>{cancel.Cancel();return Task.FromResult(new ConversionBatchResult{Cancelled=true,Files=new List<ConvertedFile>{Success(second,success)}});},
     files=>{imported=files;return Collected(files);},null,cancel.Token));
    check(completed.Cancelled&&completed.Collected==1&&imported!=null&&imported.Count==1&&File.Exists(success),"cancelling remaining work still safely collects a file already completed");
   }
   collections=0;
   var nullReply=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult<ConversionBatchResult>(null),
    files=>{collections++;return Collected(files);}));
   check(nullReply.Collected==0&&nullReply.Failed>0&&collections==0,"missing engine reply is an explicit failure, never an empty successful operation");
  }
  static void UntrustedReplies(string root,Action<bool,string> check){
   string first=Picture(root,"reply-source.png",71),second=Picture(root,"reply-second.png",72),foreign=Picture(root,"unrequested-source.png",73);
   string output=Picture(root,"reply-output.jpg",74),other=Picture(root,"reply-other.jpg",75);int collections=0;
   var unknown=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(foreign,output))),
    files=>{collections++;return Collected(files);}));
   check(unknown.Collected==0&&unknown.Failed>0&&collections==0,"unrequested source reply cannot be saved as the selected image");
   var missing=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(first,Path.Combine(root,"missing-output.jpg")))),
    files=>{collections++;return Collected(files);}));
   check(missing.Collected==0&&missing.Failed>0&&collections==0,"missing output path is not treated as an immediately saved image");
   var empty=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(first,new string[0]))),
    files=>{collections++;return Collected(files);}));
   check(empty.Collected==0&&empty.Failed>0&&collections==0,"expanded quick conversion still rejects an empty result instead of claiming a saved copy");
   IList<ConvertedFile> submitted=null;
   var duplicate=Await(Run(new[]{first,second},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(first,output),Success(second,output))),
    files=>{submitted=files;return Collected(files);}));
   check(duplicate.Collected<2&&duplicate.Failed>0&&(submitted==null||submitted.SelectMany(file=>file.Outputs).Distinct(StringComparer.OrdinalIgnoreCase).Count()==submitted.Sum(file=>file.Outputs.Length)),"two sources cannot both claim one identical result path as independent saved conversions");
   var contradictory=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(new ConvertedFile{Source=first,Outputs=new[]{output},Error="failed despite output"})),
    files=>{collections++;return Collected(files);}));
   check(contradictory.Collected==0&&contradictory.Failed>0,"an engine reply containing a failure is not reported as successful even if it names a file");
   var falseReceipt=Await(Run(new[]{first},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(first,output))),
    files=>Task.FromResult(new ConversionCollectionResult{CollectedPaths=new[]{other},Message="unexpected receipt"})));
   check(falseReceipt.Collected==0&&falseReceipt.Failed>0,"collector receipt must acknowledge the submitted output, not an unrelated existing path");
  }
  static void MultipleOutputJobs(string root,Action<bool,string> check){
   string pdf=TwoPagePdf(root,"job-multipage.pdf",false),secondPdf=TwoPagePdf(root,"job-second.pdf",false);
   string output=Picture(root,"job-page-001.png",95),other=Picture(root,"job-page-002.png",96),before=Hash(pdf);
   IList<ConvertedFile> received=null;
   var complete=Await(Run(new[]{pdf},"png",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(pdf,output,other))),
    files=>{received=files;return Collected(files);}));
   check(complete.Total==1&&complete.Converted==2&&complete.Collected==2&&complete.Failed==0,"one PDF reports two generated and saved output files rather than one source-as-copy");
   check(complete.ConvertedSources==1&&complete.CollectedSources==1&&received.Count==1&&received[0].Outputs.SequenceEqual(new[]{output,other}),"multi-page job retains source grouping and page order while counting copies separately");
   check(Hash(pdf)==before&&complete.Message.Contains("2")&&complete.Message.Contains("原文件保留"),"multi-page completion communicates saved copies without modifying its PDF original");
   var partial=Await(Run(new[]{pdf},"png",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(pdf,output,other))),
    files=>Task.FromResult(new ConversionCollectionResult{CollectedPaths=new[]{output},Message="第二页未加入"})));
   check(partial.Converted==2&&partial.Collected==1&&partial.Failed==1&&partial.CollectedSources==0,"one uncollected PDF page reports one incomplete source and one genuinely saved page");
   check(partial.Message.Contains(root)&&File.Exists(output)&&File.Exists(other),"partly collected pages remain available and their retained output location is explained");
   using(var cancel=new CancellationTokenSource()){
    var cancelled=Await(Run(new[]{pdf,secondPdf},"png",root,(paths,target,destination,quality,progress,token)=>{
     cancel.Cancel();return Task.FromResult(new ConversionBatchResult{Cancelled=true,Files=new List<ConvertedFile>{Success(pdf,output,other)}});
    },null,null,cancel.Token));
    check(cancelled.Cancelled&&cancelled.Collected==2&&cancelled.CollectedSources==1&&cancelled.Remaining==1&&cancelled.Failed==0,"cancel remaining preserves every completed PDF page and counts the unstarted source separately");
   }
   int collections=0;
   var repeated=Await(Run(new[]{pdf},"png",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(pdf,output,output))),
    files=>{collections++;return Collected(files);}));
   check(repeated.Collected==0&&repeated.Failed==1&&collections==0,"the same page path repeated within one PDF result is not imported twice");
   var missing=Await(Run(new[]{pdf},"png",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(pdf,output,Path.Combine(root,"absent-page.png")))),
    files=>{collections++;return Collected(files);}));
   check(missing.Failed==1&&missing.Collected==0&&collections==0,"a missing later page cannot silently masquerade as a complete multi-page conversion");
   string outside=Picture(Path.Combine(root,"outside-results"),"foreign-page.png",97),narrow=Path.Combine(root,"strict-results");
   string valid=Picture(narrow,"valid-page.png",98);
   var escaped=Await(Run(new[]{pdf},"png",narrow,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(pdf,valid,outside))),
    files=>{collections++;return Collected(files);}));
   check(escaped.Collected==0&&escaped.Failed==1&&collections==0,"every output page is checked against the destination boundary, not only the first");
   var wrongFormat=Await(Run(new[]{pdf},"png",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(pdf,output,Picture(root,"wrong-page.jpg",99)))),
    files=>{collections++;return Collected(files);}));
   check(wrongFormat.Collected==0&&wrongFormat.Failed==1&&collections==0,"a wrong-format later page cannot bypass first-output validation");
  }
  static void MultipleOutputCollection(string root,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"identical-pages-library"))){
    string pdf=TwoPagePdf(root,"identical-original.pdf",true),externalHash=Hash(pdf),category=library.CreateFolder("多页项目","");
    var source=library.Import(pdf,category,"项目.v3","https://example.invalid/pages","多页标签","页顺序必须保留").Item;
    source.Favorite=true;source.HiddenFromAll=true;library.Update(source);
    string original=library.FullPath(source),originalHash=Hash(original),results=Path.Combine(root,"identical-page-results");
    string first=Picture(results,"项目.v3-001.png",100),second=Path.Combine(results,"项目.v3-002.png");File.Copy(first,second);
    var collector=new FileConversionCollectionSession(library,new[]{source},true);
    var outcome=Await(Run(new[]{original},"png",results,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(original,first,second))),collector.CollectAsync));
    var one=library.All().Single(asset=>asset.Id==collector.CollectedAssetId(first));var two=library.All().Single(asset=>asset.Id==collector.CollectedAssetId(second));
    check(outcome.Collected==2&&library.All().Count==3&&one.Id!=two.Id&&one.Id!=source.Id&&two.Id!=source.Id,"byte-identical pages become distinct managed records and never collapse into each other or their source");
    check(one.Name=="项目.v3-001"&&two.Name=="项目.v3-002"&&Hash(library.FullPath(one))==Hash(library.FullPath(two)),"identical-content pages retain separate stable numbered display names");
    check(new[]{one,two}.All(asset=>asset.Source==source.Source&&asset.Tags==source.Tags&&asset.Notes==source.Notes&&asset.Favorite&&asset.HiddenFromAll&&asset.Created==source.Created&&library.AssetFolders(asset.Id).Select(folder=>folder.Id).SequenceEqual(new[]{category})),"every PDF page inherits source metadata, visibility and categories");
    var again=Await(collector.CollectAsync(new[]{Success(original,second)}));
    check(again.CollectedPaths.SequenceEqual(new[]{second})&&library.All().Count==3&&library.All().Single(asset=>asset.Id==two.Id).Name=="项目.v3-002","retrying only page 002 neither duplicates a saved page nor renumbers it as 001");
    check(Hash(pdf)==externalHash&&Hash(original)==originalHash&&library.All().Any(asset=>asset.Id==source.Id&&!asset.Deleted&&asset.Ext==".pdf"),"multi-page conversion keeps the external PDF, managed original and original asset record unchanged");
   }
  }
  static void MetadataAndOriginals(string root,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"metadata-library"))){
    string sourceFile=Picture(root,"metadata-source.png",51),resultFile=Picture(Path.Combine(root,"metadata-results"),"Design.v2.jpg",52);
    string first=library.CreateFolder("项目一",""),second=library.CreateFolder("参考","");
    var source=library.Import(sourceFile,first,"Design.v2","https://example.invalid/original","原标签","原备注").Item;source.Favorite=true;source.HiddenFromAll=true;library.Update(source);
    library.AddToFolder(new[]{source.Id},second);string stored=library.FullPath(source),storedHash=Hash(stored),externalHash=Hash(sourceFile);
    var snapshot=new FileConversionCollectionSession(library,new[]{source},true);
    var job=Await(Run(new[]{stored},"jpg",Path.Combine(root,"metadata-results"),(paths,target,destination,quality,progress,token)=>{
     source.Tags="后来编辑";library.Update(source);return Task.FromResult(Batch(Success(stored,resultFile)));
    },snapshot.CollectAsync));
    var added=library.All().Single(asset=>asset.Id==snapshot.CollectedAssetId(resultFile));
    check(job.Collected==1&&library.All().Count==2&&added.Id!=source.Id,"quick conversion adds an independent asset and retains the original asset record");
    check(added.Name=="Design.v2"&&added.Ext==".jpg"&&added.Source=="https://example.invalid/original"&&added.Tags=="原标签"&&added.Notes=="原备注","new copy preserves dotted display name and frozen source URL, tags and notes");
    check(added.Favorite&&added.HiddenFromAll&&added.Created==source.Created,"converted copy retains source favorite, overview visibility and creation metadata");
    check(library.AssetFolders(added.Id).Select(folder=>folder.Id).OrderBy(id=>id).SequenceEqual(new[]{first,second}.OrderBy(id=>id)),"new converted copy automatically inherits every original media category");
    check(Hash(stored)==storedHash&&Hash(sourceFile)==externalHash&&library.All().Single(asset=>asset.Id==source.Id).Tags=="后来编辑","conversion leaves both external and stored original bytes and later source metadata edits untouched");
   }
   using(var library=new Library(Path.Combine(root,"shortcut-library"))){
    library.EnableReferenceAssets();string path=Picture(root,"external-shortcut.png",61),output=Picture(Path.Combine(root,"shortcut-results"),"shortcut-result.jpg",62),before=Hash(path),category=library.CreateFolder("本地项目","");
    var id=library.AddReferenceAssets(new[]{path},category).Single();var source=library.All().Single(asset=>asset.Id==id);var snapshot=new FileConversionCollectionSession(library,new[]{source},true);
    var result=Await(Run(new[]{path},"jpg",Path.Combine(root,"shortcut-results"),(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(path,output))),snapshot.CollectAsync));
    var added=library.All().Single(asset=>asset.Id==snapshot.CollectedAssetId(output));
    check(result.Collected==1&&!added.IsReference&&library.AssetFolders(added.Id).Any(folder=>folder.Id==category),"quick conversion of a local shortcut creates a managed result in the same category");
    check(Hash(path)==before&&library.All().Any(asset=>asset.Id==id&&asset.IsReference),"quick conversion never moves, replaces or removes the external shortcut original");
   }
   using(var library=new Library(Path.Combine(root,"keep-copy-library"))){
    string path=Picture(root,"keep-source.png",81),output=Picture(root,"keep-result.jpg",82),originCategory=library.CreateFolder("本次原分类",""),existingCategory=library.CreateFolder("已有副本分类","");
    var source=library.Import(path,originCategory,"新转换名","https://example.invalid/new","新标签","新备注").Item;
    var existing=library.Import(output,existingCategory,"已有不同名称","https://example.invalid/existing","已有标签","已有备注").Item;
    var snapshot=new FileConversionCollectionSession(library,new[]{source},true);
    var result=Await(Run(new[]{library.FullPath(source)},"jpg",root,(paths,target,destination,quality,progress,token)=>Task.FromResult(Batch(Success(paths[0],output))),snapshot.CollectAsync));
    var added=library.All().Single(asset=>asset.Id==snapshot.CollectedAssetId(output));
    check(result.Collected==1&&library.All().Count==3&&added.Id!=existing.Id&&added.Id!=source.Id,"quick conversion always creates a fresh copy even when its result bytes already exist in the library");
    check(library.AssetFolders(added.Id).Select(folder=>folder.Id).SequenceEqual(new[]{originCategory})&&added.Tags=="新标签"&&added.Source=="https://example.invalid/new","identical-content new copy still receives the converting source metadata and category");
    check(library.All().Single(asset=>asset.Id==existing.Id).Name=="已有不同名称"&&library.AssetFolders(existing.Id).Select(folder=>folder.Id).SequenceEqual(new[]{existingCategory}),"keeping a converted copy does not rename, reclassify or overwrite a pre-existing matching asset");
    var repeated=Await(snapshot.CollectAsync(new[]{Success(library.FullPath(source),output)}));
    check(repeated.CollectedPaths.SequenceEqual(new[]{output})&&library.All().Count==3&&snapshot.CollectedAssetId(output)==added.Id,"repeating quick collection inside one job is idempotent even though first collection explicitly keeps a copy");
   }
  }
  internal static void RunReal(string worker,string root,Action<bool,string> check){RunReal(worker,root,check,null);}
  internal static void RunReal(string worker,string root,Action<bool,string> check,Action<string> trace){
   if(!Path.IsPathRooted(worker)||!File.Exists(worker)||!Path.IsPathRooted(root)||Directory.Exists(root))throw new ArgumentException("Pass an actual built worker and a fresh absolute synthetic fixture directory.");
   Directory.CreateDirectory(root);
   var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   var previousTrace=realTrace;int previousWait=realWaitSeconds;
   realTrace=trace;realWaitSeconds=20;
   // An explicit opt-in is supplied only by the VM-guarded audit runner.
   // Do not silently relax the ordinary test budget or any production timeout.
   if(Environment.GetEnvironmentVariable("NOOK_VM_REAL_CONVERSION_WAIT_SECONDS")=="120")realWaitSeconds=120;
   TraceReal("REAL_CONFIG waitSeconds="+realWaitSeconds+" worker="+worker+" fixture="+root);
   try{
    string external=Picture(root,"real-product.v2.png",91),externalHash=Hash(external);
    using(var library=new Library(Path.Combine(root,"library"))){
     string category=library.CreateFolder("真实闭环验收","");
     var source=library.Import(external,category,"产品.v2","https://example.invalid/real","真实转换标签","只用合成图片").Item;
     source.Favorite=true;library.Update(source);string original=library.FullPath(source),storedHash=Hash(original);
     string sessionRoot=Path.Combine(root,"sessions"),destination=Path.Combine(library.Root,".mori","conversions");
     foreach(string target in new[]{"jpg","webp","bmp","tiff","gif","pdf"}){
      int before=library.All().Count;var collector=new FileConversionCollectionSession(library,new[]{source},true);
      var client=new FileConversionClient(worker,sessionRoot,null,Path.Combine(root,"components")){SourceName=path=>"产品.v2"};
      var locks=MainWindow.OpenQuickInputReadLocks(new[]{original},CancellationToken.None);
      QuickConversionOutcome outcome;
      try{
       bool held=false;try{using(var writer=new FileStream(original,FileMode.Open,FileAccess.Write,FileShare.Read)){} }catch(IOException){held=true;}
       check(held,"real "+target+" quick action holds the original stable before worker decoding");
       outcome=RealJob(client,collector,new[]{original},target,destination);
       held=false;try{using(var writer=new FileStream(original,FileMode.Open,FileAccess.Write,FileShare.Read)){} }catch(IOException){held=true;}
       check(held,"real "+target+" conversion and collection complete while the original read lock is still held");
      }finally{foreach(var stream in locks)stream.Dispose();}
      check(outcome.Converted==1&&outcome.Collected==1&&outcome.Failed==0&&!outcome.Cancelled,"real PNG to "+target.ToUpperInvariant()+" passes validator, hidden worker and automatic new-copy collector");
      var added=library.All().Single(asset=>asset.Id!=source.Id&&asset.Ext=="."+target);string stored=library.FullPath(added);
      check(library.All().Count==before+1&&File.Exists(stored)&&ConversionFormats.Detect(stored)==target,"real "+target+" result exists in the library and matches its requested file format");
      check(added.Name=="产品.v2"&&added.Source==source.Source&&added.Tags==source.Tags&&added.Notes==source.Notes&&added.Favorite&&library.AssetFolders(added.Id).Select(folder=>folder.Id).SequenceEqual(new[]{category}),"real "+target+" result keeps readable dotted name, source metadata and original category");
      if(target=="jpg")using(var decoded=System.Drawing.Image.FromFile(stored))check(decoded.Width==48&&decoded.Height==32,"real quick JPG retains source pixel dimensions");
      check(Hash(external)==externalHash&&Hash(original)==storedHash,"real "+target+" conversion preserves external and managed original hashes");
      check(!Directory.Exists(sessionRoot)||!Directory.EnumerateFileSystemEntries(sessionRoot).Any(),"real "+target+" conversion releases and cleans its temporary worker session");
      using(var writer=new FileStream(original,FileMode.Open,FileAccess.Write,FileShare.None))check(writer.CanWrite,"real "+target+" quick action releases source lock when finished");
     }
     int count=library.All().Count,engineCalls=0;
     var same=Await(QuickConversionJob.RunAsync(new[]{original},"png",destination,
      (path,token)=>Task.FromResult(ConversionFormats.Detect(path)),
      (paths,target,folder,quality,progress,token)=>{engineCalls++;throw new Exception("same-format real workflow must not start worker");},
      files=>{throw new Exception("same-format real workflow must not import");},null,CancellationToken.None));
     check(same.Skipped==1&&same.Collected==0&&engineCalls==0&&library.All().Count==count,"real PNG same-format action creates no worker or extra asset");
     check(library.All().Any(asset=>asset.Id==source.Id)&&Hash(original)==storedHash,"real closed-loop conversions leave the original asset accessible and byte-identical");
     var sameTarget=library.All().Single(asset=>asset.Ext==".jpg");string samePath=library.FullPath(sameTarget),sameHash=Hash(samePath);
     var mixedCollector=new FileConversionCollectionSession(library,new[]{source,sameTarget},true);
     var mixedClient=new FileConversionClient(worker,sessionRoot,null,Path.Combine(root,"components")){SourceName=path=>"混合选择"};
     var mixedLocks=MainWindow.OpenQuickInputReadLocks(new[]{original,samePath},CancellationToken.None);
     QuickConversionOutcome mixed;
     try{mixed=RealJob(mixedClient,mixedCollector,new[]{original,samePath},"jpg",destination);}
     finally{foreach(var stream in mixedLocks)stream.Dispose();}
     check(mixed.Total==2&&mixed.Converted==1&&mixed.Collected==1&&mixed.Skipped==1&&mixed.Failed==0,"real PNG plus already-JPG batch converts only PNG and skips JPG without disabling the shared target");
     check(Hash(original)==storedHash&&Hash(samePath)==sameHash&&library.All().Count==count+1,"mixed same-target conversion creates one independent result and preserves both original selected files");
    }
    RealPdfPages(worker,Path.Combine(root,"different-pages"),false,check);
    RealPdfPages(worker,Path.Combine(root,"identical-pages"),true,check);
    RealFrameSources(worker,Path.Combine(root,"frame-sources"),check);
    check(Application.Current==null,"real conversion closed-loop uses no Application, window, picker, clipboard or personal data");
   }finally{realTrace=previousTrace;realWaitSeconds=previousWait;SynchronizationContext.SetSynchronizationContext(previous);}
  }
  static void TraceReal(string message){if(realTrace!=null)realTrace(message);}
  static T AwaitReal<T>(Func<Task<T>> start,string phase){
   var elapsed=System.Diagnostics.Stopwatch.StartNew();
   TraceReal("PHASE START "+phase+" budgetSeconds="+realWaitSeconds);
   try{
    var task=start();
    while(!task.IsCompleted){if(elapsed.Elapsed.TotalSeconds>realWaitSeconds)throw new TimeoutException("Real conversion phase timed out: "+phase+"; budgetSeconds="+realWaitSeconds+"; elapsedMs="+elapsed.ElapsedMilliseconds);Drain();Thread.Sleep(1);}
    var result=task.GetAwaiter().GetResult();Drain();
    TraceReal("PHASE END "+phase+" elapsedMs="+elapsed.ElapsedMilliseconds);return result;
   }catch(Exception error){TraceReal("PHASE ERROR "+phase+" elapsedMs="+elapsed.ElapsedMilliseconds+" type="+error.GetType().Name+" message="+error.Message);throw;}
  }
  static QuickConversionOutcome RealJob(FileConversionClient client,FileConversionCollectionSession collector,string[] inputs,string target,string destination){
   string label="target="+target+" source="+string.Join(",",inputs.Select(Path.GetFileName));
   var capabilities=AwaitReal(()=>client.GetCapabilitiesAsync(inputs,CancellationToken.None),"capabilities "+label);
   var formats=MainWindow.ValidateQuickConversionCapabilities(inputs,target,capabilities);
   return AwaitReal(()=>QuickConversionJob.RunAsync(inputs,target,destination,
    (path,token)=>{
     token.ThrowIfCancellationRequested();FileConversionClient.ValidateInputs(new[]{path});return Task.FromResult(formats[path]);
    },client.ConvertAsync,collector.CollectAsync,null,CancellationToken.None),"convert-collect "+label);
  }
  static T AwaitDocumentEngine<T>(Task<T> task){
   var until=DateTime.UtcNow.AddSeconds(60);
   while(!task.IsCompleted){if(DateTime.UtcNow>until)throw new TimeoutException("Document conversion background test exceeded 60 seconds.");Drain();Thread.Sleep(1);}
   var result=task.GetAwaiter().GetResult();Drain();return result;
  }
  static string PackageFixture(string root,string name,Dictionary<string,string> entries){
   Directory.CreateDirectory(root);string path=Path.Combine(root,name);
   using(var file=File.Create(path))using(var zip=new System.IO.Compression.ZipArchive(file,System.IO.Compression.ZipArchiveMode.Create)){
    foreach(var pair in entries){
     var entry=zip.CreateEntry(pair.Key,System.IO.Compression.CompressionLevel.NoCompression);
     using(var output=entry.Open())using(var writer=new StreamWriter(output,new UTF8Encoding(false)))writer.Write(pair.Value);
    }
   }
   return path;
  }
  static string OfficeFixture(string root){
   return PackageFixture(root,"产品说明.v2.docx",new Dictionary<string,string>{
    {"[Content_Types].xml","<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"},
    {"_rels/.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"},
    {"word/document.xml","<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Nook Office first page</w:t></w:r></w:p><w:p><w:r><w:br w:type=\"page\"/></w:r></w:p><w:p><w:r><w:t>Nook Office second page</w:t></w:r></w:p><w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr></w:body></w:document>"}
   });
  }
  static string EbookFixture(string root){
   return PackageFixture(root,"产品手册.epub",new Dictionary<string,string>{
    {"mimetype","application/epub+zip"},
    {"META-INF/container.xml","<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>"},
    {"OEBPS/content.opf","<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" unique-identifier=\"book-id\" version=\"2.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Nook synthetic manual</dc:title><dc:identifier id=\"book-id\">urn:uuid:71fdbd96-42c4-473a-8ee0-9df526134890</dc:identifier><dc:language>en</dc:language><dc:creator>Nook test</dc:creator></metadata><manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"chapter\"/></spine></package>"},
    {"OEBPS/chapter.xhtml","<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Nook manual</title></head><body><h1>Nook manual</h1><p>This generated ebook exists only to verify independent conversion and original preservation.</p></body></html>"},
    {"OEBPS/toc.ncx","<?xml version=\"1.0\"?><ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\"><head><meta name=\"dtb:uid\" content=\"urn:uuid:71fdbd96-42c4-473a-8ee0-9df526134890\"/></head><docTitle><text>Nook manual</text></docTitle><navMap><navPoint id=\"chapter\" playOrder=\"1\"><navLabel><text>Manual</text></navLabel><content src=\"chapter.xhtml\"/></navPoint></navMap></ncx>"}
   });
  }
  static string SpreadsheetFixture(string root){
   return PackageFixture(root,"库存清单.xlsx",new Dictionary<string,string>{
    {"[Content_Types].xml","<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>"},
    {"_rels/.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"},
    {"xl/workbook.xml","<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Inventory\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"},
    {"xl/_rels/workbook.xml.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"},
    {"xl/worksheets/sheet1.xml","<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Product</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>Quantity</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Nook</t></is></c><c r=\"B2\"><v>7</v></c></row><row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Desk</t></is></c><c r=\"B3\"><v>12</v></c></row></sheetData></worksheet>"}
   });
  }
  static QuickConversionOutcome DocumentJob(FileConversionClient client,FileConversionCollectionSession collector,string source,string target,string destination){
   var capabilities=AwaitDocumentEngine(client.GetCapabilitiesAsync(new[]{source},CancellationToken.None));
   var formats=MainWindow.ValidateQuickConversionCapabilities(new[]{source},target,capabilities);
   var locks=MainWindow.OpenQuickInputReadLocks(new[]{source},CancellationToken.None);
   try{return AwaitDocumentEngine(QuickConversionJob.RunAsync(new[]{source},target,destination,
    (path,token)=>Task.FromResult(formats[path]),client.ConvertAsync,collector.CollectAsync,null,CancellationToken.None));}
   finally{foreach(var stream in locks)stream.Dispose();}
  }
  internal static void RunDocuments(string worker,string root,string group,Action<bool,string> check){
   if(group!="office"&&group!="ebooks"&&group!="all")throw new ArgumentException("Document group must be office, ebooks or all.");
   Directory.CreateDirectory(root);var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
   try{
    if(group!="ebooks"){RealOfficeDocuments(worker,Path.Combine(root,"office"),check);RealSpreadsheets(worker,Path.Combine(root,"spreadsheets"),check);}
    if(group!="office")RealEbooks(worker,Path.Combine(root,"ebooks"),check);
    check(Application.Current==null,"real Office and ebook closed-loop never creates an Application, visible window, clipboard or system application");
   }finally{SynchronizationContext.SetSynchronizationContext(previous);}
  }
  static void RealOfficeDocuments(string worker,string root,Action<bool,string> check){
   string external=OfficeFixture(root),before=Hash(external),sessions=Path.Combine(root,"sessions"),destination=Path.Combine(root,"results");
   using(var library=new Library(Path.Combine(root,"library"))){
    library.EnableReferenceAssets();string category=library.CreateFolder("Office项目","");
    string id=library.AddReferenceAssets(new[]{external},category).Single();var source=library.All().Single(asset=>asset.Id==id);
    source.Tags="原始文档标签";source.Notes="保留原始文档";source.Favorite=true;library.Update(source);
    var client=new FileConversionClient(worker,sessions,null,Path.Combine(root,"components")){SourceName=path=>"产品说明.v2"};
    var capabilities=AwaitDocumentEngine(client.GetCapabilitiesAsync(new[]{external},CancellationToken.None));
    check(capabilities.Files.Single().Targets.Contains("doc")&&capabilities.Targets.Any(target=>target.Id=="doc"),"real Office shortcut exposes DOC when the bundled LibreOffice engine is present");
    var first=DocumentJob(client,new FileConversionCollectionSession(library,new[]{source},true),external,"doc",destination);
    var document=library.All().SingleOrDefault(asset=>asset.Ext==".doc"&&!asset.IsReference);
    check(first.Collected==1&&first.Failed==0&&document!=null,"real DOCX to DOC quick action collects an independent managed result: "+first.Message);
    if(document==null)return;
    string originalDoc=library.FullPath(document),docHash=Hash(originalDoc);
    check(File.ReadAllBytes(originalDoc).Take(8).SequenceEqual(new byte[]{0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1}),"real DOC result has the legacy Office compound-file header, not renamed DOCX");
    var second=DocumentJob(client,new FileConversionCollectionSession(library,new[]{document},true),originalDoc,"pdf",destination);
    var pdf=library.All().SingleOrDefault(asset=>asset.Ext==".pdf");
    check(second.Collected==1&&second.Failed==0&&pdf!=null,"real collected DOC to PDF uses the same one-click pipeline: "+second.Message);
    if(pdf!=null){
     using(var parsed=UglyToad.PdfPig.PdfDocument.Open(library.FullPath(pdf))){
      check(parsed.NumberOfPages==2,"real LibreOffice output retains both generated document pages");
      check(parsed.NumberOfPages>=2&&parsed.GetPage(1).Text.Contains("Nook Office first page")&&parsed.GetPage(2).Text.Contains("Nook Office second page"),"real Office PDF preserves distinct first and second page text");
     }
     check(pdf.Tags==source.Tags&&pdf.Notes==source.Notes&&pdf.Favorite&&library.AssetFolders(pdf.Id).Any(folder=>folder.Id==category)&&MediaTypes.Contains(pdf,"documents"),"real Office results preserve classification, tags and favourite through both conversions");
    }
    check(Hash(external)==before&&Hash(originalDoc)==docHash&&library.All().Count==3&&library.All().Any(asset=>asset.Id==id&&asset.IsReference),"Office conversion preserves original DOCX, intermediate DOC and all three independently accessible records");
    check(!Directory.Exists(sessions)||!Directory.EnumerateFileSystemEntries(sessions).Any(),"Office quick conversion cleans each staging session");
    using(var writable=new FileStream(external,FileMode.Open,FileAccess.Write,FileShare.None))check(writable.CanWrite,"Office shortcut read lock is released after conversion");
   }
  }
  static void RealEbooks(string worker,string root,Action<bool,string> check){
   string external=EbookFixture(root),before=Hash(external),sessions=Path.Combine(root,"sessions"),destination=Path.Combine(root,"results");
   using(var library=new Library(Path.Combine(root,"library"))){
    library.EnableReferenceAssets();string category=library.CreateFolder("阅读资料","");
    string id=library.AddReferenceAssets(new[]{external},category).Single();var source=library.All().Single(asset=>asset.Id==id);
    source.Tags="电子书标签";source.Notes="本地原件";library.Update(source);
    var client=new FileConversionClient(worker,sessions,null,Path.Combine(root,"components")){SourceName=path=>"产品手册"};
    var capabilities=AwaitDocumentEngine(client.GetCapabilitiesAsync(new[]{external},CancellationToken.None));
    check(capabilities.Files.Single().Targets.Contains("azw3")&&capabilities.Targets.Any(target=>target.Id=="mobi"),"real EPUB capabilities expose AZW3 and MOBI only with bundled Calibre");
    var converted=DocumentJob(client,new FileConversionCollectionSession(library,new[]{source},true),external,"azw3",destination);
    var ebook=library.All().SingleOrDefault(asset=>asset.Ext==".azw3");
    check(converted.Collected==1&&converted.Failed==0&&ebook!=null,"real EPUB to AZW3 produces an independently collected ebook: "+converted.Message);
    if(ebook!=null){
     var header=File.ReadAllBytes(library.FullPath(ebook));
     check(header.Length>68&&Encoding.ASCII.GetString(header,60,8)=="BOOKMOBI","real AZW3 output contains a Kindle/Mobi container header");
     check(!ebook.IsReference&&MediaTypes.Contains(ebook,"documents")&&ebook.Tags==source.Tags&&library.AssetFolders(ebook.Id).Any(folder=>folder.Id==category),"real converted ebook remains findable in documents and inherits original category");
    }
    int count=library.All().Count;
    using(var panel=new QuickFileConversionPanel(true,client.GetCapabilitiesAsync,client.ConvertAsync,Path.Combine(root,"external-results"))){
     AwaitDocumentEngine(panel.AddFilesAsync(new[]{external}).ContinueWith(task=>{task.GetAwaiter().GetResult();return true;}));
     var choices=(ConversionTarget[])typeof(QuickFileConversionPanel).GetMethod("Choices",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(panel,null);
     check(choices.Any(target=>target.Id=="mobi")&&panel.FormatChoices.Children.OfType<Button>().Any(button=>button.Tag is string),"external EPUB file retains MOBI in its actual format menu without importing");
     AwaitDocumentEngine(panel.StartAsync("mobi").ContinueWith(task=>{task.GetAwaiter().GetResult();return true;}));
     check(panel.ResultPaths.Length==1&&Path.GetExtension(panel.ResultPaths[0])==".mobi"&&File.Exists(panel.ResultPaths[0]),"external EPUB one-click MOBI conversion keeps a real result file: "+panel.Status.Text);
     if(panel.ResultPaths.Length==1){var bytes=File.ReadAllBytes(panel.ResultPaths[0]);check(bytes.Length>68&&Encoding.ASCII.GetString(bytes,60,8)=="BOOKMOBI","external MOBI result is decoded by its real container signature");}
    }
    check(library.All().Count==count&&Hash(external)==before&&library.All().Any(asset=>asset.Id==id&&asset.IsReference),"external ebook conversion does not import or replace the user's source or prior library result");
    check(!Directory.Exists(sessions)||!Directory.EnumerateFileSystemEntries(sessions).Any(),"ebook conversions clean all owned staging sessions");
    using(var writable=new FileStream(external,FileMode.Open,FileAccess.Write,FileShare.None))check(writable.CanWrite,"ebook input read lock is released after both conversions");
   }
  }
  static void RealSpreadsheets(string worker,string root,Action<bool,string> check){
   string external=SpreadsheetFixture(root),before=Hash(external),sessions=Path.Combine(root,"sessions"),destination=Path.Combine(root,"results");
   using(var library=new Library(Path.Combine(root,"library"))){
    library.EnableReferenceAssets();string category=library.CreateFolder("表格资料","");
    string id=library.AddReferenceAssets(new[]{external},category).Single();var source=library.All().Single(asset=>asset.Id==id);
    source.Tags="库存标签";source.Notes="独立表格结果";library.Update(source);
    var client=new FileConversionClient(worker,sessions,null,Path.Combine(root,"components")){SourceName=path=>"库存清单"};
    var capabilities=AwaitDocumentEngine(client.GetCapabilitiesAsync(new[]{external},CancellationToken.None));
    check(capabilities.Files.Single().Targets.Contains("xls"),"real spreadsheet shortcut advertises XLS with bundled LibreOffice");
    var converted=DocumentJob(client,new FileConversionCollectionSession(library,new[]{source},true),external,"xls",destination);
    var table=library.All().SingleOrDefault(asset=>asset.Ext==".xls");
    check(converted.Collected==1&&converted.Failed==0&&table!=null,"real XLSX to XLS collects an independent spreadsheet: "+converted.Message);
    if(table==null)return;
    string oldTable=library.FullPath(table),tableHash=Hash(oldTable);
    check(File.ReadAllBytes(oldTable).Take(8).SequenceEqual(new byte[]{0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1}),"real XLS result has a binary Office header, not a renamed modern spreadsheet");
    var exported=DocumentJob(client,new FileConversionCollectionSession(library,new[]{table},true),oldTable,"csv",destination);
    var csv=library.All().SingleOrDefault(asset=>asset.Ext==".csv");
    check(exported.Collected==1&&exported.Failed==0&&csv!=null,"real XLS to CSV keeps the same one-click host flow: "+exported.Message);
    if(csv!=null){
     var rows=File.ReadAllLines(library.FullPath(csv)).Where(line=>!string.IsNullOrWhiteSpace(line)).ToArray();
     var cells=rows.Select(line=>string.Join("|",line.Split(',').Select(cell=>cell.Trim().Trim('"')))).ToArray();
     check(cells.SequenceEqual(new[]{"Product|Quantity","Nook|7","Desk|12"}),"real spreadsheet conversions preserve exact headers, row order and all generated cell values");
     check(csv.Tags==source.Tags&&csv.Notes==source.Notes&&library.AssetFolders(csv.Id).Any(folder=>folder.Id==category)&&MediaTypes.Contains(csv,"documents"),"real CSV output inherits original spreadsheet classification and metadata");
    }
    check(Hash(external)==before&&Hash(oldTable)==tableHash&&library.All().Count==3&&library.All().Any(asset=>asset.Id==id&&asset.IsReference),"spreadsheet conversion retains original XLSX and XLS alongside separate CSV output");
    check(!Directory.Exists(sessions)||!Directory.EnumerateFileSystemEntries(sessions).Any(),"spreadsheet conversions clean all owned staging sessions");
    using(var writable=new FileStream(external,FileMode.Open,FileAccess.Write,FileShare.None))check(writable.CanWrite,"spreadsheet source read lock is released");
   }
  }
  static void RealPdfPages(string worker,string root,bool identical,Action<bool,string> check){
   string external=TwoPagePdf(root,"项目.v2.pdf",identical),externalHash=Hash(external);
   using(var library=new Library(Path.Combine(root,"library"))){
    string category=library.CreateFolder("PDF分页项目","");
    var source=library.Import(external,category,"项目.v2","https://example.invalid/pdf","页标签","两页保持").Item;
    source.Favorite=true;source.HiddenFromAll=true;library.Update(source);
    string original=library.FullPath(source),before=Hash(original),sessions=Path.Combine(root,"sessions"),destination=Path.Combine(root,"results");
    var collector=new FileConversionCollectionSession(library,new[]{source},true);
    var client=new FileConversionClient(worker,sessions,null,Path.Combine(root,"components")){SourceName=path=>"项目.v2"};
    var locks=MainWindow.OpenQuickInputReadLocks(new[]{original},CancellationToken.None);
    QuickConversionOutcome outcome;
    try{outcome=RealJob(client,collector,new[]{original},"png",destination);}
    finally{foreach(var stream in locks)stream.Dispose();}
    string description=identical?"identical-content PDF":"two-colour PDF";
    check(outcome.Total==1&&outcome.Converted==2&&outcome.Collected==2&&outcome.CollectedSources==1&&outcome.Failed==0,"real "+description+" produces and collects both PNG pages through the quick pipeline: "+outcome.Message);
    var pages=library.All().Where(asset=>asset.Id!=source.Id).OrderBy(asset=>asset.Name,StringComparer.Ordinal).ToArray();
    check(pages.Length==2&&pages[0].Id!=pages[1].Id&&pages.Select(asset=>asset.Name).SequenceEqual(new[]{"项目.v2-001","项目.v2-002"}),"real "+description+" keeps independently addressable numbered page records");
    check(pages.All(asset=>asset.Source==source.Source&&asset.Tags==source.Tags&&asset.Notes==source.Notes&&asset.Favorite&&asset.HiddenFromAll&&asset.Created==source.Created&&library.AssetFolders(asset.Id).Select(folder=>folder.Id).SequenceEqual(new[]{category})),"real "+description+" transfers metadata and category to each page");
    if(pages.Length==2){
     using(var first=new System.Drawing.Bitmap(library.FullPath(pages[0])))
     using(var second=new System.Drawing.Bitmap(library.FullPath(pages[1]))){
      var one=first.GetPixel(first.Width/2,first.Height/2);var two=second.GetPixel(second.Width/2,second.Height/2);
      check(one.R>240&&one.B<10&&(identical?two.R>240&&two.B<10:two.B>240&&two.R<10),"real "+description+" numbered order matches decoded page pixels");
      // PNG encoders may emit different ancillary metadata for identical pages.
      if(identical)check(SameDecodedPixels(first,second),"real identical pages remain separate and retain identical complete decoded pixels");
     }
    }
    check(Hash(external)==externalHash&&Hash(original)==before&&library.All().Any(asset=>asset.Id==source.Id&&!asset.Deleted),"real "+description+" preserves both original file copies and its source record");
    check(!Directory.Exists(sessions)||!Directory.EnumerateFileSystemEntries(sessions).Any(),"real "+description+" cleans worker staging after page collection");
    using(var writer=new FileStream(original,FileMode.Open,FileAccess.Write,FileShare.None))check(writer.CanWrite,"real "+description+" releases its original read lock");
   }
  }
  static void RealFrameSources(string worker,string root,Action<bool,string> check){
   foreach(string kind in new[]{"gif","tiff","psd","apng-png"}){
    string scenario=Path.Combine(root,kind),external=kind=="psd"?LayeredPsd(scenario):kind=="apng-png"?TwoFrameApng(scenario):TwoFrameImage(scenario,"two-frames."+kind),before=Hash(external);
    using(var library=new Library(Path.Combine(scenario,"library"))){
     string category=library.CreateFolder("帧与合成图","");
     var source=library.Import(external,category,kind+"-source","https://example.invalid/"+kind,"原标签","保留原文件").Item;
     string original=library.FullPath(source),originalHash=Hash(original),sessions=Path.Combine(scenario,"sessions"),destination=Path.Combine(scenario,"results");
     var collector=new FileConversionCollectionSession(library,new[]{source},true);
     var client=new FileConversionClient(worker,sessions,null,Path.Combine(scenario,"components")){SourceName=path=>kind+"-source"};
     var caps=AwaitReal(()=>client.GetCapabilitiesAsync(new[]{original},CancellationToken.None),"frame-capabilities source="+kind);
     string target=kind=="apng-png"?"jpg":"png",sourceFormat=kind=="apng-png"?"png":kind;
     check(caps.Files.Length==1&&caps.Files[0].Format==sourceFormat&&caps.Files[0].Targets.Contains(target),"real "+kind+" source advertises "+target+" through actual worker capabilities");
     if(kind=="psd")check(caps.Files[0].Warnings.Any(warning=>warning.Contains("图层")||warning.Contains("合成")),"Photoshop quick conversion explains its composite-only layer limitation before conversion");
     var locks=MainWindow.OpenQuickInputReadLocks(new[]{original},CancellationToken.None);
     QuickConversionOutcome outcome;
     try{outcome=RealJob(client,collector,new[]{original},target,destination);}
     finally{foreach(var stream in locks)stream.Dispose();}
     int expected=kind=="psd"?1:2;
     var added=library.All().Where(asset=>asset.Id!=source.Id).OrderBy(asset=>asset.Name,StringComparer.Ordinal).ToArray();
     check(outcome.Collected==expected&&outcome.Failed==0&&added.Length==expected,"real "+kind+" to "+target+" preserves all frames or exactly the advertised Photoshop composite: "+outcome.Message);
     check(added.All(asset=>asset.Tags==source.Tags&&asset.Source==source.Source&&library.AssetFolders(asset.Id).Any(folder=>folder.Id==category)),"real "+kind+" outputs retain source metadata and classification");
     if(added.Length==expected){
      using(var image=new System.Drawing.Bitmap(library.FullPath(added[0]))){
       var colour=image.GetPixel(image.Width/2,image.Height/2);
       check(kind=="psd"?colour.G>240&&colour.R<10:colour.R>240&&colour.B<10,"real "+kind+" first output is the actual first frame or stored composite");
      }
      if(expected==2)using(var image=new System.Drawing.Bitmap(library.FullPath(added[1]))){
       var colour=image.GetPixel(image.Width/2,image.Height/2);
       check(colour.B>240&&colour.R<10,"real "+kind+" second frame is not lost or duplicated from frame one");
      }
     }
     if(kind=="gif"){
      var animationCollector=new FileConversionCollectionSession(library,new[]{source},true);
      var animation=RealJob(client,animationCollector,new[]{original},"webp",destination);
      var webp=library.All().SingleOrDefault(asset=>asset.Ext==".webp");
      check(webp!=null&&animation.Collected==1,"animated GIF to WebP produces a new animated asset: "+animation.Message);
      if(webp!=null)using(var frames=new ImageMagick.MagickImageCollection(library.FullPath(webp))){
        check(frames.Count==2,"animated GIF to WebP preserves both frames");
        check(frames.Count==2&&MagickDelay(frames,0)==12&&MagickDelay(frames,1)==24,"animated GIF to WebP retains the original frame delays");
       }
     }
     check(Hash(external)==before&&Hash(original)==originalHash&&library.All().Any(asset=>asset.Id==source.Id&&!asset.Deleted),"real "+kind+" conversions preserve source bytes and the original record");
     check(!Directory.Exists(sessions)||!Directory.EnumerateFileSystemEntries(sessions).Any(),"real "+kind+" conversion cleans every hidden worker session");
    }
   }
  }
 }
}
