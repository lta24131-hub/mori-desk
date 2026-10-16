using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mori {
 public partial class Settings {
  string themeAccent="purple",previewBackdrop="automatic",canvasDash="solid",canvasSize="m";
  int mediaVolume=70,canvasSaveDelay=1200,canvasCacheMB=48;
  double canvasZoomSpeed=1;
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
  public bool CanvasGrid {get;set;}
  public bool CanvasShowTools {get;set;}
  public bool CanvasSnap {get;set;}
  public double CanvasZoomSpeed {get{return canvasZoomSpeed;}set{canvasZoomSpeed=double.IsNaN(value)||double.IsInfinity(value)?1:Math.Max(.5,Math.Min(2,value));}}
  public string CanvasDash {get{return canvasDash;}set{canvasDash=NormalizeCanvasChoice(value,new[]{"solid","dashed","dotted","draw"},"solid");}}
  public string CanvasSize {get{return canvasSize;}set{canvasSize=NormalizeCanvasChoice(value,new[]{"s","m","l","xl"},"m");}}
  static string NormalizeCanvasChoice(string value,string[] choices,string fallback){string candidate=(value??"").Trim().ToLowerInvariant();return Array.IndexOf(choices,candidate)>=0?candidate:fallback;}
  void DefaultCustomization(){CloseToTray=BookmarkOpenInApp=AnimateImages=MediaAutoPlay=MediaLoop=CanvasShowTools=true;}
  void AddCustomization(Dictionary<string,object> values){
   values["ThemeAccent"]=ThemeAccent;values["CloseToTray"]=CloseToTray;values["ConfirmTrash"]=ConfirmTrash;values["BookmarkOpenInApp"]=BookmarkOpenInApp;values["AnimateImages"]=AnimateImages;values["PreviewBackdrop"]=PreviewBackdrop;values["MediaVolume"]=MediaVolume;values["MediaMuted"]=MediaMuted;values["MediaAutoPlay"]=MediaAutoPlay;values["MediaLoop"]=MediaLoop;values["CanvasDefaultTopmost"]=CanvasDefaultTopmost;values["CanvasSaveDelay"]=CanvasSaveDelay;values["CanvasCacheMB"]=CanvasCacheMB;
   values["CanvasGrid"]=CanvasGrid;values["CanvasShowTools"]=CanvasShowTools;values["CanvasSnap"]=CanvasSnap;values["CanvasZoomSpeed"]=CanvasZoomSpeed;values["CanvasDash"]=CanvasDash;values["CanvasSize"]=CanvasSize;
  }
  void ReadCustomization(Dictionary<string,object> values){
   object value;int number;double decimalNumber;
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
   if(values.TryGetValue("CanvasGrid",out value)&&value is bool)CanvasGrid=(bool)value;
   if(values.TryGetValue("CanvasShowTools",out value)&&value is bool)CanvasShowTools=(bool)value;
   if(values.TryGetValue("CanvasSnap",out value)&&value is bool)CanvasSnap=(bool)value;
   if(values.TryGetValue("CanvasZoomSpeed",out value)&&value!=null&&double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out decimalNumber))CanvasZoomSpeed=decimalNumber;
   if(values.TryGetValue("CanvasDash",out value))CanvasDash=value as string;
   if(values.TryGetValue("CanvasSize",out value))CanvasSize=value as string;
  }
 }
}
