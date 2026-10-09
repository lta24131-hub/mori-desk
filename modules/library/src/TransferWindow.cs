using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Mori {
 public static class TransferWindow {
  public static bool Run(Window owner,Func<Action<string>,Func<bool>,Action,Task> work) {
   var dialog=new Window{Owner=owner,Title="整体迁移图库",Width=520,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner};
   Theme.Attach(dialog,owner);
   var stack=new StackPanel{Margin=new Thickness(24)};
   var title=new TextBlock{Text="正在迁移图库",FontSize=19,Margin=new Thickness(0,0,0,14)};
   var info=new TextBlock{Text="准备中…",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)};
   var progress=new ProgressBar{IsIndeterminate=true,Height=5,Margin=new Thickness(0,0,0,16)};
   var hint=new TextBlock{Text="原图、回收站、分类和备注一起复制。校验成功后再切换；旧图库会保留。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18)};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
   var cancel=new Button{Content="取消迁移",HorizontalAlignment=HorizontalAlignment.Right,MinWidth=90,Padding=new Thickness(12,7,12,7)};
   int cancelled=0,committing=0;bool finished=false,success=false;
   Action requestCancel=delegate{if(Volatile.Read(ref committing)!=0)return;Interlocked.Exchange(ref cancelled,1);cancel.IsEnabled=false;info.Text="正在取消，原图库不会改变…";};
   cancel.Click+=delegate{requestCancel();};
   dialog.Closing+=delegate(object s,System.ComponentModel.CancelEventArgs e){if(!finished){e.Cancel=true;requestCancel();}};
   dialog.PreviewKeyDown+=delegate(object s,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Escape){requestCancel();e.Handled=true;}};
   stack.Children.Add(title);stack.Children.Add(info);stack.Children.Add(progress);stack.Children.Add(hint);stack.Children.Add(cancel);dialog.Content=stack;
   dialog.Loaded+=async delegate {
    try {
     await work(message=>dialog.Dispatcher.BeginInvoke(new Action(delegate{if(!finished&&Volatile.Read(ref cancelled)==0)info.Text=message;})),()=>Volatile.Read(ref cancelled)!=0,delegate{Interlocked.Exchange(ref committing,1);dialog.Dispatcher.BeginInvoke(new Action(delegate{cancel.IsEnabled=false;info.Text="校验通过，正在打开新图库…";}));});
     success=true;
    }catch(OperationCanceledException){}catch(Exception ex){MessageBox.Show(dialog,ex.Message+"\n\n原图库仍完整保留，请检查当前保存位置后再重试。","迁移未完成");}
    finally{finished=true;dialog.Close();}
   };
   dialog.ShowDialog();return success;
  }
 }
}
