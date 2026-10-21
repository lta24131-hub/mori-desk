using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // No Application, Window, native picker, clipboard or real library is used.
 internal static class FileConversionCollectionTests {
  static string FileAt(string root,string name,int marker){
   string path=Path.Combine(root,name);
   var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j9s8AAAAASUVORK5CYII=");
   File.WriteAllBytes(path,png.Concat(new[]{(byte)marker}).ToArray());return path;
  }
  static ConvertedFile Result(string source,params string[] outputs){return new ConvertedFile{Source=source,Outputs=outputs,Error=""};}
  static ConversionCollectionResult Collect(FileConversionCollectionSession session,params ConvertedFile[] files){return session.CollectAsync(files).GetAwaiter().GetResult();}
  static Asset Find(Library library,FileConversionCollectionSession session,string output){string id=session.CollectedAssetId(output);return library.All().Single(asset=>asset.Id==id);}
  internal static void Run(string output,Action<bool,string> check){
   string root=Path.Combine(output,"file-conversion-collection");Directory.CreateDirectory(root);
   SnapshotAndCategory(root,check);DuplicatesAndRetry(root,check);References(root,check);
   var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{DetachedHost(root,check);}finally{SynchronizationContext.SetSynchronizationContext(previous);}
   check(Application.Current==null,"collection and host boundary checks create no Application or visible window");
  }
  static void SnapshotAndCategory(string root,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"snapshot-library"))){
    string one=library.CreateFolder("项目甲",""),two=library.CreateFolder("项目乙","");
    string sourceFile=FileAt(root,"snapshot-source.png",1),resultFile=FileAt(root,"原名称.png",2);
    var source=library.Import(sourceFile,one,"原名称","https://example.com/source","原标签","原备注").Item;library.AddToFolder(new[]{source.Id},two);
    var session=new FileConversionCollectionSession(library,new[]{source});
    var journal=new UndoHistory(library.Db);journal.Record("编辑原素材",delegate{source.Name="新名称";source.Tags="新标签";source.Source="https://example.com/changed";source.Notes="新备注";library.Update(source);});
    var receipt=Collect(session,Result(library.FullPath(source),resultFile));var added=Find(library,session,resultFile);
    check(receipt.CollectedPaths.SequenceEqual(new[]{resultFile}),"conversion collection returns exact successfully collected output paths");
    var resultIds=session.CollectedAssetIds();check(resultIds.SequenceEqual(new[]{added.Id})&&!resultIds.Contains(source.Id),"task result snapshot contains collected records only, never the original source");
    resultIds[0]="changed by caller";check(session.CollectedAssetIds().SequenceEqual(new[]{added.Id}),"returned result ID snapshot cannot mutate the collection session");
    check(added.Name=="原名称"&&added.Tags=="原标签"&&added.Notes=="原备注"&&added.Source=="https://example.com/source","conversion copies frozen source metadata, not later inspector edits");
    check(library.AssetFolders(added.Id).Select(folder=>folder.Id).OrderBy(id=>id).SequenceEqual(new[]{one,two}.OrderBy(id=>id)),"new converted output inherits every original media category");
    check(library.All().Single(asset=>asset.Id==source.Id).Name=="新名称"&&File.Exists(sourceFile),"collection leaves source metadata and original file untouched");
    check(journal.Count==1&&journal.Undo()=="编辑原素材"&&library.All().Any(asset=>asset.Id==added.Id),"conversion preserves unrelated undo history and its undo keeps converted result");
    int count=library.All().Count;var again=Collect(session,Result(library.FullPath(source),resultFile));check(library.All().Count==count&&again.CollectedPaths.Length==1,"repeating collection of a completed result is idempotent");
    var retainedIds=session.CollectedAssetIds();
    var missingCategory=new FileConversionCollectionSession(library,library.All().Where(asset=>asset.Id==source.Id));library.DeleteFolder(one);library.DeleteFolder(two);
    string later=FileAt(root,"category-deleted-result.png",3);var changed=Collect(missingCategory,Result(library.FullPath(source),later));var unfiled=Find(library,missingCategory,later);
    check(changed.CollectedPaths.Length==1&&library.AssetFolders(unfiled.Id).Count==0&&changed.Message.Contains("原分类已变化"),"deleted source categories safely fall back to unfiled and explain the change");
    using(var elsewhere=new Library(Path.Combine(root,"other-library"))){
     string other=FileAt(root,"origin-stable-result.png",4);Collect(session,Result(library.FullPath(source),other));
     check(retainedIds.SequenceEqual(new[]{added.Id})&&session.CollectedAssetIds().Length==2,"retained task result IDs remain a snapshot when later results are collected");
     check(elsewhere.All().Count==0&&library.All().Any(asset=>asset.Id==session.CollectedAssetId(other))&&session.LibraryRoot==library.Root,"collection remains bound to originating library while another library exists");
    }
   }
  }
  static void DuplicatesAndRetry(string root,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"duplicates-library"))){
    string category=library.CreateFolder("原分类",""),otherCategory=library.CreateFolder("已有分类","");
    string input=FileAt(root,"duplicates-source.png",10),same=FileAt(root,"duplicate-result.png",11),missing=Path.Combine(root,"temporarily-missing.png");
    var source=library.Import(input,category,"源文件","https://example.com/source","来源标签","来源备注").Item;
    var existing=library.Import(same,otherCategory,"已有名称","https://example.com/existing","已有标签","已有备注").Item;
    var session=new FileConversionCollectionSession(library,new[]{source});
    var partial=Collect(session,Result(library.FullPath(source),same,missing));
    var kept=library.All().Single(asset=>asset.Id==existing.Id);
    check(partial.CollectedPaths.SequenceEqual(new[]{same})&&partial.Message.Contains("1 项未加入"),"partial collection reports failed paths separately and retains successes");
    check(session.CollectedAssetIds().SequenceEqual(new[]{existing.Id})&&session.CollectedAssetId(missing)==null,"partial collection result action receives successful IDs only, not missing outputs");
    check(session.CollectedAssetId(same)==existing.Id&&kept.Name=="已有名称"&&kept.Tags=="已有标签"&&kept.Source=="https://example.com/existing"&&kept.Notes=="已有备注","duplicate reuse never overwrites existing name, tags, source or notes");
    check(library.AssetFolders(kept.Id).Select(folder=>folder.Id).SequenceEqual(new[]{otherCategory}),"duplicate reuse never silently adds source categories");
    FileAt(root,"temporarily-missing.png",12);var retried=Collect(session,Result(library.FullPath(source),missing));
    check(retried.CollectedPaths.SequenceEqual(new[]{missing})&&library.All().Count==3&&session.CollectedAssetId(same)==existing.Id,"failed collection retries without reconverting or duplicating successes");
    Collect(session,Result(library.FullPath(source),same,missing));check(session.CollectedAssetIds().Length==2,"result snapshot deduplicates repeated successful receipts");
    string existingPath=library.FullPath(existing);byte[] existingBytes=File.ReadAllBytes(existingPath);File.WriteAllText(existingPath,"externally replaced original");
    var changedOriginal=new FileConversionCollectionSession(library,new[]{source});var mismatch=Collect(changedOriginal,Result(library.FullPath(source),same));
    check(mismatch.CollectedPaths.Length==0&&mismatch.Message.Contains("原文件已变化")&&File.ReadAllText(existingPath)=="externally replaced original","externally changed duplicate original is not falsely reported as saved or overwritten");
    File.WriteAllBytes(existingPath,existingBytes);var repaired=Collect(changedOriginal,Result(library.FullPath(source),same));
    check(repaired.CollectedPaths.SequenceEqual(new[]{same})&&changedOriginal.CollectedAssetId(same)==existing.Id,"collection retry can reuse a verified duplicate after its original is restored");
    library.Trash(new[]{existing.Id},true);var fresh=new FileConversionCollectionSession(library,new[]{source});Collect(fresh,Result(library.FullPath(source),same));
    check(library.All().Single(asset=>asset.Id==existing.Id).Deleted&&fresh.CollectedAssetId(same)!=existing.Id&&Find(library,fresh,same).Name==Path.GetFileNameWithoutExtension(same),"matching trash item remains deleted and conversion creates an independent new copy");
    string page1=FileAt(root,"文档-001.png",13),page2=Path.Combine(root,"文档-002.png");var pages=new FileConversionCollectionSession(library,new[]{source});
    Collect(pages,Result(library.FullPath(source),page1,page2));FileAt(root,"文档-002.png",14);Collect(pages,Result(library.FullPath(source),page2));
    check(Find(library,pages,page1).Name=="文档-001"&&Find(library,pages,page2).Name=="文档-002","retrying one failed page preserves published page numbering rather than renumbering the filtered list");
    check(File.Exists(input)&&File.Exists(same)&&File.Exists(missing),"collection does not delete source or published output files");
   }
  }
  static void References(string root,Action<bool,string> check){
   using(var library=new Library(Path.Combine(root,"reference-library"))){
    library.EnableReferenceAssets();string category=library.CreateFolder("快捷来源分类","");
    string original=FileAt(root,"external-shortcut.png",20);byte[] before=File.ReadAllBytes(original);
    string id=library.AddReferenceAssets(new[]{original},category).Single();var source=library.All().Single(asset=>asset.Id==id);source.Tags="快捷标签";source.Source="https://example.com/reference";library.Update(source);
    var session=new FileConversionCollectionSession(library,new[]{source});string converted=FileAt(root,"shortcut-converted.png",21);Collect(session,Result(original,converted));var result=Find(library,session,converted);
    check(!result.IsReference&&result.Tags=="快捷标签"&&result.Source==source.Source&&library.AssetFolders(result.Id).Any(folder=>folder.Id==category),"shortcut conversion becomes a new managed asset with original metadata and category");
    check(before.SequenceEqual(File.ReadAllBytes(original))&&library.All().Any(asset=>asset.Id==id&&asset.IsReference),"shortcut conversion never relocates or rewrites the external original");
   }
  }
  static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,args);}
  static void Field(object target,string name,object value){target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);}
  static void Pump(Task task){
   DateTime deadline=DateTime.UtcNow.AddSeconds(5);
   while(!task.IsCompleted){
    if(DateTime.UtcNow>deadline)throw new TimeoutException("Detached conversion host task timed out");
    var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);
   }
   task.GetAwaiter().GetResult();
  }
  static void DetachedHost(string root,Action<bool,string> check){
   var host=(MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
   var conversion=new TaskCompletionSource<ConversionBatchResult>();
   using(var panel=new FileConversionPanel(true,(paths,token)=>Task.FromResult(new ConversionCapabilities{Targets=new[]{new ConversionTarget{Id="png",Label="PNG"}}}),(paths,target,folder,quality,progress,token)=>conversion.Task,null,Path.Combine(root,"escape-output"))){
    var scroll=new ScrollViewer{Content=panel};Field(host,"fileConversionPanel",panel);Field(host,"fileConversionScroll",scroll);Field(host,"fileConversionVisible",true);
    check((bool)Call(host,"InFileConversion",panel.Start)&&(bool)Call(host,"InFileConversion",scroll)&&!(bool)Call(host,"InFileConversion",new Grid()),"conversion input boundary includes its controls and blank scroll area but excludes gallery");
    var key=new KeyEventArgs(Keyboard.PrimaryDevice,new DetachedSource(panel),0,Key.Delete){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=panel.Start};
    Call(host,"OnKey",null,key);
    check(!key.Handled,"gallery key handler defers Delete from conversion controls without reaching gallery deletion");
    Pump(panel.AddFilesAsync(new[]{FileAt(root,"escape-input.png",30)}));var operation=panel.StartAsync();bool collapsed=false;
    var escape=new KeyEventArgs(Keyboard.PrimaryDevice,new DetachedSource(panel),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent,Source=panel.Start};
    MainWindow.HandleFileConversionEscape(escape,panel.Target,()=>collapsed=true);
    check(collapsed&&escape.Handled&&panel.IsBusy&&!operation.IsCompleted,"Escape collapses conversion without cancelling an active operation");
    conversion.SetResult(new ConversionBatchResult());Pump(operation);
    Field(host,"fileConversionVisible",false);check(!(bool)Call(host,"InFileConversion",panel.Start),"collapsed converter cannot capture the active gallery's input");
   }
   var grid=new Grid();for(int i=0;i<5;i++)grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(100)});
   var left=new GridSplitter();var right=new GridSplitter();Grid.SetColumn(right,3);Grid.SetColumn(left,1);grid.Children.Add(left);grid.Children.Add(right);
   var layout=new SidebarLayout(grid,left,right,208,252);int changed=0;layout.Changed+=()=>changed++;
   layout.SetTemporaryRightWidth(336);
   check(layout.Right==252&&grid.ColumnDefinitions[4].Width.Value==336&&changed==0,"opening conversion temporarily widens sidebar without altering saved width");
   layout.SetTemporaryRightWidth(null);check(grid.ColumnDefinitions[4].Width.Value==252&&changed==0,"collapsing conversion restores original inspector width");
   layout.SetTemporaryRightWidth(336);grid.ColumnDefinitions[0].Width=new GridLength(222);Call(layout,"Commit",true);
   layout.SetTemporaryRightWidth(null);check(layout.Right==252,"resizing left sidebar during conversion does not save temporary right width");
   layout.SetTemporaryRightWidth(336);grid.ColumnDefinitions[4].Width=new GridLength(370);Call(layout,"Commit",false);layout.SetTemporaryRightWidth(null);
   check(layout.Right==370&&grid.ColumnDefinitions[4].Width.Value==370,"deliberate right-sidebar resize is retained after conversion collapse");
  }
  sealed class DetachedSource:PresentationSource {
   Visual visual;
   internal DetachedSource(Visual root){visual=root;}
   public override Visual RootVisual {get{return visual;}set{visual=value;}}
   protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore(){return null;}
   public override bool IsDisposed {get{return false;}}
  }
 }
}
