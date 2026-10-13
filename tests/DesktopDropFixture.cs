using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 // An opt-in, isolated UI fixture for testing the real Windows OLE route.
 // No input injection: a tester drags each source into the normal gallery.
 internal static class DesktopDropFixture {
  public static int Run(string root){
   root=Path.GetFullPath(root);Directory.CreateDirectory(root);Entry.ClassicEnabled=true;
   var app=new Application();app.ShutdownMode=ShutdownMode.OnMainWindowClose;
   using(var lib=new Library(root)){
    var main=new MainWindow(lib){PreviewMode=true};main.Window.Title="Mori · 桌面拖入专项测试（独立图库）";
    var panel=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,62,0,0)};
    var grid=(Grid)main.Window.Content;Grid.SetRowSpan(panel,3);Panel.SetZIndex(panel,900);grid.Children.Add(panel);
    Add(panel,"拖我：PNG 图片",()=>Payload(1));Add(panel,"拖我：虚拟图片",()=>Payload(2));Add(panel,"拖我：格式回退",()=>Payload(3));Add(panel,"拖我：无效图片",()=>new DataObject("PNG",new byte[]{1,2,3}));
    app.Run(main.Window);
   }
   return 0;
  }
  static void Add(Panel panel,string label,Func<DataObject> payload){
   var source=new Border{Background=Brushes.DarkSlateGray,Padding=new Thickness(12),Margin=new Thickness(5),Cursor=Cursors.Hand,Child=new TextBlock{Text=label,Foreground=Brushes.White}};Point start=new Point();
   source.PreviewMouseLeftButtonDown+=(s,e)=>{start=e.GetPosition(source);source.CaptureMouse();e.Handled=true;};
   source.PreviewMouseLeftButtonUp+=(s,e)=>source.ReleaseMouseCapture();
   source.PreviewMouseMove+=(s,e)=>{if(!source.IsMouseCaptured||e.LeftButton!=MouseButtonState.Pressed||(e.GetPosition(source)-start).Length<8)return;e.Handled=true;source.ReleaseMouseCapture();DragDrop.DoDragDrop(source,payload(),DragDropEffects.Copy);};panel.Children.Add(source);
  }
  static DataObject Payload(int kind){
   byte[] pixels=new byte[320*220*4];for(int y=0;y<220;y++)for(int x=0;x<320;x++){int i=(y*320+x)*4;pixels[i]=(byte)(x*255/320);pixels[i+1]=(byte)(y*255/220);pixels[i+2]=(byte)(kind*65);pixels[i+3]=255;}
   var bitmap=BitmapSource.Create(320,220,96,96,PixelFormats.Bgra32,null,pixels,320*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));byte[] png;using(var output=new MemoryStream()){encoder.Save(output);png=output.ToArray();}
   var data=new DataObject();
   if(kind==1)data.SetData("PNG",new MemoryStream(png));
   else if(kind==2){var descriptor=new byte[596];BitConverter.GetBytes(1).CopyTo(descriptor,0);Encoding.Unicode.GetBytes("虚拟图片.png\0").CopyTo(descriptor,76);data.SetData("FileGroupDescriptorW",new MemoryStream(descriptor));data.SetData("FileContents",new MemoryStream(png));}
   else{data.SetData("PNG",new byte[]{1,2,3});data.SetData("image/png",new MemoryStream(png));}
   return data;
  }
 }
}
