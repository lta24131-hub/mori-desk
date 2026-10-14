using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Mori {
 public static class Theme {
  public static string Current {get;private set;}
  static string accentKey="purple";
  public static string AccentKey {get{return accentKey;}}
  internal static long Revision {get;private set;}
  public static event Action Changed;
  static readonly string[] Keys={"Canvas","Panel","Surface","Input","Control","Border","Divider","Text","Muted","Hover","Accent","AccentSoft","Danger","ScrollThumb","Primary","OnPrimary","ErrorText"};
  // White/black/grey are the canvas; accent is reserved for explicit interaction.
  // Equal RGB channels keep ordinary surfaces neutral, including native hosts.
  static readonly string[] Dark={"#161616","#1D1D1D","#242424","#292929","#303030","#484848","#363636","#EEEEEE","#AAAAAA","#3B3B3B","#B7A8ED","#322D40","#442B31","#626262","#B7A8ED","#161616","#F399AB"};
  static readonly string[] Light={"#FAFAFA","#F3F3F3","#FFFFFF","#FAFAFA","#EEEEEE","#D7D7D7","#E4E4E4","#252525","#626262","#E7E7E7","#6D5BC7","#F0EDFA","#F8E9EC","#B7B7B7","#6D5BC7","#FFFFFF","#A63F50"};
  static readonly string[] AccentNames={"purple","blue","teal","amber","rose","mono"};
  // Accent / selected tint. Each pair supports normal-size text at 4.5:1.
  static readonly string[][] LightAccents={new[]{"#6D5BC7","#F0EDFA"},new[]{"#2764B0","#EAF1FB"},new[]{"#216F69","#E7F3F0"},new[]{"#875713","#F8F0DF"},new[]{"#A54E6D","#F8EAF0"},new[]{"#555555","#EAEAEA"}};
  static readonly string[][] DarkAccents={new[]{"#B7A8ED","#322D40"},new[]{"#9BBDEB","#253448"},new[]{"#8CC8BF","#243B36"},new[]{"#E6BD75","#403521"},new[]{"#E7A7BD","#422C35"},new[]{"#BBBBBB","#353535"}};
  public static Brush Brush(string key){return (Brush)Application.Current.Resources[key];}
  internal static System.Drawing.Color DrawingColor(string key){var color=((SolidColorBrush)Brush(key)).Color;return System.Drawing.Color.FromArgb(color.A,color.R,color.G,color.B);}
  public static string NormalizeAccent(string value){string key=(value??"").Trim().ToLowerInvariant();return Array.IndexOf(AccentNames,key)>=0?key:"purple";}
  internal static Color AccentColor(string value,string mode){int index=Array.IndexOf(AccentNames,NormalizeAccent(value));return (Color)ColorConverter.ConvertFromString((mode=="light"?LightAccents:DarkAccents)[index][0]);}
  public static void Set(string value){Update(value,accentKey);}
  public static void SetAccent(string value){Update(Current,value);}
  public static void Apply(Settings settings){if(settings==null)throw new ArgumentNullException("settings");Update(settings.Theme,settings.ThemeAccent);}
  static void Update(string value,string accent){
   Current=String.Equals(value,"light",StringComparison.OrdinalIgnoreCase)?"light":"dark";accentKey=NormalizeAccent(accent);
   var colors=(string[])(Current=="light"?Light:Dark).Clone();var selected=(Current=="light"?LightAccents:DarkAccents)[Array.IndexOf(AccentNames,accentKey)];
   colors[10]=selected[0];colors[11]=selected[1];colors[14]=selected[0];var resources=Application.Current.Resources;
   for(int i=0;i<Keys.Length;i++){var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));b.Freeze();resources[Keys[i]]=b;}
   resources[SystemColors.HighlightBrushKey]=Brush("AccentSoft");resources[SystemColors.HighlightTextBrushKey]=Brush("Text");
   resources[SystemColors.InactiveSelectionHighlightBrushKey]=Brush("AccentSoft");resources[SystemColors.InactiveSelectionHighlightTextBrushKey]=Brush("Text");
   foreach(Window window in Application.Current.Windows)TitleBar(window);
   Revision++;
   if(Changed!=null)Changed();
  }
  public static void Attach(Window window,Window owner){
   if(window.Icon==null)window.Icon=QingJie.Ui.AppIcon;
   if(owner!=null)window.Resources.MergedDictionaries.Add(owner.Resources);
   window.SetResourceReference(Window.BackgroundProperty,"Canvas");window.SetResourceReference(Window.ForegroundProperty,"Text");
   window.FontFamily=new FontFamily("Microsoft YaHei UI");window.FontSize=13;
   window.SourceInitialized+=delegate{TitleBar(window);};
  }
  public static ContextMenu Menu(Window owner){var menu=new ContextMenu();menu.Resources.MergedDictionaries.Add(owner.Resources);return menu;}
  [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
  static void TitleBar(Window window){try{var handle=new WindowInteropHelper(window).Handle;if(handle==IntPtr.Zero)return;int dark=Current=="dark"?1:0;if(DwmSetWindowAttribute(handle,20,ref dark,4)!=0)DwmSetWindowAttribute(handle,19,ref dark,4);var c=((SolidColorBrush)Brush("Panel")).Color;int rgb=c.R|(c.G<<8)|(c.B<<16);DwmSetWindowAttribute(handle,35,ref rgb,4);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}}
 }
}
