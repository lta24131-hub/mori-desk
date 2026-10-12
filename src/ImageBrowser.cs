using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Forms.Integration;
using Mori;
using Forms=System.Windows.Forms;
namespace MoriDesk {
 public sealed class ImageBrowser:Window {
  readonly DockPanel root=new DockPanel();readonly TextBlock label=new TextBlock{Margin=new Thickness(12,8,12,8)};
  string[] siblings;int index;PreviewImage image;WindowsFormsHost host;PreviewDragController drag;PreviewViewport viewport;PreviewPicture picture;bool loading,closed;
  string currentPath;CancellationTokenSource scan;int directoryVersion;readonly Button previous,next;bool scanning;
  public static int Run(string path){try{var app=new Application();Theme.Set(Settings.Load().Theme);var w=new ImageBrowser(path);app.Run(w);return 0;}catch(Exception ex){MessageBox.Show(ex.Message,"Mori Desk · 看图");return 1;}}
  public ImageBrowser(string path){Width=1100;Height=790;MinWidth=650;MinHeight=420;Title="Mori Desk · 看图";Theme.Attach(this,null);WindowStartupLocation=WindowStartupLocation.CenterScreen;Content=root;
   var bar=new WrapPanel{Margin=new Thickness(8)};bar.Children.Add(ReferencePage.Button("打开…",()=>{var d=new Microsoft.Win32.OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.svg;*.webp;*.avif;*.tif;*.tiff;*.ico"};if(d.ShowDialog(this)==true)Open(d.FileName);}));previous=ReferencePage.Button("‹ 上一张",()=>Step(-1));next=ReferencePage.Button("下一张 ›",()=>Step(1));previous.IsEnabled=next.IsEnabled=false;bar.Children.Add(previous);bar.Children.Add(next);bar.Children.Add(ReferencePage.Button("适应窗口",()=>{if(viewport!=null){viewport.Fit();picture.Invalidate();}}));bar.Children.Add(ReferencePage.Button("识别 / 翻译",()=>{if(image==null||loading)return;try{new QingJie.TextWindow(QingJie.Snapshot.Load(currentPath)).Show();}catch(Exception ex){MessageBox.Show(this,ex.Message);}}));bar.Children.Add(ReferencePage.Button("文件位置",()=>{if(currentPath!=null)FileRouter.Reveal(currentPath);}));DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);DockPanel.SetDock(label,Dock.Bottom);root.Children.Add(label);
   PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}else if(e.Key==Key.Left){Step(-1);e.Handled=true;}else if(e.Key==Key.Right){Step(1);e.Handled=true;}};
   AllowDrop=true;DragOver+=(s,e)=>{e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};Drop+=(s,e)=>{var paths=e.Data.GetData(DataFormats.FileDrop)as string[];if(paths!=null&&paths.Length>0)Open(paths[0]);e.Handled=true;};Closed+=(s,e)=>{closed=true;directoryVersion++;if(scan!=null)scan.Cancel();Release();};Loaded+=(s,e)=>{if(path!=null)Open(path);else label.Text="拖入图片查看；不会自动收藏到素材库。";};
  }
  void Release(){if(drag!=null){drag.Dispose();drag=null;}if(host!=null){root.Children.Remove(host);host.Dispose();host=null;}if(image!=null){image.Dispose();image=null;}viewport=null;picture=null;}
  async void Open(string path){if(loading)return;try{path=Path.GetFullPath(path);if(FileRouter.Kind(path)!="image")throw new NotSupportedException("请选择支持的图片格式。");if(scan!=null)scan.Cancel();scan=new CancellationTokenSource();var token=scan.Token;int stamp=++directoryVersion;currentPath=path;siblings=new[]{path};index=0;scanning=true;previous.IsEnabled=next.IsEnabled=false;
   // Decode the requested file first. Folder discovery must never gate first paint.
   await Load();if(closed||stamp!=directoryVersion)return;
   try{var list=await Task.Run(()=>ImageDirectory.Read(path,token));if(closed||stamp!=directoryVersion)return;siblings=list;index=Array.FindIndex(siblings,p=>string.Equals(p,currentPath,StringComparison.OrdinalIgnoreCase));}catch(OperationCanceledException){return;}catch(IOException){}catch(UnauthorizedAccessException){}
   if(!closed&&stamp==directoryVersion){scanning=false;UpdateInfo();}
  }catch(Exception ex){label.Text="无法预览："+ex.Message;}}
  async void Step(int step){if(loading||scanning||siblings==null||siblings.Length<2)return;index=(index+step+siblings.Length)%siblings.Length;currentPath=siblings[index];await Load();}
  void UpdateInfo(){previous.IsEnabled=next.IsEnabled=!loading&&!scanning&&siblings!=null&&siblings.Length>1;if(image!=null)label.Text=(index+1)+" / "+siblings.Length+"    "+image.Image.Width+" × "+image.Image.Height+"    滚轮缩放 · 拖动查看 · 双击关闭 · 未收藏"+(scanning?" · 正在读取同目录列表…":"");}
  async Task Load(){loading=true;previous.IsEnabled=next.IsEnabled=false;Release();label.Text="正在打开…";try{string path=currentPath;var loaded=await Task.Run(()=>PreviewImage.Open(path));if(closed){loaded.Dispose();return;}image=loaded;viewport=new PreviewViewport(image.Image.Width,image.Image.Height);var canvas=new PreviewCanvas{Dock=Forms.DockStyle.Fill,BackColor=Theme.Current=="light"?System.Drawing.Color.FromArgb(250,250,249):System.Drawing.Color.FromArgb(29,31,34)};picture=new PreviewPicture(image.Image,viewport){BackColor=canvas.BackColor};canvas.Controls.Add(picture);host=new WindowsFormsHost{Child=canvas};root.Children.Add(host);Action draw=()=>{picture.Invalidate();if(drag!=null)drag.UpdateCursor();};drag=new PreviewDragController(canvas,picture,viewport,draw);Forms.MouseEventHandler wheel=(s,e)=>{if(e.Delta==0)return;var point=canvas.PointToClient(((Forms.Control)s).PointToScreen(e.Location));drag.Cancel();viewport.ZoomAt(Math.Pow(1.15,e.Delta/120.0),point.X,point.Y);draw();};canvas.MouseWheel+=wheel;picture.MouseWheel+=wheel;picture.MouseEnter+=(s,e)=>canvas.Focus();canvas.Resize+=(s,e)=>{viewport.Resize(canvas.ClientSize.Width,canvas.ClientSize.Height);draw();};canvas.KeyDown+=(s,e)=>{if(e.KeyCode==Forms.Keys.Left)Step(-1);if(e.KeyCode==Forms.Keys.Right)Step(1);if(e.KeyCode==Forms.Keys.Escape)Close();};PreviewWindow.AttachDoubleClickClose(picture,drag,Close);UpdateLayout();viewport.Resize(canvas.ClientSize.Width,canvas.ClientSize.Height);draw();Title=Path.GetFileName(path)+" · Mori Desk";UpdateInfo();}catch(Exception ex){label.Text="无法预览："+ex.Message;}finally{loading=false;if(!closed)UpdateInfo();}}
 }
}
