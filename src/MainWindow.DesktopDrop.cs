using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  bool desktopImageHover;
  Border desktopDropFeedback;DispatcherTimer desktopDropFeedbackTimer;
  void DesktopDropNotice(string message,bool failure=false,bool pending=false){
   Status(message);
   if(desktopDropFeedback==null){
    var root=Window.Content as Grid;if(root==null)return;
    var label=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=13};label.SetResourceReference(TextBlock.ForegroundProperty,"Text");
    desktopDropFeedback=new Border{Child=label,Padding=new Thickness(16,12,16,12),Margin=new Thickness(24,24,24,48),MaxWidth=390,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,IsHitTestVisible=false};
    desktopDropFeedback.SetResourceReference(Border.BackgroundProperty,"Surface");desktopDropFeedback.SetResourceReference(Border.BorderBrushProperty,"Accent");Grid.SetRowSpan(desktopDropFeedback,3);Panel.SetZIndex(desktopDropFeedback,999);root.Children.Add(desktopDropFeedback);
    desktopDropFeedbackTimer=new DispatcherTimer();desktopDropFeedbackTimer.Tick+=(s,e)=>{desktopDropFeedbackTimer.Stop();desktopDropFeedback.Visibility=Visibility.Collapsed;};Window.Closed+=(s,e)=>desktopDropFeedbackTimer.Stop();
   }
   ((TextBlock)desktopDropFeedback.Child).Text=message;desktopDropFeedback.Visibility=Visibility.Visible;desktopDropFeedbackTimer.Stop();
   if(!pending){desktopDropFeedbackTimer.Interval=TimeSpan.FromSeconds(failure?12:4);desktopDropFeedbackTimer.Start();}
  }
  void DesktopImageOver(object sender,DragEventArgs e){
   if(e.Handled)return;desktopImageHover=DesktopImageDrop.CanRead(e.Data);if(!desktopImageHover)return;
   e.Handled=true;e.Effects=!busy&&(e.AllowedEffects&DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;
   if(!busy)ShowDragHint("收藏这张图片\n松开后保存到素材库，保留独立副本");
  }
  async void DesktopImageDropped(object sender,DragEventArgs e){
   bool recognized=desktopImageHover||DesktopImageDrop.CanRead(e.Data);desktopImageHover=false;HideDragHint();
   if(e.Handled||!recognized)return;
   e.Handled=true;e.Effects=DragDropEffects.None;HideDragHint();
   DesktopDropLog.Write(library.Root,"received",e.Data);
   if(busy){DesktopDropNotice("正在完成其他操作，请稍后再拖入图片。",true);return;}
   if((e.AllowedEffects&DragDropEffects.Copy)==0){DesktopDropNotice("来源软件不允许复制这张图片，未收藏。",true);return;}
   string file=null,phase="capture";
   try{
    // Save edits without opening a modal while the OLE source is waiting.
    if(!SaveClassicPending()){DesktopDropNotice("图片未收藏：请先填写右侧素材名称，再拖入图片。",true);return;}
    var image=DesktopImageDrop.Capture(e.Data);e.Effects=DragDropEffects.Copy;
    DesktopDropLog.Write(library.Root,"captured");
    var node=FolderNode(e.OriginalSource as DependencyObject,Find<TreeView>("Folders"));
    string folder=node!=null?Convert.ToString(node.Tag):bookmarkMode?"":CurrentFolder();
    busy=true;DesktopDropNotice("正在保存图片…",false,true);
    // Ensure the Drop callback returns before network/import work starts.
    await Task.Yield();
    phase="materialize";file=await Task.Run(()=>image.Stage());
    phase="import";
    var result=await Task.Run(()=>library.Import(file,folder,image.Name,image.Source,"","",null,"ask"));
    if(result.ChoiceRequired){bool all;string choice=DuplicateWindow.Ask(Window,library,result.Item,file,false,out all);if(choice=="skip"){DesktopDropLog.Write(library.Root,"cancelled-duplicate");DesktopDropNotice("已取消收藏，原图片未改动。");return;}result=await Task.Run(()=>library.Import(file,folder,image.Name,image.Source,"","",null,choice));}
    history.Clear();DesktopDropLog.Write(library.Root,"saved");
    // A committed image must not be reported as failed if a view refresh fails.
    bool shown=true;try{HideBoard();HideReferences();RevealImported(new[]{result.Item.Id},folder);Refresh(true);}catch(Exception refreshError){shown=false;DesktopDropLog.Write(library.Root,"refresh-failed",null,refreshError);}
    DesktopDropNotice(!shown?"图片已保存，但列表刷新失败，请手动刷新。":result.KeptCopy?"已另存一份图片":result.Duplicate?"已使用图库中的已有图片":"图片已收藏到素材库 · 已保存独立副本");
   }catch(Exception ex){DesktopDropLog.Write(library.Root,phase+"-failed",null,ex);DesktopDropNotice("图片未收藏："+(ex is OperationCanceledException?"下载超时，请复制图片后在 Mori 粘贴。":ex.Message),true);}
   finally{DesktopImageDrop.Cleanup(file);busy=false;}
  }
 }
}
