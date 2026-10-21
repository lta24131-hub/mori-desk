using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;

namespace MoriDesk {
 // Detached controls and injected services only: never create a Window or
 // Application, use the clipboard, invoke a picker or write to a real library.
 internal static class FileConversionUiTests {
  static ConversionCapabilities Caps(params string[] names){return new ConversionCapabilities{Targets=names.Select(n=>new ConversionTarget{Id=n,Label=n.ToUpperInvariant(),Extension="."+n}).ToArray()};}
  static ConvertedFile Success(string source,string output){return new ConvertedFile{Source=source,Outputs=new[]{output},Error=""};}
  static ConvertedFile Failure(string source,string error){return new ConvertedFile{Source=source,Outputs=new string[0],Error=error};}
  static ConversionBatchResult Result(string source,string output,bool cancelled=false){return new ConversionBatchResult{Cancelled=cancelled,Files=new List<ConvertedFile>{output==null?Failure(source,"Synthetic failure"):Success(source,output)}};}
  static Task<ConversionBatchResult> NeverConvert(string[] inputs,string target,string directory,int quality,IProgress<ConversionProgress> progress,CancellationToken token){throw new Exception("This test must never convert files");}
  static void Drain(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static void Pump(Task task){DateTime deadline=DateTime.UtcNow.AddSeconds(5);while(!task.IsCompleted){if(DateTime.UtcNow>deadline)throw new TimeoutException("Offscreen conversion test timed out");Drain();Thread.Sleep(1);}task.GetAwaiter().GetResult();Drain();}
  static void Until(Func<bool> condition){DateTime deadline=DateTime.UtcNow.AddSeconds(5);while(!condition()){if(DateTime.UtcNow>deadline)throw new TimeoutException("Offscreen state transition timed out");Drain();Thread.Sleep(1);}}
  static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));Drain();}
  static bool ParentAreaVisible(FrameworkElement element){return element.Parent is FrameworkElement&&((FrameworkElement)element.Parent).Parent is FrameworkElement&&((FrameworkElement)((FrameworkElement)element.Parent).Parent).Visibility==Visibility.Visible;}
  static void WaitForThumbnail(FileConversionPanel panel){DateTime deadline=DateTime.UtcNow.AddSeconds(3);while(!Children<Image>(panel.SingleFileCard).Any(image=>image.Source!=null)&&DateTime.UtcNow<deadline){Drain();Thread.Sleep(1);}}
  static void Style(FileConversionPanel panel){panel.Resources["Canvas"]=Brushes.White;panel.Resources["Surface"]=Brushes.White;panel.Resources["Input"]=Brushes.WhiteSmoke;panel.Resources["Text"]=Brushes.Black;panel.Resources["Muted"]=Brushes.Gray;panel.Resources["Control"]=Brushes.WhiteSmoke;panel.Resources["Border"]=Brushes.LightGray;panel.Resources["Primary"]=Brushes.MediumPurple;panel.Resources["OnPrimary"]=Brushes.White;panel.Resources["Accent"]=Brushes.MediumPurple;panel.Resources["AccentSoft"]=Brushes.Lavender;panel.Resources["ErrorText"]=Brushes.DarkRed;}
  static string Fixture(string root,string name){string path=Path.Combine(root,name);File.WriteAllText(path,"SYNTHETIC UI FIXTURE, NOT A REAL IMAGE");return path;}
  static string Picture(string root,string name,int width=160,int height=120){
   // Synthetic pixels exercise real thumbnail decoding without personal media,
   // a graphics engine or a visible rendering surface.
   string path=Path.Combine(root,name);var pixels=new byte[width*height*4];
   for(int y=0;y<height;y++)for(int x=0;x<width;x++){int i=(y*width+x)*4;bool tile=(x/20+y/20)%2==0;pixels[i]=(byte)(tile?209:243);pixels[i+1]=(byte)(tile?202:241);pixels[i+2]=(byte)(tile?184:238);pixels[i+3]=255;if(x>52&&x<108&&y>22&&y<103){pixels[i]=184;pixels[i+1]=139;pixels[i+2]=126;}}
   var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);bitmap.Freeze();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))encoder.Save(stream);return path;
  }
  internal static void Run(string root,Action<bool,string> check){
   if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Conversion UI tests require STA");
   string fixtures=Path.Combine(root,"file-conversion-ui");Directory.CreateDirectory(fixtures);
   var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{PassiveAndInputs(fixtures,check);ProbeRaces(fixtures,check);SingleAndBatch(fixtures,check);ConversionAndCollection(fixtures,check);RetryFailedOnly(fixtures,check);RetryCollection(fixtures,check);PartialCollectionAndResultActions(fixtures,check);BoundedPreviewsAndWarnings(fixtures,check);PagedResults(fixtures,check);PagedFailures(fixtures,check);MovedUncollectedOutput(fixtures,check);CancelledChooserAndMovedResult(fixtures,check);CancellationAndCollapse(fixtures,check);ErrorsAndDispose(fixtures,check);SelectionGuards(check);RenderPreviews(fixtures,check);RenderResults(fixtures,check);}
   finally{SynchronizationContext.SetSynchronizationContext(previous);}
  }
  static void PassiveAndInputs(string root,Action<bool,string> check){
   string input=Picture(root,"first.png");int calls=0;
   using(var missing=new FileConversionPanel(false,(files,token)=>{calls++;return Task.FromResult(Caps("png"));},NeverConvert,null,Path.Combine(root,"not-created"))){
    check(calls==0&&!missing.IsBusy&&!missing.Start.IsEnabled&&missing.Status.Text.Contains("组件未安装"),"opening absent-engine panel is passive and clearly explains missing component");
    Pump(missing.AddFilesAsync(new[]{input}));check(calls==0&&!missing.Start.IsEnabled&&!Directory.Exists(missing.Output.Text),"absent component never probes, creates an output folder or pretends success");
   }
   using(var panel=new FileConversionPanel(true,(files,token)=>{calls++;return Task.FromResult(Caps("png","jpg","webp","pdf","svg"));},NeverConvert,null,Path.Combine(root,"not-created"))){
    Style(panel);check(!panel.Collect.IsEnabled&&panel.Collect.IsChecked==false&&!panel.Start.IsEnabled&&panel.Open.Visibility==Visibility.Collapsed,"standalone empty panel has no fabricated collector or completed result");
    Pump(panel.AddFilesAsync(new[]{input,input.ToUpperInvariant(),root,Path.Combine(root,"missing.png")}));
    check(panel.Inputs.Length==1&&panel.Start.IsEnabled,"input validation accepts only distinct existing files, never expands folders");
    check(panel.Status.Text.Contains("忽略 2")&&panel.Status.Text.Contains("重复"),"invalid paths, folders and duplicates have an explanation");
    check(panel.Target.Items.Count==4&&!panel.Target.Items.Cast<ConversionTarget>().Any(t=>t.Id=="svg"),"worker capabilities cannot expose unsupported first-version targets");
    check(!panel.Quality.IsEnabled&&!ParentAreaVisible(panel.Quality),"lossless PNG has no misleading quality control");
    panel.Target.SelectedItem=panel.Target.Items.Cast<ConversionTarget>().Single(t=>t.Id=="jpg");
    check(panel.Quality.IsEnabled&&ParentAreaVisible(panel.Quality)&&panel.Warning.Text.Contains("透明"),"JPEG exposes quality and a transparency-loss warning");
    check(panel.Start.Content.ToString().Contains("JPG"),"primary action names the selected target rather than generic start");
    panel.Target.SelectedItem=panel.Target.Items.Cast<ConversionTarget>().Single(t=>t.Id=="webp");check(panel.Quality.IsEnabled,"WebP exposes lossy quality");
    panel.Target.SelectedItem=panel.Target.Items.Cast<ConversionTarget>().Single(t=>t.Id=="pdf");check(!panel.Quality.IsEnabled&&panel.Warning.Text.Contains("可编辑文字"),"image PDF explains loss of editable text and hides quality");
    panel.Files.SelectAll();Pump(panel.RemoveSelectedAsync());check(panel.Inputs.Length==0&&!panel.Start.IsEnabled&&panel.Target.Items.Count==0,"removing selected inputs safely clears stale targets");
   }
   using(var none=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps()),NeverConvert)){Pump(none.AddFilesAsync(new[]{input}));check(!none.Start.IsEnabled&&none.Status.Text.Contains("没有共同"),"no common conversion path is blocked and explained");}
  }
  static void ProbeRaces(string root,Action<bool,string> check){
   string first=Picture(root,"race-first.png"),second=Picture(root,"race-second.png");var reply=new TaskCompletionSource<ConversionCapabilities>();int calls=0;CancellationToken observed=CancellationToken.None;
   using(var panel=new FileConversionPanel(true,(files,token)=>{calls++;if(calls==1){observed=token;return reply.Task;}return Task.FromResult(Caps("pdf"));},NeverConvert)){
    Task pending=panel.AddFilesAsync(new[]{first});check(panel.IsChecking&&!panel.Start.IsEnabled,"pending format check cannot start with stale targets");
    Pump(panel.AddFilesAsync(new[]{second}));check(observed.IsCancellationRequested&&panel.Inputs.Length==2&&((ConversionTarget)panel.Target.SelectedItem).Id=="pdf","new inputs cancel the older format check and use their own intersection");
    reply.SetResult(Caps("jpg"));Pump(pending);check(panel.Target.Items.Count==1&&((ConversionTarget)panel.Target.SelectedItem).Id=="pdf","late format result never overrides the newer selection");
   }
  }
  static void SingleAndBatch(string root,Action<bool,string> check){
   string first=Picture(root,"single-source.png"),second=Picture(root,"second-source.png");
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png","jpg","webp")),NeverConvert,files=>Task.FromResult("已收藏。"))){
    Style(panel);Pump(panel.AddFilesAsync(new[]{first}));
    check(panel.SingleFileMode&&panel.SingleFileCard.Visibility==Visibility.Visible&&panel.BatchFiles.Visibility==Visibility.Collapsed,"one input is a source card, not a batch queue or mandatory batch mode");
    check(panel.Collect.IsChecked==true,"library-origin conversion defaults to collecting a new copy");
    Pump(panel.AddFilesAsync(new[]{second}));
    check(!panel.SingleFileMode&&panel.BatchFiles.Visibility==Visibility.Visible&&panel.SingleFileCard.Visibility==Visibility.Collapsed,"explicit multiselection alone reveals the expandable batch list");
    check(panel.Files.Items.Count==2,"batch list represents each input exactly once");
    panel.Files.SelectedItems.Clear();panel.Files.SelectedItems.Add(panel.Files.Items[1]);Pump(panel.RemoveSelectedAsync());
    check(panel.SingleFileMode&&panel.SingleFileCard.Visibility==Visibility.Visible&&panel.BatchFiles.Visibility==Visibility.Collapsed,"removing down to one file restores the simple single-file presentation");
    Pump(panel.ReplaceFilesAsync(new[]{second}));check(panel.Inputs.SequenceEqual(new[]{second})&&panel.SingleFileMode,"explicit single-file replacement replaces the original rather than silently making a batch");
    string status=panel.Status.Text;Pump(panel.ReplaceFilesAsync(null));check(panel.Inputs.SequenceEqual(new[]{second})&&panel.Status.Text==status,"cancelling source replacement preserves the previous input and status");
   }
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png","jpg")),NeverConvert)){
    Pump(panel.AddFilesAsync(new[]{Fixture(root,"multipage-source.pdf")}));check(panel.SingleFileMode&&panel.Warning.Visibility==Visibility.Visible&&panel.Warning.Text.Contains("多个文件"),"single PDF still warns that one input can generate multiple files");
   }
  }
  static void ConversionAndCollection(string root,Action<bool,string> check){
   string input=Picture(root,"convert.png"),output=Picture(root,"converted.png");int conversions=0,collections=0;string[] received=null;
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>{
    conversions++;received=files;progress.Report(new ConversionProgress{Completed=1,Total=1,FileName=input});return Task.FromResult(Result(input,output));
   },files=>{collections++;return Task.FromResult("已收藏成功。");},Path.Combine(root,"future-output"))){
    Pump(panel.AddFilesAsync(new[]{input}));Pump(panel.StartAsync());
    check(conversions==1&&collections==1&&received.SequenceEqual(new[]{input}),"single-library-file default converts the current source and collects its new output");
    check(panel.ResultFolder==root&&panel.Open.Visibility==Visibility.Visible&&panel.Open.IsEnabled&&panel.ResultFiles.SelectMany(f=>f.Outputs??new string[0]).Contains(output),"successful result stays accessible in the panel without forced navigation");
    string status=panel.Status.Text;panel.ChooseFiles=()=>null;Click(panel.Change);check(panel.Status.Text==status&&panel.ResultFiles.Any(file=>file.Outputs.Contains(output)),"cancelling the single-file change chooser retains the finished task and status");
    panel.Collect.IsChecked=false;Pump(panel.StartAsync());check(conversions==2&&collections==1,"explicit folder-only output does not import into the library");
   }
   var reply=new TaskCompletionSource<ConversionBatchResult>();int closed=0;CancellationToken observed=CancellationToken.None;string submittedTarget=null,submittedDirectory=null;int submittedQuality=0,imported=0;
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("jpg","png")),(files,target,directory,quality,progress,token)=>{
    received=files;observed=token;submittedTarget=target;submittedDirectory=directory;submittedQuality=quality;return reply.Task;
   },files=>{imported++;return Task.FromResult("已收藏。");},root)){
    panel.CloseRequested=()=>closed++;Pump(panel.AddFilesAsync(new[]{input}));panel.Target.SelectedItem=panel.Target.Items.Cast<ConversionTarget>().Single(t=>t.Id=="jpg");panel.Quality.Value=76;Task pending=panel.StartAsync();
    panel.Target.SelectedItem=panel.Target.Items.Cast<ConversionTarget>().Single(t=>t.Id=="png");panel.Output.Text=Path.Combine(root,"different");panel.Quality.Value=10;panel.Collect.IsChecked=false;
    Pump(panel.AddFilesAsync(new[]{output}));Pump(panel.ReplaceFilesAsync(new[]{output}));Click(panel.Collapse);
    check(!panel.Change.IsEnabled&&panel.Inputs.SequenceEqual(new[]{input}),"active task disables the change-source action and ignores replacement requests");
    check(closed==1&&!observed.IsCancellationRequested&&panel.IsBusy,"collapse restores details while leaving the active conversion running");
    check(received.SequenceEqual(new[]{input})&&submittedTarget=="jpg"&&submittedDirectory==root&&submittedQuality==76&&panel.Inputs.SequenceEqual(new[]{input}),"source, target, directory and quality are frozen when conversion starts");
    reply.SetResult(Result(input,output));Pump(pending);
    check(imported==1,"collection intent is frozen with the submitted request, not read from later UI state");
   }
  }
  static void RetryFailedOnly(string root,Action<bool,string> check){
   string first=Picture(root,"retry-first.png"),second=Picture(root,"retry-second.png"),one=Picture(root,"retry-result-one.png"),two=Picture(root,"retry-result-two.png");
   int calls=0;var batches=new List<string[]>();var requests=new List<string>();var collected=new List<string[]>();
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png","jpg")),(files,target,directory,quality,progress,token)=>{
    calls++;batches.Add(files.ToArray());requests.Add(target+"|"+directory+"|"+quality);return Task.FromResult(calls==1?new ConversionBatchResult{Files=new List<ConvertedFile>{Success(first,one),Failure(second,"Synthetic corrupt file")}}:Result(second,two));
   },files=>{collected.Add(files.Select(f=>f.Source).ToArray());return Task.FromResult("已收藏。");},root)){
    Pump(panel.AddFilesAsync(new[]{first,second}));Pump(panel.StartAsync());
    check(panel.FailedInputs.SequenceEqual(new[]{second})&&panel.ResultFiles.Any(f=>f.Source==first&&f.Outputs.Contains(one)),"partial failure keeps completed outputs and identifies the failed input");
    panel.Target.SelectedItem=panel.Target.Items.Cast<ConversionTarget>().Single(t=>t.Id=="jpg");panel.Output.Text=Path.Combine(root,"changed-after-task");panel.Quality.Value=42;panel.Collect.IsChecked=false;
    Pump(panel.RetryFailedAsync());
    check(calls==2&&batches[0].SequenceEqual(new[]{first,second})&&batches[1].SequenceEqual(new[]{second}),"retry failed does not reconvert already successful inputs");
    check(requests.Count==2&&requests[0]==requests[1],"failed-only retry preserves original target, destination and quality after controls are edited");
    check(panel.FailedInputs.Length==0&&panel.ResultFiles.SelectMany(f=>f.Outputs??new string[0]).OrderBy(p=>p).SequenceEqual(new[]{one,two}.OrderBy(p=>p)),"failed-only retry merges into previous results instead of discarding success");
    check(collected.Count==2&&collected[0].SequenceEqual(new[]{first})&&collected[1].SequenceEqual(new[]{second}),"failed-only retry collects only newly successful files, not earlier successes");
    Pump(panel.RetryFailedAsync());check(calls==2,"retry with no failed files cannot create an unintended new conversion");
    Pump(panel.AddFilesAsync(new[]{Picture(root,"explicit-new-input.png")}));check(panel.ResultFiles.Length==0&&panel.FailedInputs.Length==0,"explicitly changing the source clears previous task state");
   }
  }
  static void RetryCollection(string root,Action<bool,string> check){
   string input=Picture(root,"collection-source.png"),output=Picture(root,"collection-result.png");int conversions=0,collections=0;
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>{conversions++;return Task.FromResult(Result(input,output));},files=>{
    collections++;if(collections==1)throw new IOException("Synthetic library unavailable");return Task.FromResult("已收藏成功。");
   },root)){
    Pump(panel.AddFilesAsync(new[]{input}));Pump(panel.StartAsync());
    check(panel.Status.Text.Contains("转换已完成")&&panel.Status.Text.Contains("收藏未完成")&&panel.Open.IsEnabled&&panel.ResultFiles.Any(f=>f.Outputs.Contains(output)),"collection failure is distinct from conversion failure and preserves generated files");
    Pump(panel.RetryCollectionAsync());check(conversions==1&&collections==2&&panel.Status.Text.Contains("收藏成功"),"retry collection imports existing results without reconverting the original");
   }
   var collection=new TaskCompletionSource<string>();int ended=0;bool entered=false;
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>Task.FromResult(Result(input,output)),files=>{entered=true;return collection.Task;},root)){
    panel.OperationEnded+=()=>ended++;Pump(panel.AddFilesAsync(new[]{input}));Task pending=panel.StartAsync();Until(()=>entered);
    check(panel.IsBusy&&!panel.Cancel.IsEnabled&&ended==0,"already-started library collection stays busy without a misleading cancel action");
    panel.CancelOperation();check(!pending.IsCompleted&&panel.IsBusy&&ended==0,"close/cancel cannot abandon already-started collection");
    collection.SetResult("已收藏。");Pump(pending);check(!panel.IsBusy&&ended==1&&panel.Open.IsEnabled,"collection completion releases lifecycle exactly once and preserves results");
   }
  }
  static void PartialCollectionAndResultActions(string root,Action<bool,string> check){
   string input=Fixture(root,"partial-collection.pdf"),first=Picture(root,"partial-collection-page1.png"),second=Picture(root,"partial-collection-page2.png");int conversions=0,collections=0;var collected=new List<string[]>();var actions=new List<string>();
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>{
    conversions++;return Task.FromResult(new ConversionBatchResult{Files=new List<ConvertedFile>{new ConvertedFile{Source=input,Outputs=new[]{first,second},Error=""}}});
   },null,root)){
    panel.CollectDetailed=files=>{collections++;collected.Add(files.SelectMany(f=>f.Outputs).ToArray());return Task.FromResult(new ConversionCollectionResult{Message=collections==1?"已收藏 1 项，另 1 项未完成。":"全部收藏成功。",CollectedPaths=collections==1?new[]{first}:new[]{second}});};
    panel.Collect.IsChecked=true;panel.PreviewResult=path=>actions.Add("preview:"+path);panel.CopyResult=path=>actions.Add("copy:"+path);panel.RevealResult=path=>actions.Add("reveal:"+path);panel.LocateResult=path=>actions.Add("locate:"+path);
    Pump(panel.AddFilesAsync(new[]{input}));Pump(panel.StartAsync());
    check(conversions==1&&collections==1&&panel.CollectResults.Visibility==Visibility.Visible,"partial library receipt keeps retry collection available");
    Pump(panel.RetryCollectionAsync());
    check(conversions==1&&collections==2&&collected[0].SequenceEqual(new[]{first,second})&&collected[1].SequenceEqual(new[]{second}),"partial collection retries only uncollected outputs, without reconversion or duplicate import");
    check(panel.CollectResults.Visibility==Visibility.Collapsed,"fully collected outputs no longer offer redundant collection");
    Style(panel);var size=new Size(350,1100);panel.Measure(size);panel.Arrange(new Rect(new Point(),size));panel.UpdateLayout();
    foreach(string label in new[]{"预览","复制文件","文件位置","在素材库定位"}){
     var buttons=Children<Button>(panel).Where(button=>object.Equals(button.Content,label)).ToArray();check(buttons.Length==2,"result actions are exposed per generated page: "+label);if(buttons.Length>0)Click(buttons[0]);
    }
    check(actions.SequenceEqual(new[]{"preview:"+first,"copy:"+first,"reveal:"+first,"locate:"+first}),"result actions operate on the generated file, not the original PDF or current selection");
   }
  }
  static void BoundedPreviewsAndWarnings(string root,Action<bool,string> check){
   foreach(bool tall in new[]{false,true}){
    string input=Picture(root,tall?"skinny-tall.png":"skinny-wide.png",tall?32:4096,tall?4096:32);
    var preview=FileConversionPanel.DecodePreview(input,CancellationToken.None) as BitmapSource;
    check(preview!=null&&preview.PixelWidth<=176&&preview.PixelHeight<=176&&Math.Max(preview.PixelWidth,preview.PixelHeight)==176,"fallback thumbnail bounds both dimensions for "+(tall?"very tall":"very wide")+" raster");
    check(preview!=null&&preview.IsFrozen,"fallback thumbnail can cross worker/UI threads safely");
    check(FileConversionPanel.DecodePreview(input,new CancellationToken(true))==null,"cancelled fallback decode returns no image");
   }
   string image=Picture(root,"original.filename.png");const string suppliedWarning="保留 ICC 色彩配置；转换不保留可编辑图层。";
   using(var panel=new FileConversionPanel(true,(files,token)=>{var caps=Caps("pdf","webp","jpg","png");caps.Targets.Single(target=>target.Id=="png").Warning=suppliedWarning;return Task.FromResult(caps);},NeverConvert)){
    Style(panel);panel.SourceName=path=>"显示名称（不是文件名）";panel.SourceStem=path=>"design.v2.final";Pump(panel.AddFilesAsync(new[]{image}));Layout(panel,new Size(350,850));
    check(panel.Warning.Text==suppliedWarning&&panel.Warning.Visibility==Visibility.Visible,"injected capability warning is preserved rather than replaced with a generic message");
    check(Children<TextBlock>(panel).Any(text=>text.Text=="生成：design.v2.final.png"),"output preview preserves the full dotted source stem exactly");
    check(panel.Target.Items.Cast<ConversionTarget>().Take(3).Select(target=>target.Id).SequenceEqual(new[]{"png","jpg","webp"}),"common image targets stay first regardless of worker capability ordering");
    check(panel.Target.Visibility==Visibility.Collapsed&&panel.FormatChoices.Children.OfType<Button>().Count(button=>button.Tag is string)==3&&Children<Button>(panel).Any(button=>button.Name=="ConversionMoreFormats"&&button.Visibility==Visibility.Visible),"additional formats use one explicit more-formats entry, not a duplicated target selector");
   }
  }
  static void PagedResults(string root,Action<bool,string> check){
   string input=Fixture(root,"many-pages.pdf");string[] outputs=Enumerable.Range(1,25).Select(index=>Picture(root,"many-pages-"+index.ToString("00")+".png")).ToArray();var previewed=new List<string>();
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>Task.FromResult(new ConversionBatchResult{Files=new List<ConvertedFile>{new ConvertedFile{Source=input,Outputs=outputs,Error=""}}}),null,root)){
    Style(panel);panel.PreviewResult=path=>previewed.Add(path);Pump(panel.AddFilesAsync(new[]{input}));Pump(panel.StartAsync());Layout(panel,new Size(350,850));
    check(panel.ResultFiles.Single().Outputs.Length==25,"large result keeps every generated file available in task state");
    var previews=Children<Button>(panel).Where(button=>object.Equals(button.Content,"预览")).ToArray();
    check(previews.Length==12&&!Children<Button>(panel).Single(button=>button.Name=="ConversionPreviousResults").IsEnabled,"first result page materializes at most twelve rows and disables previous");
    Click(previews[0]);Click(Children<Button>(panel).Single(button=>button.Name=="ConversionNextResults"));Layout(panel,new Size(350,850));
    previews=Children<Button>(panel).Where(button=>object.Equals(button.Content,"预览")).ToArray();check(previews.Length==12,"second result page materializes only its twelve rows");Click(previews[0]);
    Click(Children<Button>(panel).Single(button=>button.Name=="ConversionNextResults"));Layout(panel,new Size(350,850));
    previews=Children<Button>(panel).Where(button=>object.Equals(button.Content,"预览")).ToArray();check(previews.Length==1&&!Children<Button>(panel).Single(button=>button.Name=="ConversionNextResults").IsEnabled,"last result page shows the remaining row and disables next");Click(previews[0]);
    check(previewed.SequenceEqual(new[]{outputs[0],outputs[12],outputs[24]}),"result paging keeps output identity and order across page changes");
    Click(Children<Button>(panel).Single(button=>button.Name=="ConversionPreviousResults"));Layout(panel,new Size(350,850));check(Children<Button>(panel).Count(button=>object.Equals(button.Content,"预览"))==12,"returning to an earlier page replaces rows rather than appending them");
   }
  }
  static void PagedFailures(string root,Action<bool,string> check){
   string[] inputs=Enumerable.Range(1,20).Select(index=>Picture(root,"all-failed-"+index.ToString("00")+".png")).ToArray();
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>Task.FromResult(new ConversionBatchResult{Files=files.Select((path,index)=>Failure(path,"Synthetic conversion error "+(index+1))).ToList()}),null,root)){
    Style(panel);Pump(panel.AddFilesAsync(inputs));Pump(panel.StartAsync());Layout(panel,new Size(350,850));
    var first=Children<TextBlock>(panel).Where(text=>text.Text.Contains("Synthetic conversion error ")).Select(text=>text.Text.Split('\n')[0]).ToArray();
    check(panel.FailedInputs.Length==20&&first.Length==12,"twenty failures retain all task errors while materializing only the first twelve result rows");
    var next=Children<Button>(panel).FirstOrDefault(button=>button.Name=="ConversionNextResults");
    check(next!=null&&next.IsEnabled,"all-failed tasks expose pagination even when there are zero successful outputs");
    if(next!=null){
     Click(next);Layout(panel,new Size(350,850));
     var last=Children<TextBlock>(panel).Where(text=>text.Text.Contains("Synthetic conversion error ")).Select(text=>text.Text.Split('\n')[0]).ToArray();
     check(last.Length==8&&first.Concat(last).Distinct().OrderBy(name=>name).SequenceEqual(inputs.Select(Path.GetFileName).OrderBy(name=>name)),"failure pagination exposes all twenty source errors exactly once across twelve/eight rows");
     next=Children<Button>(panel).FirstOrDefault(button=>button.Name=="ConversionNextResults");check(next!=null&&!next.IsEnabled,"the final all-failed page disables next rather than repeating or skipping errors");
    }
   }
  }
  static void MovedUncollectedOutput(string root,Action<bool,string> check){
   string input=Picture(root,"uncollected-source.png"),output=Picture(root,"uncollected-output.png"),moved=Path.Combine(root,"uncollected-output-relocated.png");int calls=0;string[] received=null;
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>Task.FromResult(Result(input,output)),null,root)){
    panel.CollectDetailed=files=>{calls++;received=files.SelectMany(file=>file.Outputs).ToArray();return Task.FromResult(new ConversionCollectionResult{Message="1 项未加入：转换结果已移动或不可读取。",CollectedPaths=new string[0]});};panel.Collect.IsChecked=false;
    Pump(panel.AddFilesAsync(new[]{input}));Pump(panel.StartAsync());check(calls==0&&panel.CollectResults.Visibility==Visibility.Visible,"folder-only result remains available for explicit later collection");
    // Preserve this synthetic output at a new fixture path while simulating
    // another program/user moving it before collection.
    File.Move(output,moved);Pump(panel.RetryCollectionAsync());
    check(calls==1&&received!=null&&received.SequenceEqual(new[]{output}),"missing uncollected outputs reach the collector instead of being silently filtered out");
    check(panel.Status.Text.Contains("未加入")&&!panel.Status.Text.Contains("已收藏到素材库")&&panel.CollectResults.Visibility==Visibility.Visible,"missing uncollected outputs remain explicitly failed/retryable, never falsely all-collected");
   }
  }
  static void CancelledChooserAndMovedResult(string root,Action<bool,string> check){
   string first=Picture(root,"chooser-first.png"),second=Picture(root,"chooser-second.png"),one=Picture(root,"chooser-one-output.png"),two=Picture(root,"chooser-two-output.png");int probes=0,located=0,previewed=0;
   using(var panel=new FileConversionPanel(true,(files,token)=>{probes++;return Task.FromResult(Caps("png"));},(files,target,directory,quality,progress,token)=>Task.FromResult(new ConversionBatchResult{Files=new List<ConvertedFile>{Success(first,one),Success(second,two)}}),files=>Task.FromResult("已收藏成功。"),root)){
    Style(panel);panel.ChooseFiles=()=>null;panel.LocateResult=path=>{if(path==one)located++;};panel.PreviewResult=path=>previewed++;Pump(panel.AddFilesAsync(new[]{first,second}));Pump(panel.StartAsync());Layout(panel,new Size(350,850));
    string before=panel.Status.Text;int previousProbes=probes;var originalResults=panel.ResultFiles.SelectMany(file=>file.Outputs).ToArray();Click(panel.Add);
    check(panel.Status.Text==before&&probes==previousProbes&&panel.ResultFiles.SelectMany(file=>file.Outputs).SequenceEqual(originalResults),"cancelling the chooser preserves completed results, status and supported formats without a new probe");
    // Only relocate this test's own generated file. The simulated collected
    // copy remains addressable by ID even when the export copy was moved.
    File.Move(one,Path.Combine(root,"chooser-one-relocated.png"));Click(Children<Button>(panel).First(button=>object.Equals(button.Content,"在素材库定位")));
    check(located==1,"library locate remains callable after its external output copy is moved");
    Click(Children<Button>(panel).First(button=>object.Equals(button.Content,"预览")));check(previewed==0&&panel.Status.Text.Contains("移动"),"file preview still explains a missing output rather than invoking a broken path");
   }
  }
  static void CancellationAndCollapse(string root,Action<bool,string> check){
   string input=Picture(root,"cancel.png"),output=Picture(root,"partial-output.png");var reply=new TaskCompletionSource<ConversionBatchResult>();CancellationToken received=CancellationToken.None;int calls=0,collections=0,ended=0;
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>{calls++;received=token;return reply.Task;},files=>{collections++;return Task.FromResult("已收藏完成的结果。");},root)){
    panel.OperationEnded+=()=>ended++;Pump(panel.AddFilesAsync(new[]{input}));Task pending=panel.StartAsync();
    check(panel.IsBusy&&!panel.Add.IsEnabled&&!panel.Output.IsEnabled&&!panel.Target.IsEnabled&&panel.Cancel.IsEnabled,"conversion freezes request controls and exposes cancel remaining");
    Pump(panel.StartAsync());Pump(panel.AddFilesAsync(new[]{output}));check(calls==1&&panel.Inputs.Length==1,"duplicate start and drop during a task cannot mutate it or submit a second task");
    panel.CancelOperation();check(received.IsCancellationRequested&&!panel.Cancel.IsEnabled&&panel.Status.Text.Contains("正在取消"),"cancel signals the active worker and cannot be repeated");
    reply.SetResult(Result(input,output,true));Pump(pending);
    check(!panel.IsBusy&&panel.Open.IsEnabled&&collections==1&&ended==1&&panel.ResultFiles.Any(f=>f.Outputs.Contains(output)),"cancel remaining preserves and collects already completed results exactly once");
   }
   int closed=0;using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),NeverConvert)){panel.CloseRequested=()=>closed++;Click(panel.Collapse);check(closed==1&&!panel.IsDisposed,"idle collapse leaves the panel available for reopening");}
  }
  static void ErrorsAndDispose(string root,Action<bool,string> check){
   string input=Picture(root,"error.png");
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>{throw new IOException("Synthetic corrupt file");},null,root)){
    Pump(panel.AddFilesAsync(new[]{input}));panel.Output.Text="relative";Pump(panel.StartAsync());check(panel.Status.Text.Contains("目录无效")&&!panel.IsBusy,"relative result path is rejected before conversion");
    panel.Output.Text=root;Pump(panel.StartAsync());check(panel.Status.Text.Contains("转换未完成")&&panel.Status.Text.Contains("corrupt")&&panel.Start.IsEnabled,"conversion errors retain actionable explanation and remain retryable");
   }
   using(var panel=new FileConversionPanel(true,(files,token)=>Task.FromResult(Caps("png")),(files,target,directory,quality,progress,token)=>Task.FromResult(Result(input,Path.Combine(root,"nonexistent-output.png"))),null,root)){Pump(panel.AddFilesAsync(new[]{input}));Pump(panel.StartAsync());check(!panel.Open.IsEnabled,"nonexistent converter outputs never expose a misleading open-results action");}
   var delayed=new TaskCompletionSource<ConversionCapabilities>();CancellationToken observed=CancellationToken.None;int calls=0;var disposed=new FileConversionPanel(true,(files,token)=>{calls++;observed=token;return delayed.Task;},NeverConvert);
   Task pending=disposed.AddFilesAsync(new[]{input});disposed.Dispose();string before=disposed.Status.Text;delayed.SetResult(Caps("png"));Pump(pending);Pump(disposed.StartAsync());check(observed.IsCancellationRequested&&disposed.IsDisposed&&disposed.Status.Text==before&&!disposed.Start.IsEnabled&&calls==1,"disposed panel cancels format probing and ignores late results");
  }
  static void SelectionGuards(Action<bool,string> check){
   Mori.Asset image=new Mori.Asset{Id="image",Ext=".png"},pdf=new Mori.Asset{Id="pdf",Ext=".pdf"},web=new Mori.Asset{Id="web",Ext=".url"},deleted=new Mori.Asset{Id="deleted",Ext=".png",Deleted=true};
   check(Mori.MainWindow.ConversionSelectionAllowed(new[]{image,pdf},new[]{image,pdf},false,"all"),"visible image/document multiselection can enter conversion");
   check(!Mori.MainWindow.ConversionSelectionAllowed(new[]{image},new[]{pdf},false,"all"),"hidden selected record is never acted on");
   check(!Mori.MainWindow.ConversionSelectionAllowed(new[]{image},new[]{image},true,"all"),"browser/project/reference pages cannot consume stale gallery selection");
   check(!Mori.MainWindow.ConversionSelectionAllowed(new[]{image},new[]{image},false,"trash"),"recycle-bin conversion stays unavailable");
   check(!Mori.MainWindow.ConversionSelectionAllowed(new[]{deleted},new[]{deleted},false,"all"),"deleted record cannot be converted");
   check(!Mori.MainWindow.ConversionSelectionAllowed(new[]{web},new[]{web},false,"all"),"bookmark is not a conversion file input");
   check(!Mori.MainWindow.ConversionSelectionAllowed(new Mori.Asset[0],new[]{image},false,"all"),"empty selection cannot open asset conversion");
  }
  static void ApplyActualTheme(Grid panel,bool dark){
   // Only parse the resource dictionary, not the Window or production shell.
   var xml=new XmlDocument();using(var stream=typeof(FileConversionPanel).Assembly.GetManifestResourceStream("Mori.Main.xaml"))xml.Load(stream);
   var namespaces=new XmlNamespaceManager(xml.NameTable);namespaces.AddNamespace("w","http://schemas.microsoft.com/winfx/2006/xaml/presentation");var resourceNode=xml.SelectSingleNode("/w:Window/w:Window.Resources",namespaces);
   string xaml="<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:Mori;assembly=Mori\">"+resourceNode.InnerXml+"</ResourceDictionary>";panel.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml));
   const BindingFlags fields=BindingFlags.NonPublic|BindingFlags.Static;var keys=(string[])typeof(Mori.Theme).GetField("Keys",fields).GetValue(null);var colors=(string[])typeof(Mori.Theme).GetField(dark?"Dark":"Light",fields).GetValue(null);
   for(int i=0;i<keys.Length;i++){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));brush.Freeze();panel.Resources[keys[i]]=brush;}
   panel.Resources[SystemColors.HighlightBrushKey]=panel.Resources["AccentSoft"];panel.Resources[SystemColors.HighlightTextBrushKey]=panel.Resources["Text"];panel.Resources[SystemColors.InactiveSelectionHighlightBrushKey]=panel.Resources["AccentSoft"];panel.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey]=panel.Resources["Text"];
  }
  static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);var match=child as T;if(match!=null)yield return match;foreach(var nested in Children<T>(child))yield return nested;}}
  static void Layout(FrameworkElement panel,Size size){panel.Measure(size);panel.Arrange(new Rect(new Point(),size));panel.UpdateLayout();Drain();}
  static void CheckPinnedHeader(FileConversionPanel panel,FrameworkElement host,Action<bool,string> check,string state){
   var title=Children<TextBlock>(panel).Single(text=>text.Text=="转换格式");var scroll=panel.Children.OfType<ScrollViewer>().Single();
   Point titleTop=title.TranslatePoint(new Point(),host),titleBottom=title.TranslatePoint(new Point(0,title.ActualHeight),host),collapseTop=panel.Collapse.TranslatePoint(new Point(),host),collapseBottom=panel.Collapse.TranslatePoint(new Point(0,panel.Collapse.ActualHeight),host),scrollTop=scroll.TranslatePoint(new Point(),host);
   check(titleTop.Y>=0&&collapseTop.Y>=0&&titleBottom.Y<=scrollTop.Y&&collapseBottom.Y<=scrollTop.Y&&panel.Collapse.IsEnabled,"title and collapse remain visible above the scrolling content at "+state);
  }
  static void SavePreview(Border host,Size size,string path){var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(host);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))encoder.Save(stream);}
  static void RenderPreviews(string root,Action<bool,string> check){
   string first=Picture(root,"产品主图.png"),second=Picture(root,"第二张产品图.png"),third=Fixture(root,"产品说明书.pdf");
   foreach(bool dark in new[]{false,true})foreach(bool narrow in new[]{false,true}){
    using(var panel=new FileConversionPanel(true,(inputs,token)=>Task.FromResult(Caps("png","jpg","webp","pdf")),NeverConvert,results=>Task.FromResult("本测试不会调用收藏。"),@"D:\转换结果")){
     ApplyActualTheme(panel,dark);Pump(panel.AddFilesAsync(new[]{first}));panel.Target.SelectedItem=panel.Target.Items.Cast<ConversionTarget>().First(t=>t.Id==(narrow?"jpg":"png"));
     var size=narrow?new Size(300,760):new Size(350,850);var host=new Border{Background=(Brush)panel.Resources["Canvas"],Child=panel};host.Measure(size);host.Arrange(new Rect(new Point(),size));host.UpdateLayout();WaitForThumbnail(panel);Drain();
     check(PresentationSource.FromVisual(panel)==null&&Application.Current==null,"actual-theme sidebar preview has no Application, HWND or visible window");
     check(panel.SingleFileMode&&panel.BatchFiles.Visibility==Visibility.Collapsed,"single-file sidebar does not display the batch queue");
     check(panel.Start.ActualHeight>=34&&panel.Start.TranslatePoint(new Point(0,panel.Start.ActualHeight),host).Y<=size.Height,"single-file primary action remains reachable at "+size.Width+" px width");
     check(Children<Image>(panel.SingleFileCard).Any(image=>image.Source!=null),"single-file card renders a genuine file thumbnail offscreen");
     check(panel.Start.Content.ToString().Contains(narrow?"JPG":"PNG"),"sidebar primary action has the real selected format");
     string name="conversion-"+(dark?"dark":"light")+"-"+(narrow?"narrow":"normal"),path=Path.Combine(root,name+".png");SavePreview(host,size,path);check(File.Exists(path)&&new FileInfo(path).Length>1000,"offscreen single-file preview saved: "+path);
     if(!dark&&!narrow){
      var referenceSize=new Size(372,944);Layout(host,referenceSize);SavePreview(host,referenceSize,Path.Combine(root,"conversion-light-reference-372x944.png"));
      check(panel.ActualWidth==372&&panel.ActualHeight==944&&PresentationSource.FromVisual(panel)==null,"reference-sized single-file render uses the exact 372 by 944 detached sidebar dimensions");Layout(host,size);
     }
     panel.Collect.IsChecked=false;host.Measure(size);host.Arrange(new Rect(new Point(),size));host.UpdateLayout();
     check(((FrameworkElement)panel.Output.Parent).Visibility==Visibility.Visible&&PresentationSource.FromVisual(panel)==null,"external folder-only mode reveals output location in the same detached panel");
     check(panel.Output.ActualHeight>=30&&panel.Start.TranslatePoint(new Point(0,panel.Start.ActualHeight),host).Y<=size.Height,"external folder-only single file keeps destination and convert action reachable at "+size.Width+" px");
     SavePreview(host,size,Path.Combine(root,name+"-external.png"));panel.Collect.IsChecked=true;
     Pump(panel.AddFilesAsync(new[]{second,third}));panel.BatchFiles.IsExpanded=true;host.Measure(size);host.Arrange(new Rect(new Point(),size));host.UpdateLayout();Drain();
     check(!panel.SingleFileMode&&panel.Files.Items.Count==3&&panel.BatchFiles.IsExpanded,"same-width panel expands the actual three-file batch only on request");SavePreview(host,size,Path.Combine(root,name+"-batch.png"));
     panel.Status.Text=new string('错',3000);host.Measure(size);host.Arrange(new Rect(new Point(),size));host.UpdateLayout();check(panel.ActualWidth<=size.Width&&PresentationSource.FromVisual(panel)==null,"long worker errors remain inside the sidebar and never create a modal surface");
     var scroll=panel.Children.OfType<ScrollViewer>().Single();scroll.ScrollToBottom();Layout(host,size);check(scroll.VerticalOffset>0,"long-error regression actually scrolls beyond the initial viewport");CheckPinnedHeader(panel,host,check,"long-error bottom");
    }
   }
  }
  static void RenderResults(string root,Action<bool,string> check){
   string first=Picture(root,"产品展示.png"),second=Picture(root,"产品背面.png"),output=Picture(root,"产品展示-转换.png");
   foreach(bool dark in new[]{false,true})foreach(bool partial in new[]{false,true}){
    using(var panel=new FileConversionPanel(true,(inputs,token)=>Task.FromResult(Caps("png","jpg","webp")),(files,target,directory,quality,progress,token)=>Task.FromResult(new ConversionBatchResult{Files=partial?new List<ConvertedFile>{Success(first,output),Failure(second,"无法读取文件，请检查是否已移动或损坏。")}:new List<ConvertedFile>{Success(first,output)}}),files=>Task.FromResult("已加入素材库 1 项。"),root)){
     ApplyActualTheme(panel,dark);panel.PreviewResult=path=>{};panel.CopyResult=path=>{};panel.RevealResult=path=>{};panel.LocateResult=path=>{};Pump(panel.AddFilesAsync(partial?new[]{first,second}:new[]{first}));Pump(panel.StartAsync());
     var size=new Size(350,850);var host=new Border{Background=(Brush)panel.Resources["Canvas"],Child=panel};Layout(host,size);
     var scroll=panel.Children.OfType<ScrollViewer>().Single();scroll.ScrollToBottom();Layout(host,size);
     check(panel.ResultFiles.Any(file=>file.Outputs.Contains(output))&&Children<Button>(panel).Any(button=>object.Equals(button.Content,"预览")),"completed output state renders its per-file actions, "+(partial?"partial failure":"success"));
     check(PresentationSource.FromVisual(panel)==null&&Application.Current==null,"result-state rendering remains offscreen without app or window");
     CheckPinnedHeader(panel,host,check,partial?"partial-results bottom":"completed-results bottom");
     SavePreview(host,size,Path.Combine(root,"conversion-"+(dark?"dark":"light")+"-results-"+(partial?"partial":"ready")+".png"));
    }
   }
  }
 }
}
