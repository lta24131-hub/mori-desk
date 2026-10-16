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
 // Preview startup never restores pins or creates native hotkey/bridge services.
 // These lifecycle regressions use synthetic pixels and isolated folders only.
 internal static class PinRuntimeTests {
  const BindingFlags StaticFields=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
  static FieldInfo Field(Type type,string name){return type.GetField(name,StaticFields);}
  static void Set(Type type,string name,object value){Field(type,name).SetValue(null,value);}
  static object Get(Type type,string name){return Field(type,name).GetValue(null);}
  static BitmapSource Picture(){var image=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[]{40,80,120,255,40,80,120,255,40,80,120,255,40,80,120,255},8);image.Freeze();return image;}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);
   var runtime=typeof(DeskRuntime).GetFields(StaticFields).Where(field=>!field.IsLiteral&&!field.IsInitOnly).ToDictionary(field=>field,field=>field.GetValue(null));
   var previousPins=AppState.Pins;var previousSettings=AppState.Settings;bool previousQuitting=AppState.Quitting;var previousHotkeys=AppState.Hotkeys;var previousCaptures=AppState.Captures.ToArray();
   object previousPreferences=Get(typeof(Preferences),"TestFilePath"),previousOcr=Get(typeof(OcrLanguagePacks),"TestFolder"),previousOffline=Get(typeof(OfflinePacks),"TestFolder");
   int windows=Application.Current==null?0:Application.Current.Windows.Count;
   try{
    // Detach, but never dispose, any pre-existing runtime resources. Stop below
    // can therefore release only this preview fixture's own empty service set.
    foreach(string name in new[]{"source","imagePins","lease","retry","failure","CollectImage","Notify","OpenSettings"})Set(typeof(DeskRuntime),name,null);
    foreach(string name in new[]{"Started","ownsPins","awaitingHandoff","<SessionEnding>k__BackingField"})Set(typeof(DeskRuntime),name,false);
    AppState.Hotkeys=null;AppState.Captures.Clear();AppState.Quitting=false;
    string library=Path.Combine(root,"owned-library");DeskRuntime.Start(true,library);
    check(DeskRuntime.Started&&(bool)Get(typeof(DeskRuntime),"ownsPins"),"preview runtime owns its pin store after successful startup");
    check(AppState.Pins.Folder==Path.Combine(library,".mori","capture","pins")&&Convert.ToString(Get(typeof(Preferences),"TestFilePath"))==Path.Combine(library,".mori","test-capture.json"),"preview runtime confines pins and capture preferences to the temporary library");
    check(OcrLanguagePacks.Folder==Path.Combine(library,".mori","test-languages")&&OfflinePacks.Folder==Path.Combine(library,".mori","test-offline"),"preview runtime confines OCR and offline pack paths to temporary data");
    var store=AppState.Pins;var pin=store.Add(Picture());byte[] pixels=File.ReadAllBytes(store.ImagePath(pin));pin.Left=387;pin.Top=219;pin.Width=192;pin.Opacity=.75;
    DeskRuntime.Started=false; // An optional service failed after store ownership.
    check(DeskRuntime.PrepareExit(null)&&AppState.Quitting&&!DeskRuntime.Started,"normal exit still protects pin close handlers after a partially started service fails");
    var saved=new PinStore(store.Folder).Records.Single();
    check(saved.Active&&saved.ClosedAt==0&&saved.Left==387&&saved.Top==219&&saved.Width==192&&saved.Opacity==.75,"normal exit persists active pins and unsaved geometry even with Started false");
    AppState.Quitting=false;pin.Left=418;pin.Width=208;DeskRuntime.PrepareSessionEnd();
    check(DeskRuntime.SessionEnding&&AppState.Quitting&&!DeskRuntime.Started,"system session ending protects active pins even with Started false");
    saved=new PinStore(store.Folder).Records.Single();
    check(saved.Active&&saved.ClosedAt==0&&saved.Left==418&&saved.Width==208,"system session ending persists current active pin geometry after partial startup failure");
    check(File.ReadAllBytes(store.ImagePath(pin)).SequenceEqual(pixels),"runtime exit and system-ending persistence leave original synthetic PNG bytes unchanged");
    DeskRuntime.Stop();
    check(!DeskRuntime.Started&&!DeskRuntime.SessionEnding&&!(bool)Get(typeof(DeskRuntime),"ownsPins"),"stopping runtime relinquishes pin ownership and clears session-ending state");
    var foreign=new PinStore(Path.Combine(root,"other-owner"));var other=foreign.Add(Picture());string manifest=Path.Combine(foreign.Folder,"pins.json");byte[] original=File.ReadAllBytes(manifest);other.Left=699;other.Active=false;other.ClosedAt=123;
    AppState.Pins=foreign;AppState.Quitting=false;DeskRuntime.PrepareSessionEnd();
    check(!AppState.Quitting&&!DeskRuntime.SessionEnding&&File.ReadAllBytes(manifest).SequenceEqual(original),"a stopped runtime cannot mark session ending or overwrite another owner's current pin store");
    check(DeskRuntime.PrepareExit(null)&&!AppState.Quitting&&File.ReadAllBytes(manifest).SequenceEqual(original),"a stopped runtime's normal exit also leaves another owner's store untouched");
    saved=new PinStore(foreign.Folder).Records.Single();
    check(saved.Active&&saved.ClosedAt==0&&saved.Left!=699,"another owner's persisted active pin metadata survives stale lifecycle callbacks");
    DeskRuntime.Start(true,Path.Combine(root,"next-library"));
    check(DeskRuntime.Started&&!DeskRuntime.SessionEnding&&!AppState.Quitting&&!ReferenceEquals(AppState.Pins,foreign),"a later preview startup reacquires only its new store with clean shutdown flags");
    check((Application.Current==null?0:Application.Current.Windows.Count)==windows,"pin runtime lifecycle tests never create or show a window");
   }finally{
    DeskRuntime.Stop();AppState.Pins=previousPins;AppState.Settings=previousSettings;AppState.Quitting=previousQuitting;AppState.Hotkeys=previousHotkeys;AppState.Captures.Clear();AppState.Captures.AddRange(previousCaptures);
    Set(typeof(Preferences),"TestFilePath",previousPreferences);Set(typeof(OcrLanguagePacks),"TestFolder",previousOcr);Set(typeof(OfflinePacks),"TestFolder",previousOffline);
    foreach(var pair in runtime)pair.Key.SetValue(null,pair.Value);
   }
  }
 }
}
