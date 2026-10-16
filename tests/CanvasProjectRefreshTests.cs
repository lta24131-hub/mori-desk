using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MoriDesk {
 internal static class CanvasProjectRefreshTests {
  static void Await(Task task){
   DateTime deadline=DateTime.UtcNow.AddSeconds(10);
   while(!task.IsCompleted){if(DateTime.UtcNow>deadline)throw new TimeoutException("Project refresh did not finish.");var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}
   task.GetAwaiter().GetResult();
  }
  static void Render(CanvasProjectsPage page){page.Measure(new Size(650,430));page.Arrange(new Rect(0,0,650,430));page.UpdateLayout();}
  static string Thumbnail(){var image=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{20,40,200,255},4);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var output=new MemoryStream()){png.Save(output);return "data:image/png;base64,"+Convert.ToBase64String(output.ToArray());}}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();var context=SynchronizationContext.Current;CanvasProjectsPage page=null;
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{
    var store=new FreeCanvasStore(Path.Combine(root,"profile"));var mature=new MatureCanvasStore(Path.Combine(root,"profile"));
    var documents=new List<FreeCanvasDocument>();for(int i=0;i<30;i++)documents.Add(store.Create("项目 "+i.ToString("D2")));
    var messages=new List<string>();page=new CanvasProjectsPage(store,(id,paths)=>true,data=>new string[0],data=>new string[0],()=>true,()=>true,()=>false,messages.Add,mature);
    bool visible=false;page.RefreshVisibleForTests=()=>visible;var initial=page.ProjectsForTests.ToDictionary(p=>p.Id,p=>page.ProjectButtonForTests(p.Id));int queries=page.QueryCountForTests;
    for(int i=0;i<50;i++)page.RequestRefresh();Await(page.FlushRefreshForTests());
    check(page.QueryCountForTests==queries&&page.RefreshPendingForTests&&page.ProjectsForTests.Count==30,"hidden project page keeps one dirty flag without reading every autosave notification");
    visible=true;Await(page.FlushRefreshForTests());
    check(page.QueryCountForTests==queries+1&&!page.RefreshPendingForTests,"a burst of 50 pending save notifications becomes one project scan");
    check(initial.All(p=>ReferenceEquals(p.Value,page.ProjectButtonForTests(p.Key))),"unchanged automatic refresh retains every project card and its preview instance");
    Render(page);page.ScrollForTests.ScrollToVerticalOffset(520);page.UpdateLayout();double offset=page.ScrollForTests.VerticalOffset;
    check(offset>0,"project refresh scroll fixture contains multiple screens of cards");
    string changed=page.ProjectsForTests.Last().Id;var document=store.Load(changed);document.Name="更新后的项目";store.Save(document);
    for(int i=0;i<12;i++)page.RequestRefresh();Await(page.FlushRefreshForTests());page.UpdateLayout();
    check(page.ProjectsForTests.Single(p=>p.Id==changed).Name==document.Name&&!ReferenceEquals(initial[changed],page.ProjectButtonForTests(changed)),"updated project metadata replaces only its changed card");
    check(initial.Where(p=>p.Key!=changed).All(p=>ReferenceEquals(p.Value,page.ProjectButtonForTests(p.Key))),"reordering the updated project reuses all other cards instead of clearing the gallery");
    check(Math.Abs(page.ScrollForTests.VerticalOffset-offset)<1,"automatic metadata refresh preserves the current scroll offset");
    var beforeFailure=page.ProjectsForTests;var cardBeforeFailure=page.ProjectButtonForTests(changed);page.ProjectQueryForTests=()=>{throw new IOException("synthetic read failure");};page.RequestRefresh();Await(page.FlushRefreshForTests());
    check(ReferenceEquals(beforeFailure,page.ProjectsForTests)&&ReferenceEquals(cardBeforeFailure,page.ProjectButtonForTests(changed))&&messages.Any(m=>m.Contains("synthetic read failure")),"failed background refresh preserves the last usable project list and reports the error");
    page.ProjectQueryForTests=null;
    using(var entered=new ManualResetEventSlim(false))using(var release=new ManualResetEventSlim(false)){
     page.ProjectQueryForTests=()=>{var result=CanvasProjectsPage.QueryProjects(store,mature);entered.Set();if(!release.Wait(5000))throw new TimeoutException("Stale refresh fixture was not released.");return result;};
     page.RequestRefresh();Task first=page.FlushRefreshForTests();check(entered.Wait(3000),"background project scan begins without running on the UI dispatcher");
     document=store.Load(changed);document.Name="最新项目名";store.Save(document);page.RequestRefresh();int inFlight=page.QueryCountForTests;Await(page.FlushRefreshForTests());
     check(page.QueryCountForTests==inFlight,"a notification during an in-flight refresh does not start a competing project scan");
     release.Set();Await(first);
     check(page.RefreshPendingForTests&&page.ProjectsForTests.Single(p=>p.Id==changed).Name!="最新项目名","a superseded scan is not published over a newer pending change");
    }
    page.ProjectQueryForTests=null;Await(page.FlushRefreshForTests());
    check(page.ProjectsForTests.Single(p=>p.Id==changed).Name=="最新项目名"&&!page.RefreshPendingForTests,"the merged follow-up scan publishes the latest committed project metadata");
    var board=mature.Create("预览稍后到达");page.Refresh();Button beforeThumbnail=page.ProjectButtonForTests(board.Id);string sameUpdated=page.ProjectsForTests.Single(p=>p.Id==board.Id).Updated;
    CanvasProjectThumbnail.SaveMature(mature,board,Thumbnail());page.RequestRefresh();Await(page.FlushRefreshForTests());
    check(page.ProjectsForTests.Single(p=>p.Id==board.Id).Updated==sameUpdated&&!ReferenceEquals(beforeThumbnail,page.ProjectButtonForTests(board.Id)),"a newly saved thumbnail updates its card even when the scene timestamp is unchanged");
    string manifest=Path.Combine(store.Root,changed,"project.json");byte[] bytes=File.ReadAllBytes(manifest);DateTime modified=File.GetLastWriteTimeUtc(manifest);visible=false;
    for(int i=0;i<30;i++)page.RequestRefresh();int hiddenQueries=page.QueryCountForTests;Await(page.FlushRefreshForTests());
    check(page.QueryCountForTests==hiddenQueries&&File.ReadAllBytes(manifest).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(manifest)==modified&&store.List().Count==31,"hidden-page refreshes neither scan nor rewrite saved projects or create extra projects");
   }finally{if(page!=null){page.RefreshVisibleForTests=()=>false;page.Visibility=Visibility.Collapsed;}SynchronizationContext.SetSynchronizationContext(context);}
  }
 }
}
