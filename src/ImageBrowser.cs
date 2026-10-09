using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
  public static int Run(string path){try{var app=new Application();Theme.Set(Settings.Load().Theme);var w=new ImageBrowser(path);app.Run(w);return 0;}catch(Exception ex){MessageBox.Show(ex.Message,"Mori Desk · 看图");return 1;}}
  public ImageBrowser(string path){Width=1100;Height=790;MinWidth=650;MinHeight=420;Title="Mori Desk · 看图";Theme.Attach(this,null);WindowStartupLocation=WindowStartupLocation.CenterScreen;Content=root;
   var bar=new WrapPanel{Margin=new Thickness(8)};bar.Children.Add(ReferencePage.Button("打开…",()=>{var d=new Microsoft.Win32.OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.svg;*.webp;*.avif;*.tif;*.tiff;*.ico"};if(d.ShowDialog(this)==true)Open(d.FileName);}));bar.Children.Add(ReferencePage.Button("‹ 上一张",()=>Step(-1)));bar.Children.Add(ReferencePage.Button("下一张 ›",()=>Step(1)));bar.Children.Add(ReferencePage.Button("适应窗口",()=>{if(viewport!=null){viewport.Fit();picture.Invalidate();}}));bar.Children.Add(ReferencePage.Button("识别 / 翻译",()=>{if(image==null)return;try{new QingJie.TextWindow(QingJie.Snapshot.Load(siblings[index])).Show();}catch(Exception ex){MessageBox.Show(this,ex.Message);}}));bar.Children.Add(ReferencePage.Button("文件位置",()=>{if(siblings!=null)FileRouter.Reveal(siblings[index]);}));DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);DockPanel.SetDock(label,Dock.Bottom);root.Children.Add(label);
   PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}else if(e.Key==Key.Left){Step(-1);e.Handled=true;}else if(e.Key==Key.Right){Step(1);e.Handled=true;}};
   AllowDrop=true;DragOver+=(s,e)=>{e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};Drop+=(s,e)=>{var paths=e.Data.GetData(DataFormats.FileDrop)as string[];if(paths!=null&&paths.Length>0)Open(paths[0]);e.Handled=true;};Closed+=(s,e)=>{closed=true;Release();};Loaded+=(s,e)=>{if(path!=null)Open(path);else label.Text="拖入图片查看；不会自动收藏到素材库。";};
  }
  void Release(){if(drag!=null){drag.Dispose();drag=null;}if(host!=null){root.Children.Remove(host);host.Dispose();host=null;}if(image!=null){image.Dispose();image=null;}viewport=null;picture=null;}
  void Open(string path){if(loading)return;try{path=Path.GetFullPath(path);siblings=Directory.GetFiles(Path.GetDirectoryName(path)).Where(p=>FileRouter.Pictures.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(p=>p,StringComparer.CurrentCultureIgnoreCase).ToArray();index=Array.FindIndex(siblings,p=>string.Equals(p,path,StringComparison.OrdinalIgnoreCase));if(index<0)throw new NotSupportedException("请选择支持的图片格式。");Load();}catch(Exception ex){label.Text=ex.Message;}}
  void Step(int step){if(loading||siblings==null||siblings.Length==0)return;index=(index+step+siblings.Length)%siblings.Length;Load();}
  async void Load(){loading=true;Release();label.Text="正在打开…";try{string path=siblings[index];var loaded=await Task.Run(()=>PreviewImage.Open(path));if(closed){loaded.Dispose();return;}image=loaded;viewport=new PreviewViewport(image.Image.Width,image.Image.Height);var canvas=new PreviewCanvas{Dock=Forms.DockStyle.Fill,BackColor=Theme.Current=="light"?System.Drawing.Color.FromArgb(250,250,249):System.Drawing.Color.FromArgb(29,31,34)};picture=new PreviewPicture(image.Image,viewport){BackColor=canvas.BackColor};canvas.Controls.Add(picture);host=new WindowsFormsHost{Child=canvas};root.Children.Add(host);Action draw=()=>{picture.Invalidate();if(drag!=null)drag.UpdateCursor();};drag=new PreviewDragController(canvas,picture,viewport,draw);Forms.MouseEventHandler wheel=(s,e)=>{if(e.Delta==0)return;var point=canvas.PointToClient(((Forms.Control)s).PointToScreen(e.Location));drag.Cancel();viewport.ZoomAt(Math.Pow(1.15,e.Delta/120.0),point.X,point.Y);draw();};canvas.MouseWheel+=wheel;picture.MouseWheel+=wheel;picture.MouseEnter+=(s,e)=>canvas.Focus();canvas.Resize+=(s,e)=>{viewport.Resize(canvas.ClientSize.Width,canvas.ClientSize.Height);draw();};canvas.KeyDown+=(s,e)=>{if(e.KeyCode==Forms.Keys.Left)Step(-1);if(e.KeyCode==Forms.Keys.Right)Step(1);if(e.KeyCode==Forms.Keys.Escape)Close();};PreviewWindow.AttachDoubleClickClose(picture,drag,Close);UpdateLayout();viewport.Resize(canvas.ClientSize.Width,canvas.ClientSize.Height);draw();Title=Path.GetFileName(path)+" · Mori Desk";label.Text=(index+1)+" / "+siblings.Length+"    "+image.Image.Width+" × "+image.Image.Height+"    滚轮缩放 · 拖动查看 · 双击关闭 · 未收藏";}catch(Exception ex){label.Text="无法预览："+ex.Message;}finally{loading=false;}}
 }
}
