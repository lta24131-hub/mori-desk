using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MoriDesk;
namespace Mori {
 public partial class MainWindow {
  BoardPage boardPage;bool boardMode;readonly Dictionary<UIElement,Visibility> boardCovered=new Dictionary<UIElement,Visibility>();Border dragHint;
  void CoverBoard(UIElement element){if(element==null||boardCovered.ContainsKey(element))return;boardCovered[element]=element.Visibility;element.Visibility=Visibility.Collapsed;}
  void ShowBoard(string section){
   if(busy||(!boardMode&&!PrepareLibraryChange()))return;HideCanvasProjects();HideBrowser();HideReferences();
   if(boardPage==null){boardPage=new BoardPage(Window,new BoardStore(library),Status,OpenBoardItem);boardPage.PasteOther=Paste;boardPage.SectionChanged=()=>{if(boardMode)ShowBoard(boardPage.Section);};Grid.SetColumn(boardPage,2);Grid.SetColumnSpan(boardPage,3);Panel.SetZIndex(boardPage,15);Find<Grid>("Workspace").Children.Add(boardPage);}
   if(!boardMode){boardCovered.Clear();foreach(UIElement child in Find<Grid>("Workspace").Children)if(child!=boardPage&&Grid.GetColumn(child)>=2)CoverBoard(child);foreach(string name in new[]{"Import","Paste","AddBookmark","Export","HomeAdd"})CoverBoard(B(name));CoverBoard((UIElement)T("Search").Parent);CoverBoard((UIElement)Find<Slider>("ThumbnailSize").Parent);CoverBoard(Find<TreeView>("Folders"));CoverBoard(Find<Grid>("MediaFolderHeading"));}
   boardMode=true;bookmarkMode=false;mediaSection=section;view="all";selection.Clear();boardPage.Visibility=Visibility.Visible;boardPage.ShowSection(section);referenceNav.Content="快捷收藏";UiIcons.SetGlyph(referenceNav,"\uE71B");UpdateNavigationScope();
   foreach(string name in new[]{"All","Pictures","Videos","Audio","Bookmarks","Documents","Unfiled","Favorites","Trash"})B(name).SetResourceReference(Button.BackgroundProperty,"Panel");string active=section=="all"||section=="assets"?"All":section=="pictures"?"Pictures":section=="videos"?"Videos":section=="audio"?"Audio":section=="documents"?"Documents":section=="unfiled"?"Unfiled":null;if(active!=null)B(active).SetResourceReference(Button.BackgroundProperty,"AccentSoft");referenceNav.SetResourceReference(Button.BackgroundProperty,section=="shortcuts"?"AccentSoft":"Panel");Status("素材桌面 · 分类不移动原文件 · 电脑文件拖入默认添加快捷方式");
  }
  void HideBoard(){if(!boardMode)return;boardMode=false;boardPage.Visibility=Visibility.Collapsed;referenceNav.SetResourceReference(Button.BackgroundProperty,"Panel");foreach(var pair in boardCovered)pair.Key.Visibility=pair.Value;boardCovered.Clear();Window.UpdateLayout();}
  void OpenBoardItem(BoardItem item){if(item.Asset!=null){Preview(item.Asset);return;}FileRouter.Open(Window,item.Path,item.Name);}
  internal static DragDropEffects ShortcutDropEffect(DragDropEffects allowed){return (allowed&DragDropEffects.Link)!=0?DragDropEffects.Link:(allowed&DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;}
  void BoardDragOver(object sender,DragEventArgs e){if(e.Handled||InFileConversion(e.OriginalSource)||canvasProjectsMode||(!Entry.BoardEnabled&&!Entry.ClassicEnabled)||!IsLocalFileDrop(e.Data))return;e.Handled=true;e.Effects=busy?DragDropEffects.None:ShortcutDropEffect(e.AllowedEffects);if(!busy){HighlightLocalCollection(e.OriginalSource as DependencyObject);ShowDragHint();}}
  void ShowDragHint(string message=null){
   if(dragHint==null){var root=Window.Content as Grid;if(root==null)return;var text=new TextBlock{Text=Entry.ClassicEnabled?"松开即收藏\n只保存本地位置，不复制文件":"添加到 Nook\n松开后选择分类框 · 只建快捷方式",FontSize=18,LineHeight=30,TextAlignment=TextAlignment.Center};text.SetResourceReference(TextBlock.ForegroundProperty,"Text");dragHint=new Border{Child=text,Padding=new Thickness(28,20,28,20),CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};dragHint.SetResourceReference(Border.BackgroundProperty,"Surface");dragHint.SetResourceReference(Border.BorderBrushProperty,"Accent");Grid.SetRowSpan(dragHint,3);Panel.SetZIndex(dragHint,1000);root.Children.Add(dragHint);Window.PreviewDragLeave+=(s,e)=>{var p=e.GetPosition(Window);if(p.X<=0||p.Y<=0||p.X>=Window.ActualWidth||p.Y>=Window.ActualHeight)HideDragHint();};Window.Deactivated+=(s,e)=>HideDragHint();Window.PreviewKeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Escape)HideDragHint();};}
   ((TextBlock)dragHint.Child).Text=message??(Entry.ClassicEnabled?"松开即收藏\n只保存本地位置，不复制文件":"添加到 Nook\n松开后选择分类框 · 只建快捷方式");
   dragHint.Visibility=Visibility.Visible;
  }
  void HideDragHint(){if(dragHint!=null)dragHint.Visibility=Visibility.Collapsed;HighlightLocalCollection(null);}
  void OfferBoardPaths(string[] paths,string target){HideDragHint();if(canvasProjectsMode)return;if(busy){Status("请等待当前操作完成后再添加文件。");return;}if(boardPage==null)ShowBoard("all");if(boardPage!=null&&boardPage.OfferFiles(paths,target))ShowBoard(boardPage.Section);}
 }
}
