using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

namespace MoriDesk {
 // Only synthetic PNG copies and isolated indexes are used. The real Restore
 // and visibility paths run with disabled, non-activating windows off-screen.
 internal static class PinLifecycleTests {
  sealed class Windows {
   internal readonly List<PinWindow> Items=new List<PinWindow>();
   internal Windows(PinStore store){
    var expected=store.Records.Where(p=>p.Active).ToArray();
    Configure(store,window=>{
     if(Items.Count>=expected.Length)throw new InvalidOperationException("Unexpected extra fixture pin window.");
     var record=expected[Items.Count];
     window.ShowActivated=false;window.IsEnabled=false;window.IsHitTestVisible=false;window.Topmost=false;
     // Keep the original persisted image geometry without letting the normal
     // off-screen recovery fallback place a fixture over the user's desktop.
     window.Left=record.Left-PinFrame.Inset;window.Top=record.Top-PinFrame.Inset;
     Items.Add(window);
    });
   }
   internal void CloseAll(){foreach(var window in Items.ToArray())window.Close();}
  }
  static BitmapSource Picture(byte channel){
   var pixels=new byte[16*12*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=channel;pixels[i+1]=80;pixels[i+2]=140;pixels[i+3]=255;}
   var image=BitmapSource.Create(16,12,96,96,PixelFormats.Bgra32,null,pixels,16*4);image.Freeze();return image;
  }
  static void Configure(PinStore store,Action<PinWindow> action){typeof(PinStore).GetField("ConfigureWindowForTests",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(store,action);}
  static string Geometry(PinRecord record){return string.Join("|",new[]{record.Id,record.Left.ToString("R",System.Globalization.CultureInfo.InvariantCulture),record.Top.ToString("R",System.Globalization.CultureInfo.InvariantCulture),record.Width.ToString("R",System.Globalization.CultureInfo.InvariantCulture),record.Opacity.ToString("R",System.Globalization.CultureInfo.InvariantCulture)});}
  static string[] Geometry(PinStore store){return store.Records.Select(Geometry).OrderBy(value=>value,StringComparer.Ordinal).ToArray();}
  static void CheckFiles(PinStore store,Dictionary<string,byte[]> copies,Action<bool,string> check,string message){check(copies.All(pair=>File.ReadAllBytes(Path.Combine(store.Folder,pair.Key)).SequenceEqual(pair.Value)),message);}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);var app=Application.Current??new Application();var previousMode=app.ShutdownMode;var previousMain=app.MainWindow;var previousPins=AppState.Pins;bool previousQuitting=AppState.Quitting;
   Windows firstWindows=null,restartWindows=null;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   try{
    AppState.Quitting=false;
    var empty=new PinStore(Path.Combine(root,"empty"));check(empty.ToggleVisibility()==null,"pin visibility reports no active windows without manufacturing a new pin");
    var seed=new PinStore(Path.Combine(root,"source"));var one=seed.Add(Picture(25));var two=seed.Add(Picture(170));var closed=seed.Add(Picture(220));
    one.Left=-24000;one.Top=-24000;one.Width=88;one.Opacity=.65;two.Left=-23800;two.Top=-24000;two.Width=116;two.Opacity=.85;closed.Left=-23600;closed.Top=-24000;closed.Width=96;closed.Active=false;closed.ClosedAt=DateTime.UtcNow.Ticks;seed.Save();
    string copied=Path.Combine(root,"copied-session");Directory.CreateDirectory(copied);foreach(var file in Directory.GetFiles(seed.Folder))File.Copy(file,Path.Combine(copied,Path.GetFileName(file)));
    var originalPngs=Directory.GetFiles(copied,"*.png").ToDictionary(Path.GetFileName,File.ReadAllBytes,StringComparer.OrdinalIgnoreCase);
    var store=new PinStore(copied);AppState.Pins=store;string[] geometry=Geometry(store);firstWindows=new Windows(store);store.Restore();
    check(firstWindows.Items.Count==2&&firstWindows.Items.All(window=>window.IsVisible&&!window.ShowActivated),"restart restores every active pin and leaves manually closed history unshown without activating windows");
    check(store.Records.Count==3&&store.Records.Count(record=>record.Active)==2,"restoring multiple pins preserves active versus manually closed records");
    store.Restore();check(firstWindows.Items.Count==2,"repeated restore reuses each live pin window instead of duplicating it");
    check(store.ToggleVisibility()==false&&firstWindows.Items.All(window=>!window.IsVisible),"visibility command hides all restored desktop pins together");store.PersistAll();
    check(new PinStore(copied).Records.Count(record=>record.Active)==2&&Geometry(store).SequenceEqual(geometry),"temporary hide plus persistence keeps both active states and exact image geometry");
    check(store.ToggleVisibility()==true&&firstWindows.Items.All(window=>window.IsVisible),"visibility command shows the same hidden pins together");store.PersistAll();
    check(Geometry(new PinStore(copied)).SequenceEqual(geometry),"hide and show do not reset saved position size or opacity");
    check(store.ToggleVisibility()==false,"pins can remain temporarily hidden at application exit");store.PersistAll();AppState.Quitting=true;firstWindows.CloseAll();
    var restarted=new PinStore(copied);AppState.Pins=restarted;AppState.Quitting=false;restartWindows=new Windows(restarted);restarted.Restore();
    check(restartWindows.Items.Count==2&&restartWindows.Items.All(window=>window.IsVisible)&&restarted.Records.Count(record=>record.Active)==2,"application restart restores all pins even when their previous session ended temporarily hidden");
    check(Geometry(restarted).SequenceEqual(geometry),"restart retains persisted pin image geometry and opacity");
    string manuallyClosed=restarted.Records.Where(record=>record.Active).First().Id;restartWindows.Items[0].Close();
    check(!restarted.Records.Single(record=>record.Id==manuallyClosed).Active&&restarted.Records.Single(record=>record.Id==manuallyClosed).ClosedAt>0,"explicit pin close marks only that image as manually closed and recoverable");
    restarted.Restore();check(restartWindows.Items.Count==2&&restartWindows.Items.Count(window=>window.IsVisible)==1,"restoring after an explicit close never resurrects that manually closed pin");
    check(restarted.ToggleVisibility()==false&&restarted.ToggleVisibility()==true&&restartWindows.Items.Count(window=>window.IsVisible)==1,"batch visibility ignores manually closed pins while preserving the remaining one");
    restarted.PersistAll();AppState.Quitting=true;restartWindows.CloseAll();var final=new PinStore(copied);
    check(final.Records.Count==3&&final.Records.Count(record=>record.Active)==1&&!final.Records.Single(record=>record.Id==closed.Id).Active&&!final.Records.Single(record=>record.Id==manuallyClosed).Active,"next restart preserves one active pin and both independently closed history records");
    CheckFiles(final,originalPngs,check,"hide show close and restart never remove or rewrite any synthetic original PNG");
    check(Directory.GetFiles(seed.Folder,"*.png").All(file=>File.ReadAllBytes(file).SequenceEqual(originalPngs[Path.GetFileName(file)])),"copied-session lifecycle cannot alter its separate source image folder");
    RetryFailedRestore(Path.Combine(root,"restore-retry"),check);
   }finally{
    AppState.Quitting=true;if(firstWindows!=null)firstWindows.CloseAll();if(restartWindows!=null)restartWindows.CloseAll();AppState.Pins=previousPins;AppState.Quitting=previousQuitting;app.MainWindow=previousMain;app.ShutdownMode=previousMode;
   }
  }
  static void RetryFailedRestore(string root,Action<bool,string> check){
   var store=new PinStore(root);var record=store.Add(Picture(105));record.Left=-28000;record.Top=-28000;record.Width=112;record.Opacity=.75;store.Save();string before=Geometry(record);byte[] image=File.ReadAllBytes(store.ImagePath(record));
   var previousNotify=DeskRuntime.Notify;var previousTray=AppState.Tray;int attempts=0,notices=0;PinWindow failed=null,restored=null;AppState.Pins=store;AppState.Quitting=false;
   try{
    DeskRuntime.Notify=message=>notices++;AppState.Tray=null;
    Configure(store,window=>{attempts++;window.ShowActivated=false;window.IsEnabled=false;window.IsHitTestVisible=false;window.Topmost=false;window.Left=record.Left-PinFrame.Inset;window.Top=record.Top-PinFrame.Inset;if(attempts==1){failed=window;throw new IOException("Synthetic pin window setup failure");}restored=window;});
    store.Restore();
    check(attempts==1&&notices==1&&failed!=null&&!Application.Current.Windows.Cast<Window>().Contains(failed),"failed restore discards its half-created window and reports one recoverable failure");
    check(record.Active&&record.ClosedAt==0&&Geometry(record)==before&&new PinStore(root).Records.Single().Active,"failed window setup cannot turn an active pin into manually closed history or change saved geometry");
    store.Restore();check(attempts==2&&restored!=null&&restored.IsVisible,"restore retries successfully after window setup fails instead of activating a stale non-visible dictionary entry");
    store.PersistAll();check(Geometry(record)==before&&File.ReadAllBytes(store.ImagePath(record)).SequenceEqual(image),"successful retry retains the same original pixels and image geometry");
   }finally{AppState.Quitting=true;if(restored!=null)restored.Close();if(failed!=null)failed.Close();DeskRuntime.Notify=previousNotify;AppState.Tray=previousTray;}
  }
 }
}
