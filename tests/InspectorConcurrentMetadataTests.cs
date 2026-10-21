using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Synthetic fixtures; included in SuiteTests and the VM reflection runner.
 // Both cases cover supported background writers, not arbitrary DB editors.
 internal static class InspectorConcurrentMetadataTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static void Call(MainWindow main,string name,params object[] args){typeof(MainWindow).GetMethod(name,Hidden).Invoke(main,args);}
  static TextBox Text(MainWindow main,string name){return (TextBox)main.Window.FindName(name);}
  static void Select(MainWindow main,string id){var selected=(HashSet<string>)Field(main,"selection");selected.Clear();selected.Add(id);Call(main,"UpdateInspector");}
  static void Save(MainWindow main){((Button)main.Window.FindName("SaveMeta")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
  static void StopTimers(MainWindow main){foreach(string name in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})((DispatcherTimer)Field(main,name)).Stop();}
  static string Picture(string root){string file=Path.Combine(root,"original.png");using(var image=new System.Drawing.Bitmap(4,3)){using(var paint=System.Drawing.Graphics.FromImage(image))paint.Clear(System.Drawing.Color.MediumPurple);image.Save(file,System.Drawing.Imaging.ImageFormat.Png);}return file;}
  static void Fixture(string root,Action<Library,MainWindow,Asset,string> work){
   root=Path.GetFullPath(root);Directory.CreateDirectory(root);
   string oldData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled,interactive=Entry.InteractiveShell;
   var previousThumbnail=ThumbnailImage.CurrentLibrary;var previousContext=SynchronizationContext.Current;
   var app=Application.Current??new Application();var oldShutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"isolated-profile"));Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   try{
    string file=Picture(root);
    using(var library=new Library(Path.Combine(root,"library"))){
     var asset=library.Import(file,"","Original name","https://example.invalid/original","original tags","original notes").Item;
     asset.Favorite=true;library.Update(asset);
     var main=new MainWindow(library){PreviewMode=true};main.DeferDeskServices();main.PendingInformationChoice=()=>MessageBoxResult.No;StopTimers(main);
     try{Select(main,asset.Id);work(library,main,asset,file);}
     finally{StopTimers(main);main.CloseForReplacement();}
    }
   }finally{ThumbnailImage.CurrentLibrary=previousThumbnail;Environment.SetEnvironmentVariable("MORI_DESK_DATA",oldData);Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;Entry.InteractiveShell=interactive;SynchronizationContext.SetSynchronizationContext(previousContext);Theme.Set(oldTheme);app.ShutdownMode=oldShutdown;}
  }
  internal static void Run(string root,Action<bool,string> check){
   Fixture(Path.Combine(root,"supported-writers"),(library,main,asset,file)=>{
    byte[] original=File.ReadAllBytes(file),managed=File.ReadAllBytes(library.FullPath(asset));
    Text(main,"Tags").Text="draft while browser saves";
    var request=new Dictionary<string,object>{{"action","save"},{"kind","image"},{"data",Convert.ToBase64String(original)},{"name","incoming browser name"},{"source","https://example.invalid/incoming"},{"duplicateAction","reuse"}};
    // This is the actual native-host entry: a second Library connection is
    // opened under the normal write lease, not a direct UPDATE in the test.
    NativeBridge.HandleAtCurrentLibrary(request,library.Root);
    var stored=library.All().Single();
    check(stored.Id==asset.Id&&stored.Name==asset.Name&&stored.Notes==asset.Notes&&stored.Tags==asset.Tags&&stored.Source==asset.Source&&stored.Favorite,"supported duplicate browser save preserves all existing editable metadata");
    Call(main,"Refresh",false);
    check((bool)Field(main,"refreshDeferred")&&Text(main,"Tags").Text=="draft while browser saves"&&Text(main,"Notes").Text==asset.Notes,"browser refresh defers without changing an inspector draft");
    Save(main);stored=library.All().Single();
    check(stored.Tags=="draft while browser saves"&&stored.Name==asset.Name&&stored.Notes==asset.Notes&&stored.Source==asset.Source&&stored.Favorite,"saving after normal duplicate browser collection retains unrelated metadata");

    Text(main,"Tags").Text="draft while conversion collects";
    string output=Path.Combine(Path.GetDirectoryName(file),"converted-output.png");File.Copy(file,output,true);
    var session=new FileConversionCollectionSession(library,new[]{stored});
    var receipt=session.CollectAsync(new[]{new ConvertedFile{Source=library.FullPath(stored),Outputs=new[]{output}}}).GetAwaiter().GetResult();
    check(receipt.CollectedPaths.SequenceEqual(new[]{output})&&session.CollectedAssetId(output)==asset.Id&&library.All().Count==1,"supported optional conversion collection reuses byte-identical active output without replacing its record");
    stored=library.All().Single();
    check(stored.Tags=="draft while browser saves"&&stored.Notes==asset.Notes&&stored.Source==asset.Source&&stored.Name==asset.Name&&stored.Favorite,"reusing a conversion result does not rewrite the existing result's metadata from an older snapshot");
    Call(main,"Refresh",false);Save(main);stored=library.All().Single();
    check(stored.Tags=="draft while conversion collects"&&stored.Notes==asset.Notes&&stored.Source==asset.Source&&stored.Favorite,"saving after supported conversion collection retains unrelated metadata");
    check(File.ReadAllBytes(file).SequenceEqual(original)&&File.ReadAllBytes(library.FullPath(stored)).SequenceEqual(managed),"all concurrent metadata scenarios preserve synthetic source and managed image bytes");
    check(!main.Window.IsVisible,"supported writer regressions never present a desktop window");
   });
  }
 }
}
