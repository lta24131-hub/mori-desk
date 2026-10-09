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
  public static event Action Changed;
  static readonly string[] Keys={"Canvas","Panel","Surface","Input","Control","Border","Divider","Text","Muted","Hover","Accent","AccentSoft","Danger","ScrollThumb"};
  static readonly string[] Dark={"#1D1F22","#232529","#27292D","#292C30","#303338","#42464C","#34373C","#E5E7EB","#A0A6AE","#3C4147","#9AC8B6","#344A42","#49373A","#535A62"};
  static readonly string[] Light={"#FAFAF9","#F2F3F1","#FFFFFF","#F7F8F6","#E9ECE8","#D3D8D2","#DFE3DC","#252B29","#656F69","#DEE5DE","#39745D","#DCEBE2","#F3E2E2","#BBC4BD"};
  public static Brush Brush(string key){return (Brush)Application.Current.Resources[key];}
  public static void Set(string value){
   Current=value=="light"?"light":"dark";var colors=Current=="light"?Light:Dark;var resources=Application.Current.Resources;
   for(int i=0;i<Keys.Length;i++){var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));b.Freeze();resources[Keys[i]]=b;}
   resources[SystemColors.HighlightBrushKey]=Brush("AccentSoft");resources[SystemColors.HighlightTextBrushKey]=Brush("Text");
   resources[SystemColors.InactiveSelectionHighlightBrushKey]=Brush("AccentSoft");resources[SystemColors.InactiveSelectionHighlightTextBrushKey]=Brush("Text");
   foreach(Window window in Application.Current.Windows)TitleBar(window);
   if(Changed!=null)Changed();
  }
  public static void Attach(Window window,Window owner){
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
