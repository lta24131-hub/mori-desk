using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MoriDesk;
namespace Mori {
 public partial class MainWindow {
  ReferencePage referencePage;bool referenceMode;Button deskTools;readonly System.Collections.Generic.Dictionary<UIElement,Visibility> referenceCovered=new System.Collections.Generic.Dictionary<UIElement,Visibility>();
  void InitializeDesk(){
   Window.Title="Mori Desk · 素材与工具";
   deskTools=new Button{Content="工具",ToolTip="截图 · 输入翻译 · 看图 · 3D 查看",Style=B("More").Style};UiIcons.SetGlyph(deskTools,"\uE713");var actions=(Panel)B("More").Parent;actions.Children.Insert(actions.Children.IndexOf(B("More")),deskTools);deskTools.Click+=delegate{ToolMenu(deskTools);};
   var nav=new Button{Content="本地文件",ToolTip="仅收藏文件路径，不复制原件",Style=B("All").Style};UiIcons.SetGlyph(nav,"\uE8B7");var navigation=(Panel)B("All").Parent;navigation.Children.Insert(navigation.Children.IndexOf(B("Documents"))+1,nav);nav.Click+=delegate{ShowReferences();};
   Window.Loaded+=delegate{if(!Entry.InteractiveShell)return;DeskRuntime.Notify=Status;DeskRuntime.CollectImage=CollectScreenshot;DeskRuntime.Start(PreviewMode,library.Root);Application.Current.ShutdownMode=ShutdownMode.OnMainWindowClose;};
   Window.Closing+=delegate(object s,System.ComponentModel.CancelEventArgs e){if(!e.Cancel&&!DeskRuntime.PrepareExit(Window))e.Cancel=true;};
   Window.Closed+=delegate{DeskRuntime.Stop();};
  }
  void ShowReferences(){if(busy||!PrepareLibraryChange())return;if(referencePage==null){referencePage=new ReferencePage(Window,new ReferenceStore(library.Db),HideReferences,Status,(path,name)=>FileRouter.Open(Window,path,name));Grid.SetColumnSpan(referencePage,5);Panel.SetZIndex(referencePage,10);Find<Grid>("Workspace").Children.Add(referencePage);}if(!referenceMode){referenceCovered.Clear();foreach(UIElement child in Find<Grid>("Workspace").Children)if(child!=referencePage){referenceCovered[child]=child.Visibility;child.Visibility=Visibility.Hidden;}}referenceMode=true;referencePage.Visibility=Visibility.Visible;referencePage.Refresh();foreach(string name in new[]{"Import","Paste","AddBookmark","Export","More"})B(name).IsEnabled=false;T("Search").IsEnabled=false;Status("本地收藏只记路径 · 不复制、不移动、不删除原件");}
  void HideReferences(){referenceMode=false;if(referencePage!=null)referencePage.Visibility=Visibility.Collapsed;foreach(var child in referenceCovered)child.Key.Visibility=child.Value;referenceCovered.Clear();foreach(string name in new[]{"Import","Paste","AddBookmark","More"})B(name).IsEnabled=true;T("Search").IsEnabled=true;Refresh(false);Status("素材库保存副本；本地文件保存快捷引用。");}
  void ToolMenu(Button anchor){var menu=Theme.Menu(Window);Action<string,Action> add=(title,action)=>{var item=new MenuItem{Header=title};item.Click+=delegate{action();};menu.Items.Add(item);};add("截图 / 标注 / 原位翻译",()=>{HideToTrayForCapture();QingJie.AppState.BeginCapture();});add("输入翻译",QingJie.AppState.ShowTranslator);add("粘贴 / 找回贴图",()=>QingJie.AppState.Paste());menu.Items.Add(new Separator());add("打开图片…（不收藏）",()=>FileRouter.Choose(Window,false));add("打开 3D 模型…（不收藏）",()=>FileRouter.Choose(Window,true));add("本地文件快捷收藏",ShowReferences);menu.PlacementTarget=anchor;menu.IsOpen=true;}
  void HideToTrayForCapture(){if(!PreviewMode)HideToTray();}
  void ShowDeskSettings(){if(busy){Status("当前操作完成后再打开设置。");return;}var w=new Window{Title="Mori Desk · 设置",Owner=Window,Width=510,Height=420,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};Theme.Attach(w,Window);var p=new StackPanel{Margin=new Thickness(24)};p.Children.Add(new TextBlock{Text="统一设置",FontSize=23,Margin=new Thickness(4,0,0,18)});p.Children.Add(ReferencePage.Button("素材库 · 保存位置 / 外观 / 开机启动",()=>{w.Close();ShowLibrarySettings();}));p.Children.Add(ReferencePage.Button("截图与翻译 · 快捷键 / 服务 / 语言包",()=>{w.Close();QingJie.AppState.ShowSettings();}));p.Children.Add(new TextBlock{Text="本地文件：保存路径和分类，不包含原文件。\n素材库：导入时复制文件，原文件保持不动。\n3D 查看器：使用时启动，关闭后释放独立进程。\n\n这是独立测试版，不读取旧 Mori / Luma 数据。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,18,4,0),LineHeight=23});w.Content=p;w.ShowDialog();}
  async Task<bool> CollectScreenshot(BitmapSource bitmap){if(busy){QingJie.AppState.Notify("素材库正在处理文件，请稍后再收藏。");return false;}busy=true;string file=Path.Combine(Path.GetTempPath(),"MoriDesk-"+Guid.NewGuid().ToString("N")+".png");try{var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(file))encoder.Save(stream);var result=await Task.Run(()=>library.Import(file,"","截图 "+DateTime.Now.ToString("yyyy-MM-dd HHmmss"),"","","","","skip"));history.Clear();Status(result.Duplicate?"图库已有这张截图":"截图已收藏到素材库");if(Window.IsVisible)Refresh(false);return true;}catch(Exception ex){MessageBox.Show("收藏失败，截图仍可继续编辑或保存："+ex.Message,"Mori Desk");return false;}finally{if(File.Exists(file))File.Delete(file);busy=false;}}
 }
}
