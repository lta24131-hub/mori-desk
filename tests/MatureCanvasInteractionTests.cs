using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MoriDesk {
 // Native command/bridge regressions only. No WebView process or user window
 // is started; browser replies are exact protocol messages into the real host.
 internal static class MatureCanvasInteractionTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=128*1024*1024,RecursionLimit=64};}
  static Dictionary<string,object> Obj(object value){return (Dictionary<string,object>)value;}
  static Dictionary<string,object> Map(params object[] pairs){var result=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)result.Add((string)pairs[i],pairs[i+1]);return result;}
  static Dictionary<string,object> Scene(MatureCanvasWindow host){return Obj(Json().DeserializeObject(host.DocumentForTests.SceneJson));}
  static Dictionary<string,object> Records(Dictionary<string,object> scene){return Obj(Obj(scene["document"])["store"]);}
  static Task Send(MatureCanvasWindow host,string type,string request,object payload=null,string session=null){return host.HandleMessageAsync(MatureCanvasWindow.Origin+"/index.html",Json().Serialize(Map("protocol",MatureCanvasWindow.Protocol,"type",type,"requestId",request,"session",session??host.SessionForTests,"payload",payload??Map())));}
  static void Wait(Func<bool> completed){DateTime until=DateTime.UtcNow.AddSeconds(25);while(!completed()&&DateTime.UtcNow<until){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(1);}if(!completed())throw new Exception("Mature canvas interaction fixture timed out");}
  static void Wait(Task task){Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
  static void Dispose(MatureCanvasWindow host){host.PostForTests=null;typeof(MatureCanvasWindow).GetField("ready",Hidden).SetValue(host,false);host.Close();}
  static void AddImported(Dictionary<string,object> scene,Dictionary<string,object> image){
   string asset="asset:"+image["id"],shape="shape:fixture-"+image["id"];var records=Records(scene);
   records[asset]=Map("id",asset,"typeName","asset","type","image","meta",Map(),"props",Map("w",image["width"],"h",image["height"],"name",image["name"],"mimeType",image["mimeType"],"src",image["src"],"isAnimated",false));
   records[shape]=Map("id",shape,"typeName","shape","type","image","x",20,"y",40,"rotation",0,"index","a1","parentId","page:page","isLocked",false,"opacity",1,"meta",Map(),"props",Map("w",image["width"],"h",image["height"],"assetId",asset,"playing",true,"url","","crop",null,"flipX",false,"flipY",false));
  }
  static string Source(string root){string file=Path.Combine(root,"synthetic-native.png");using(var bitmap=new System.Drawing.Bitmap(8,6)){using(var paint=System.Drawing.Graphics.FromImage(bitmap))paint.Clear(System.Drawing.Color.MediumPurple);bitmap.Save(file,System.Drawing.Imaging.ImageFormat.Png);}return file;}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
   try{string source=Source(root);byte[] bytes=File.ReadAllBytes(source);DateTime time=File.GetLastWriteTimeUtc(source);SameProject(Path.Combine(root,"same-project"),source,check);Acknowledgements(Path.Combine(root,"acknowledgements"),source,check);Chrome(Path.Combine(root,"chrome"),check);check(File.ReadAllBytes(source).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(source)==time,"native same-project/ACK/chrome regressions preserve original bytes and timestamps");}finally{SynchronizationContext.SetSynchronizationContext(previous);}
  }
  static void SameProject(string root,string source,Action<bool,string> check){
   var host=new MatureCanvasWindow(root);var store=new MatureCanvasStore(root);Dictionary<string,object> browser=null;var sequence=new List<string>();var replies=new List<Task>();bool savedBeforeAdd=false;
   try{
    host.PostForTests=json=>{var output=Obj(Json().DeserializeObject(json));string type=(string)output["type"];var payload=Obj(output["payload"]);
     if(type=="load"){browser=Obj(payload["scene"]);replies.Add(Send(host,"loaded","loaded"));}
     else if(type=="flush"){sequence.Add("flush");replies.Add(Send(host,"save",(string)output["requestId"],Map("scene",browser,"revision",host.RevisionForTests)));}
     else if(type=="addImages"){sequence.Add("add");savedBeforeAdd=store.Load(host.ProjectId).SceneJson.Contains("unsaved-current-edit");var images=(object[])payload["images"];foreach(var image in images)AddImported(browser,Obj(image));replies.Add(Send(host,"imagesAdded",(string)output["requestId"],Map("count",images.Length)));}
    };
    Wait(Send(host,"ready","ready"));Obj(Records(browser)["document:document"])["name"]="unsaved-current-edit";string id=host.ProjectId,session=host.SessionForTests;
    var add=host.OpenProjectAsync(id,new[]{source});Wait(add);foreach(var reply in replies.ToArray())Wait(reply);
    var saved=store.Load(id);check(add.Result&&savedBeforeAdd&&sequence.SequenceEqual(new[]{"flush","add","flush"}),"explicit same-project add flushes unsaved browser edits before staging images and again before reporting success");
    check(saved.ImageCount==1&&saved.SceneJson.Contains("unsaved-current-edit")&&host.ProjectId==id&&host.SessionForTests==session&&host.RevisionForTests==2,"same-project addition preserves edits/session and its success follows durable image-shape save");
   }finally{Dispose(host);}
  }
  static void Acknowledgements(string root,string source,Action<bool,string> check){
   var host=new MatureCanvasWindow(root);var store=new MatureCanvasStore(root);Dictionary<string,object> browser=null;Dictionary<string,object> pending=null;var replies=new List<Task>();int flushes=0;
   try{
    host.PostForTests=json=>{var output=Obj(Json().DeserializeObject(json));string type=(string)output["type"];var payload=Obj(output["payload"]);
     if(type=="load"){browser=Obj(payload["scene"]);replies.Add(Send(host,"loaded","loaded"));}
     else if(type=="flush"){flushes++;replies.Add(Send(host,"save",(string)output["requestId"],Map("scene",browser,"revision",host.RevisionForTests)));}
     else if(type=="addImages")pending=output;
    };
    Wait(Send(host,"ready","ready"));var add=host.OpenProjectAsync(host.ProjectId,new[]{source});Wait(()=>pending!=null);string request=(string)pending["requestId"];
    check(!add.IsCompleted&&!host.CanImportImages&&store.Load(host.ProjectId).ImageCount==0,"native add waits for browser acknowledgement with controls frozen and no fake saved image shape");
    Wait(Send(host,"imagesAdded",request,Map("count",1),"stale-session"));Wait(Send(host,"imagesAdded","unknown-request",Map("count",1)));Wait(Send(host,"imagesAdded",request,Map("count","1")));Wait(Send(host,"imagesAdded",request,Map()));
    check(!add.IsCompleted&&!host.CanImportImages&&flushes==1&&store.Load(host.ProjectId).ImageCount==0,"stale-session, wrong-request, string and missing-count ACKs cannot complete or save the pending import");
    Wait(Send(host,"imagesAdded",request,Map("count",0)));Wait(add);check(!add.Result&&host.CanImportImages&&flushes==1&&store.Load(host.ProjectId).ImageCount==0,"wrong-count ACK ends with explicit failure and cannot report successful saved import");
    Wait(Send(host,"imagesAdded",request,Map("count",1)));check(store.Load(host.ProjectId).ImageCount==0&&flushes==1,"late ACK for a completed failed request cannot create an image or trigger success save");
    pending=null;var retry=host.OpenProjectAsync(host.ProjectId,new[]{source});Wait(()=>pending!=null);foreach(var image in (object[])Obj(pending["payload"])["images"])AddImported(browser,Obj(image));Wait(Send(host,"imagesAdded",(string)pending["requestId"],Map("count",1)));Wait(retry);foreach(var reply in replies.ToArray())Wait(reply);
    check(retry.Result&&store.Load(host.ProjectId).ImageCount==1&&host.CanImportImages&&flushes==3,"fresh matching ACK can complete a later retry only after its own durable flush");
   }finally{Dispose(host);}
  }
  static void Chrome(string root,Action<bool,string> check){
   int settingsCalls=0;var host=new MatureCanvasWindow(root,null,null,null,()=>new Mori.Settings(),()=>settingsCalls++);Dictionary<string,object> browser=null;var replies=new List<Task>();var outputMessages=new List<Dictionary<string,object>>();
   try{
    var layout=(Grid)((Border)host.Content).Child;var titlebar=layout.Children.OfType<DockPanel>().Single(p=>p.Name=="CanvasTitlebar");var buttons=titlebar.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();Func<string,Button> named=name=>buttons.Single(b=>AutomationProperties.GetName(b)==name);
    var pin=named("置顶画布");var tools=named("隐藏工具");var minimize=named("最小化");var close=named("关闭画布");
    check(Array.IndexOf(buttons,pin)+1==Array.IndexOf(buttons,minimize)&&Grid.GetRow(titlebar)==0&&host.WindowStyle==WindowStyle.None,"pin is in the native top titlebar immediately before minimize, not buried among canvas tools");
    check(buttons.All(b=>b.Content is FrameworkElement)&&!pin.IsEnabled&&!tools.IsEnabled&&!named("画布菜单").IsEnabled&&!named("添加图片").IsEnabled&&minimize.IsEnabled&&close.IsEnabled,"titlebar is icon-only and disables project/pin/tool/settings entry during load while window controls stay available");
    host.PostForTests=json=>{var output=Obj(Json().DeserializeObject(json));outputMessages.Add(output);string type=(string)output["type"];var payload=Obj(output["payload"]);if(type=="load"){browser=Obj(payload["scene"]);replies.Add(Send(host,"loaded","loaded"));}else if(type=="flush")replies.Add(Send(host,"save",(string)output["requestId"],Map("scene",browser,"revision",host.RevisionForTests)));};
    Wait(Send(host,"ready","ready"));check(pin.IsEnabled&&tools.IsEnabled&&named("画布菜单").IsEnabled,"matching loaded handshake enables pin/tools/settings entry");
    tools.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var visibility=outputMessages.Last(m=>(string)m["type"]=="toolVisibility");check(!(bool)Obj(visibility["payload"])["visible"]&&AutomationProperties.GetName(tools)=="显示工具"&&(bool)tools.Tag,"hide-tools icon sends exact hidden state and exposes the restore action");
    Wait(Send(host,"toolVisibilityChanged","visibility",Map("visible",true)));check(AutomationProperties.GetName(tools)=="隐藏工具"&&!(bool)tools.Tag,"SDK focus-mode visibility reply restores native icon state without recreating a project");
    pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Wait(()=>host.CanImportImages&&host.Topmost);foreach(var reply in replies.ToArray())Wait(reply);check((bool)pin.Tag&&AutomationProperties.GetName(pin)=="取消置顶"&&new MatureCanvasStore(root).Load(host.ProjectId).Pinned,"pin icon persists the project setting only after current scene flush succeeds");
    typeof(MatureCanvasWindow).GetMethod("ShowCanvasSettings",Hidden).Invoke(host,null);check(settingsCalls==1,"mature-canvas settings entry invokes the supplied app settings action without opening an unrelated modal fixture");
    check(!host.IsVisible,"native chrome protocol fixture never presents a desktop window");
   }finally{Dispose(host);}
  }
 }
}
