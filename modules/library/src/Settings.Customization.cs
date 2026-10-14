using System;
using System.Collections.Generic;

namespace Mori {
 public partial class Settings {
  string themeAccent="purple",previewBackdrop="automatic";
  int mediaVolume=70,canvasSaveDelay=1200,canvasCacheMB=48;
  public string ThemeAccent {get{return themeAccent;}set{themeAccent=Mori.Theme.NormalizeAccent(value);}}
  public bool CloseToTray {get;set;}
  public bool ConfirmTrash {get;set;}
  public bool BookmarkOpenInApp {get;set;}
  public bool AnimateImages {get;set;}
  public string PreviewBackdrop {get{return previewBackdrop;}set{previewBackdrop=Array.IndexOf(new[]{"automatic","light-checker","dark-checker","white","dark"},value)>=0?value:"automatic";}}
  public int MediaVolume {get{return mediaVolume;}set{mediaVolume=Math.Max(0,Math.Min(100,value));}}
  public bool MediaMuted {get;set;}
  public bool MediaAutoPlay {get;set;}
  public bool MediaLoop {get;set;}
  public bool CanvasDefaultTopmost {get;set;}
  public int CanvasSaveDelay {get{return canvasSaveDelay;}set{canvasSaveDelay=Math.Max(500,Math.Min(10000,value));}}
  public int CanvasCacheMB {get{return canvasCacheMB;}set{canvasCacheMB=Math.Max(16,Math.Min(128,value));}}
  void DefaultCustomization(){CloseToTray=BookmarkOpenInApp=AnimateImages=MediaAutoPlay=MediaLoop=true;}
  void AddCustomization(Dictionary<string,object> values){
   values["ThemeAccent"]=ThemeAccent;values["CloseToTray"]=CloseToTray;values["ConfirmTrash"]=ConfirmTrash;values["BookmarkOpenInApp"]=BookmarkOpenInApp;values["AnimateImages"]=AnimateImages;values["PreviewBackdrop"]=PreviewBackdrop;values["MediaVolume"]=MediaVolume;values["MediaMuted"]=MediaMuted;values["MediaAutoPlay"]=MediaAutoPlay;values["MediaLoop"]=MediaLoop;values["CanvasDefaultTopmost"]=CanvasDefaultTopmost;values["CanvasSaveDelay"]=CanvasSaveDelay;values["CanvasCacheMB"]=CanvasCacheMB;
  }
  void ReadCustomization(Dictionary<string,object> values){
   object value;int number;
   if(values.TryGetValue("ThemeAccent",out value))ThemeAccent=value as string;
   if(values.TryGetValue("PreviewBackdrop",out value))PreviewBackdrop=value as string;
   if(values.TryGetValue("MediaVolume",out value)&&value!=null&&int.TryParse(Convert.ToString(value),out number))MediaVolume=number;
   if(values.TryGetValue("CanvasSaveDelay",out value)&&value!=null&&int.TryParse(Convert.ToString(value),out number))CanvasSaveDelay=number;
   if(values.TryGetValue("CanvasCacheMB",out value)&&value!=null&&int.TryParse(Convert.ToString(value),out number))CanvasCacheMB=number;
   if(values.TryGetValue("CloseToTray",out value)&&value is bool)CloseToTray=(bool)value;
   if(values.TryGetValue("ConfirmTrash",out value)&&value is bool)ConfirmTrash=(bool)value;
   if(values.TryGetValue("BookmarkOpenInApp",out value)&&value is bool)BookmarkOpenInApp=(bool)value;
   if(values.TryGetValue("AnimateImages",out value)&&value is bool)AnimateImages=(bool)value;
   if(values.TryGetValue("MediaMuted",out value)&&value is bool)MediaMuted=(bool)value;
   if(values.TryGetValue("MediaAutoPlay",out value)&&value is bool)MediaAutoPlay=(bool)value;
   if(values.TryGetValue("MediaLoop",out value)&&value is bool)MediaLoop=(bool)value;
   if(values.TryGetValue("CanvasDefaultTopmost",out value)&&value is bool)CanvasDefaultTopmost=(bool)value;
  }
 }
}
