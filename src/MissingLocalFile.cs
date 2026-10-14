using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MoriDesk {
 internal enum MissingLocalFileAction { Keep, Relocate, RemoveShortcut }

 // Only check the requested path. Never scan other folders or assume that an
 // unavailable disk has been deleted.
 internal static class MissingLocalFile {
  internal static bool Exists(string path,bool directory){return directory?Directory.Exists(path):File.Exists(path);}
  internal static string Explanation(bool directory){return (directory?"原文件夹":"原文件")+"已移动、重命名、删除，或所在磁盘暂时不可用。\n收藏和信息仍然保留，可以重新定位后继续打开。";}
  internal static Window Create(Window owner,string path,bool directory,Action<MissingLocalFileAction> choose){
   var window=new Window{Owner=owner,Title="原文件暂不可用",Width=520,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner};
   Mori.Theme.Attach(window,owner);
   var body=new StackPanel{Margin=new Thickness(22)};window.Content=body;
   body.Children.Add(new TextBlock{Text="找不到原"+(directory?"文件夹":"文件"),FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
   body.Children.Add(new TextBlock{Text=Explanation(directory),TextWrapping=TextWrapping.Wrap,LineHeight=22,Margin=new Thickness(0,0,0,12)});
   var address=new TextBox{Text=path??"",IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=96,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(8),Margin=new Thickness(0,0,0,12)};address.SetResourceReference(Control.ForegroundProperty,"Muted");address.SetResourceReference(Control.BackgroundProperty,"Input");address.SetResourceReference(Control.BorderBrushProperty,"Border");body.Children.Add(address);
   body.Children.Add(new TextBlock{Text="仅移除收藏不会删除原文件，Ctrl+Z 可恢复收藏。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
   var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};body.Children.Add(buttons);
   Action<string,MissingLocalFileAction,bool> add=(label,result,cancel)=>{var button=ReferencePage.Button(label,()=>{choose(result);window.Close();});button.Tag=result;button.IsCancel=cancel;if(result==MissingLocalFileAction.Relocate)button.IsDefault=true;buttons.Children.Add(button);};
   add("保留收藏",MissingLocalFileAction.Keep,true);add("仅移除收藏",MissingLocalFileAction.RemoveShortcut,false);add("重新定位…",MissingLocalFileAction.Relocate,false);
   window.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){choose(MissingLocalFileAction.Keep);window.Close();e.Handled=true;}};
   return window;
  }
  internal static MissingLocalFileAction Show(Window owner,string path,bool directory){var result=MissingLocalFileAction.Keep;Create(owner,path,directory,choice=>result=choice).ShowDialog();return result;}
 }
}
