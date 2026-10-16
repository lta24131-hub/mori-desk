using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;
using Forms=System.Windows.Forms;

namespace MoriDesk {
 // Every write is intercepted. These fixtures use generated pixels and inert
 // Forms controls only: no native handle, shown window or system clipboard.
 internal static class PreviewClipboardTests {
  static byte[] Pixel(BitmapSource image,int x,int y){var result=new byte[4];var pixels=image.Format==PixelFormats.Bgra32?image:new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);pixels.CopyPixels(new Int32Rect(x,y,1,1),result,4,0);return result;}
  static byte[] Pixels(BitmapSource image){var pixels=image.Format==PixelFormats.Bgra32?image:new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var result=new byte[checked(image.PixelWidth*image.PixelHeight*4)];pixels.CopyPixels(result,image.PixelWidth*4,0);return result;}
  static BitmapSource Decode(byte[] png){using(var stream=new MemoryStream(png,false)){var image=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);image.Freeze();return image;}}
  static byte[] Png(DataObject data,Action<bool,string> check,string name,out Stream original){
   original=data.GetData("PNG",false) as Stream;check(original!=null&&original.CanRead&&original.Position==0,name+": PNG stream is live and rewound when the writer flushes");
   using(var copy=new MemoryStream()){original.CopyTo(copy);return copy.ToArray();}
  }
  internal static void Run(string root,Action<bool,string> check){
   // root is intentionally unused: even generated clipboard fixtures need no
   // temporary files, user paths, application startup or persistent settings.
   StaticImage(check);CurrentGifFrame(check);Failures(check);SizeLimit(check);MenuAndKeyboard(check,false);MenuAndKeyboard(check,true);
  }
  static void StaticImage(Action<bool,string> check){
   BitmapSource snapshot=null;byte[] encoded=null;Stream writerStream=null;int writes=0;
   using(var image=new Drawing.Bitmap(320,180,Imaging.PixelFormat.Format32bppArgb)){
    image.SetPixel(0,0,Drawing.Color.FromArgb(0,0,0,0));image.SetPixel(90,50,Drawing.Color.FromArgb(128,30,80,170));image.SetPixel(319,179,Drawing.Color.FromArgb(255,90,120,210));
    string failure=PreviewClipboard.TryCopy(image,(data,persist)=>{
     writes++;snapshot=data.GetImage();check(persist,"copy requests persistent ownership while its PNG stream is still alive");
     check(snapshot!=null&&snapshot.IsFrozen&&snapshot.PixelWidth==320&&snapshot.PixelHeight==180,"Bitmap clipboard representation is frozen at full decoded dimensions");
     check(data.GetDataPresent(DataFormats.Bitmap,false)&&data.GetDataPresent("PNG",false),"copy provides both native Bitmap and explicit PNG representations");
     check(!data.GetDataPresent(DataFormats.FileDrop,false)&&!data.GetDataPresent(DataFormats.Text,false)&&!data.GetDataPresent(DataFormats.UnicodeText,false),"copy contains pixels, not a file shortcut, local path or text");
     encoded=Png(data,check,"static image",out writerStream);var png=Decode(encoded);
     check(png.PixelWidth==320&&png.PixelHeight==180&&Pixels(snapshot).SequenceEqual(Pixels(png)),"PNG and Bitmap representations contain the same full decoded pixels");
     check(Pixel(png,0,0)[3]==0&&Pixel(png,90,50).SequenceEqual(new byte[]{170,80,30,128})&&Pixel(png,319,179).SequenceEqual(new byte[]{210,120,90,255}),"copy preserves transparent, half-transparent and far-edge opaque RGB pixels without a backdrop");
    });
    check(failure==""&&writes==1,"successful copy reports success only after exactly one intercepted write");
    check(writerStream!=null&&!writerStream.CanRead,"copy releases the temporary PNG stream after the synchronous writer returns");
    image.SetPixel(90,50,Drawing.Color.Lime);check(Pixel(snapshot,90,50)[3]==128,"later changes to decoded source pixels cannot mutate copied pixels");
   }
   check(snapshot.IsFrozen&&Pixel(snapshot,90,50).SequenceEqual(new byte[]{170,80,30,128}),"frozen Bitmap pixels survive source-image disposal");
   check(Pixel(Decode(encoded),319,179).SequenceEqual(new byte[]{210,120,90,255}),"flushed PNG bytes remain decodable after source and temporary stream disposal");
  }
  static void GifCode(byte[] bytes,ref int bit,int code){for(int i=0;i<3;i++,bit++)if((code&(1<<i))!=0)bytes[bit/8]|=(byte)(1<<(bit%8));}
  static byte[] Gif(){
   using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)){
    writer.Write(new byte[]{71,73,70,56,57,97,8,0,8,0,128,0,0,255,0,0,0,0,255,33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0});
    for(int frame=0;frame<2;frame++){writer.Write(new byte[]{33,249,4,4,10,0,0,0,44,0,0,0,0,8,0,8,0,0,2});var pixels=new byte[49];int bit=0;for(int i=0;i<64;i++){GifCode(pixels,ref bit,4);GifCode(pixels,ref bit,frame);}GifCode(pixels,ref bit,5);writer.Write((byte)pixels.Length);writer.Write(pixels);writer.Write((byte)0);}
    writer.Write((byte)59);return stream.ToArray();
   }
  }
  static void CurrentGifFrame(Action<bool,string> check){
   BitmapSource frozen=null;byte[] encoded=null;int writes=0;
   using(var stream=new MemoryStream(Gif()))using(var image=Drawing.Image.FromStream(stream)){
    check(image.GetFrameCount(Imaging.FrameDimension.Time)==2,"copy-current-frame fixture is a real two-frame animated GIF");image.SelectActiveFrame(Imaging.FrameDimension.Time,1);
    PreviewClipboard.Copy(image,(data,persist)=>{writes++;frozen=data.GetImage();Stream live;encoded=Png(data,check,"current GIF frame",out live);check(persist&&frozen.PixelWidth==8&&frozen.PixelHeight==8&&Pixel(frozen,4,4).SequenceEqual(new byte[]{255,0,0,255}),"GIF copy snapshots the selected blue frame rather than resetting to the first red frame");});
    image.SelectActiveFrame(Imaging.FrameDimension.Time,0);check(Pixel(frozen,4,4).SequenceEqual(new byte[]{255,0,0,255}),"subsequent GIF playback cannot modify the copied static frame");
   }
   using(var stream=new MemoryStream(encoded,false)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);check(writes==1&&decoder.Frames.Count==1&&Pixel(decoder.Frames[0],4,4).SequenceEqual(new byte[]{255,0,0,255}),"GIF clipboard PNG is one detached static current frame after original GIF disposal");}
  }
  static void Failures(Action<bool,string> check){
   int writes=0;Stream failedStream=null;var missing=PreviewClipboard.TryCopy(null,(data,persist)=>writes++);check(writes==0&&missing.Contains("加载完成")&&missing.StartsWith("未能复制图片"),"missing image fails before any clipboard write and explains loading state");
   using(var image=new Drawing.Bitmap(3,2,Imaging.PixelFormat.Format32bppArgb)){
    string busy=PreviewClipboard.TryCopy(image,(data,persist)=>{writes++;failedStream=data.GetData("PNG",false)as Stream;throw new ExternalException("fixture occupied",unchecked((int)0x800401d0));});
    check(writes==1&&busy=="剪贴板暂时被其他程序占用，请稍后再次复制。","CLIPBRD_E_CANT_OPEN produces the exact actionable busy message, not a success or silent retry");check(failedStream!=null&&!failedStream.CanRead,"busy clipboard failure still disposes its temporary PNG stream");
    string failed=PreviewClipboard.TryCopy(image,(data,persist)=>{writes++;throw new IOException("fixture writer failed");});check(writes==2&&failed=="未能复制图片：fixture writer failed","unexpected writer failures are reported without claiming a saved clipboard");
    bool propagated=false;try{PreviewClipboard.Copy(image,(data,persist)=>{writes++;throw new IOException("fixture propagate");});}catch(IOException error){propagated=error.Message=="fixture propagate";}
    check(propagated&&writes==3,"Copy itself preserves the writer exception for callers that handle failure explicitly");
    string nativeFailure=PreviewClipboard.TryCopy(image,(data,persist)=>{writes++;failedStream=data.GetData("PNG",false)as Stream;throw new ExternalException("fixture image conversion failed",unchecked((int)0x80004005));});
    check(writes==4&&nativeFailure=="未能复制图片：fixture image conversion failed","non-busy ExternalException preserves its real failure instead of claiming that the clipboard is occupied");
    check(failedStream!=null&&!failedStream.CanRead,"non-busy native failure still disposes its temporary PNG stream");
    string invalidData=PreviewClipboard.TryCopy(image,(data,persist)=>{writes++;throw new COMException("fixture invalid clipboard data",unchecked((int)0x800401d3));});
    check(writes==5&&invalidData=="未能复制图片：fixture invalid clipboard data","another clipboard COM error is not mistaken for CLIPBRD_E_CANT_OPEN");
    string busyCom=PreviewClipboard.TryCopy(image,(data,persist)=>{writes++;throw new COMException("fixture COM occupied",unchecked((int)0x800401d0));});
    check(writes==6&&busyCom=="剪贴板暂时被其他程序占用，请稍后再次复制。","CLIPBRD_E_CANT_OPEN keeps its busy guidance when raised as a COMException subclass");
    string retry=PreviewClipboard.TryCopy(image,(data,persist)=>writes++);check(retry==""&&writes==7,"native and writer failures do not retain a busy guard or prevent a later successful retry");
   }
  }
  static void SizeLimit(Action<bool,string> check){
   // The indexed fixture occupies about 4 MiB, not a full 128 MiB ARGB image.
   // Its decoded dimensions are just beyond the production 32-Mpixel cap;
   // rejection must happen before snapshotting, encoding or invoking a writer.
   const string message="图片超过复制大小限制（3200 万像素），请先降低图片文件的像素尺寸后重试；缩放预览不会改变图片大小。";
   int writes=0;
   using(var image=new Drawing.Bitmap(8192,4097,Imaging.PixelFormat.Format1bppIndexed)){
    string failure=PreviewClipboard.TryCopy(image,(data,persist)=>writes++);
    check(writes==0&&failure=="未能复制图片："+message,"oversized decoded image is rejected before any clipboard write and explicitly distinguishes file pixels from preview zoom");
    bool exactFailure=false;try{PreviewClipboard.Copy(image,(data,persist)=>writes++);}catch(NotSupportedException error){exactFailure=error.Message==message;}
    check(writes==0&&exactFailure,"direct Copy exposes the same actionable size-limit error without touching the clipboard sink");
   }
  }
  static void MenuAndKeyboard(Action<bool,string> check,bool pinFirst){
   string label=pinFirst?"pin then copy":"copy then pin";int copies=0,pins=0;var image=new Drawing.Bitmap(320,180,Imaging.PixelFormat.Format32bppArgb);PreviewPicture picture=null;PreviewCanvas canvas=null;
   try{
    image.SetPixel(310,170,Drawing.Color.FromArgb(128,25,75,125));var view=new PreviewViewport(320,180);view.Resize(80,45);view.ZoomAt(5,20,20);view.PanTo(-250,-125);
    picture=new PreviewPicture(image,view,new Settings{AnimateImages=false,PreviewBackdrop="dark-checker"});canvas=new PreviewCanvas();canvas.Controls.Add(picture);
    Action copy=()=>PreviewClipboard.Copy(image,(data,persist)=>{copies++;var bitmap=data.GetImage();check(persist&&bitmap.PixelWidth==320&&bitmap.PixelHeight==180&&Pixel(bitmap,310,170).SequenceEqual(new byte[]{125,75,25,128}),"menu/shortcut copy ignores zoom, pan, small viewport and dark checker / "+label);});
    Func<Task> pin=()=>{pins++;return Task.FromResult(true);};if(pinFirst){picture.AttachDesktopPin(pin);picture.AttachCopy(copy);}else{picture.AttachCopy(copy);picture.AttachDesktopPin(pin);}picture.AttachCopy(()=>copies+=100);picture.AttachDesktopPin(()=>{pins+=100;return Task.FromResult(true);});canvas.CopyImage=copy;
    var menu=picture.ContextMenuStrip;var entries=menu.Items.Cast<Forms.ToolStripItem>().ToArray();
    check(!picture.IsHandleCreated&&!canvas.IsHandleCreated&&!menu.IsHandleCreated,"constructing image actions does not create any native control handles / "+label);
    check(entries.Length==8&&entries[0].Name=="CopyImage"&&entries[1].Name=="PinToDesktop"&&entries[2] is Forms.ToolStripSeparator&&entries.Count(x=>x.Name=="ImageActionsSeparator")==1,"copy and pin keep stable order with one shared separator and no duplicate attachment / "+label);
    var copyItem=(Forms.ToolStripMenuItem)menu.Items["CopyImage"];check(copyItem.Text=="复制图片"&&copyItem.ShortcutKeyDisplayString=="Ctrl+C"&&copyItem.AccessibleName=="复制图片","copy action has a discoverable label, shortcut hint and accessible name / "+label);copyItem.PerformClick();check(copies==1,"actual copy menu item invokes its first attached action once / "+label);
    ((Forms.ToolStripMenuItem)menu.Items["PinToDesktop"]).PerformClick();check(pins==1&&copies==1,"copy does not intercept the independent pin action / "+label);
    var backgrounds=menu.Items.OfType<Forms.ToolStripMenuItem>().Where(item=>item.Tag is PreviewBackground).ToArray();check(backgrounds.Length==5&&backgrounds.Count(item=>item.Checked)==1,"all five existing background options remain available and mutually exclusive / "+label);
    foreach(var item in backgrounds){item.PerformClick();check(picture.BackgroundMode==(PreviewBackground)item.Tag&&backgrounds.Count(option=>option.Checked)==1&&item.Checked,"background selection remains exclusive with added image actions / "+label+" / "+item.Tag);}
    check(copies==1&&pins==1,"background changes never invoke copy or desktop pin / "+label);
    check(canvas.TryCopyShortcut(Forms.Keys.Control|Forms.Keys.C)&&copies==2,"focused preview canvas consumes exact Ctrl+C through its shared copy action / "+label);
    foreach(var key in new[]{Forms.Keys.C,Forms.Keys.Control,Forms.Keys.Control|Forms.Keys.Insert,Forms.Keys.Control|Forms.Keys.Shift|Forms.Keys.C,Forms.Keys.Control|Forms.Keys.Alt|Forms.Keys.C,Forms.Keys.Alt|Forms.Keys.C,Forms.Keys.Escape,Forms.Keys.Left})check(!canvas.TryCopyShortcut(key)&&copies==2,"unrelated key is not consumed and cannot copy / "+label+" / "+key);
    bool nullCopy=false,nullPin=false;try{picture.AttachCopy(null);}catch(ArgumentNullException){nullCopy=true;}try{picture.AttachDesktopPin(null);}catch(ArgumentNullException){nullPin=true;}check(nullCopy&&nullPin,"null image action attachment is rejected explicitly / "+label);
    check(!picture.IsHandleCreated&&!canvas.IsHandleCreated&&!menu.IsHandleCreated,"menu clicks and shortcut tests remain entirely handle-free / "+label);
    canvas.Dispose();check(canvas.IsDisposed&&canvas.CopyImage==null&&picture.IsDisposed&&menu.IsDisposed,"disposing preview canvas clears its copy delegate and releases child image menu / "+label);check(!canvas.TryCopyShortcut(Forms.Keys.Control|Forms.Keys.C)&&copies==2,"disposed canvas cannot issue a late clipboard copy / "+label);
   }finally{if(canvas!=null)canvas.Dispose();if(picture!=null)picture.Dispose();image.Dispose();}
   using(var unbound=new PreviewCanvas())check(!unbound.TryCopyShortcut(Forms.Keys.Control|Forms.Keys.C)&&!unbound.IsHandleCreated,"unbound preview canvas leaves Ctrl+C untouched without creating a native handle / "+label);
  }
 }
}
