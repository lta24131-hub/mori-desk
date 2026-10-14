using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using Forms=System.Windows.Forms;

namespace Mori {
 public static class PreviewBackgroundTests {
  static bool Near(Color actual,Color expected){return Math.Abs(actual.R-expected.R)<4&&Math.Abs(actual.G-expected.G)<4&&Math.Abs(actual.B-expected.B)<4;}
  static bool CheckerColor(Color actual,bool dark){return Near(actual,dark?PreviewBackgrounds.DarkA:PreviewBackgrounds.LightA)||Near(actual,dark?PreviewBackgrounds.DarkB:PreviewBackgrounds.LightB);}
  public static void Run(Action<bool,string> check){
   using(var source=new Bitmap(200,100,PixelFormat.Format32bppArgb)){
    using(var g=Graphics.FromImage(source)){g.Clear(Color.Transparent);g.FillRectangle(Brushes.Black,20,25,40,50);g.FillRectangle(Brushes.White,90,25,40,50);using(var semi=new SolidBrush(Color.FromArgb(128,30,80,170)))g.FillRectangle(semi,150,25,40,50);}
    check(PreviewBackgrounds.MayHaveAlpha(source),"alpha-capable image uses metadata, not a full pixel scan");
    var viewport=new PreviewViewport(200,100);viewport.Resize(300,200);
    using(var picture=new PreviewPicture(source,viewport){Size=new Size(300,200),BackColor=Color.FromArgb(29,31,34)})using(var output=new Bitmap(300,200)){
     int blackX=(int)(viewport.X+40*viewport.Zoom),whiteX=(int)(viewport.X+110*viewport.Zoom),semiX=(int)(viewport.X+170*viewport.Zoom),middleY=(int)(viewport.Y+50*viewport.Zoom);
     picture.DrawToBitmap(output,new Rectangle(0,0,300,200));
     check(picture.BackgroundMode==PreviewBackground.Automatic,"new preview defaults to automatic transparency backdrop");
     check(CheckerColor(output.GetPixel(80,40),false)&&CheckerColor(output.GetPixel(92,40),false)&&!Near(output.GetPixel(80,40),output.GetPixel(92,40)),"transparent pixels reveal a low-contrast light checker on a dark theme");
     check(Near(output.GetPixel(1,1),picture.BackColor),"checker is clipped to image bounds, not the whole window");
     check(Near(output.GetPixel(blackX,middleY),Color.Black)&&Near(output.GetPixel(whiteX,middleY),Color.White),"opaque black and white artwork remains unaltered");
     check(output.GetPixel(semiX,middleY).A==255&&output.GetPixel(semiX,middleY).B>170,"semi-transparent pixels composite on backdrop without becoming opaque source pixels");
     int changes=0;picture.PreviewBackgroundChanged+=delegate{changes++;};
     picture.BackgroundMode=PreviewBackground.DarkChecker;picture.DrawToBitmap(output,new Rectangle(0,0,300,200));
     check(CheckerColor(output.GetPixel(80,40),true)&&Near(output.GetPixel(whiteX,middleY),Color.White),"dark checker makes white artwork visible without inverting the image");
     picture.BackgroundMode=PreviewBackground.White;picture.DrawToBitmap(output,new Rectangle(0,0,300,200));check(Near(output.GetPixel(80,40),Color.White),"pure white backdrop is available");
     picture.BackgroundMode=PreviewBackground.Dark;picture.DrawToBitmap(output,new Rectangle(0,0,300,200));check(Near(output.GetPixel(80,40),PreviewBackgrounds.SolidDark),"solid deep grey backdrop is available");
     picture.BackgroundMode=PreviewBackground.Dark;check(changes==3,"same background choice does not notify or rebuild the image");
     var menu=picture.ContextMenuStrip;check(menu.Items.Count==5&&menu.AccessibleName=="图片背景","preview right click exposes named compact background options");
     ((Forms.ToolStripMenuItem)menu.Items[1]).PerformClick();check(picture.BackgroundMode==PreviewBackground.LightChecker&&changes==4,"right-click background choice changes the renderer");
     int checkedCount=0;foreach(Forms.ToolStripMenuItem item in menu.Items)if(item.Checked)checkedCount++;check(checkedCount==1&&((Forms.ToolStripMenuItem)menu.Items[1]).Checked,"background options remain a mutually exclusive choice");
     bool rejected=false;try{picture.BackgroundMode=(PreviewBackground)999;}catch(ArgumentOutOfRangeException){rejected=true;}check(rejected&&picture.BackgroundMode==PreviewBackground.LightChecker,"invalid backdrop value is rejected without changing the selection");
     double zoom=viewport.Zoom;picture.BackgroundMode=PreviewBackground.Automatic;picture.BackColor=Color.FromArgb(250,250,249);picture.DrawToBitmap(output,new Rectangle(0,0,300,200));
     check(CheckerColor(output.GetPixel(80,40),false)&&Near(output.GetPixel(1,1),picture.BackColor),"light theme retains subtle transparent checker and its own outer background");
     check(viewport.Zoom==zoom&&picture.Size==new Size(300,200),"background changes retain zoom and viewport-sized render surface");
     viewport.ZoomAt(4,150,100);viewport.PanTo(-100,-40);picture.DrawToBitmap(output,new Rectangle(0,0,300,200));
     check(CheckerColor(output.GetPixel(80,10),false)&&!Near(output.GetPixel(80,10),output.GetPixel(92,10)),"checker stays small and screen-aligned while image is enlarged and panned");
     check(source.GetPixel(0,0).A==0&&source.GetPixel(170,50).A==128&&source.GetPixel(40,50).R==0&&source.GetPixel(110,50).R==255,"all background choices preserve original transparency and pixel colors");
     var light=(TextureBrush)typeof(PreviewPicture).GetField("lightChecker",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(picture);using(var tile=light.Image)check(tile.Width==24&&tile.Height==24,"checker allocation is a 24-pixel tile, never a full-size image copy");
     picture.Dispose();check(menu.IsDisposed&&typeof(PreviewPicture).GetField("lightChecker",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(picture)==null&&typeof(PreviewPicture).GetField("darkChecker",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(picture)==null,"preview disposal releases both background textures and its menu");
    }
   }
   using(var source=new Bitmap(100,100,PixelFormat.Format24bppRgb)){
    using(var graphics=Graphics.FromImage(source))graphics.Clear(Color.FromArgb(25,90,150));check(!PreviewBackgrounds.MayHaveAlpha(source),"ordinary 24-bit opaque image does not request a checker");
    var viewport=new PreviewViewport(100,100);viewport.Resize(150,200);
    using(var picture=new PreviewPicture(source,viewport){Size=new Size(150,200),BackColor=Color.FromArgb(29,31,34)})using(var output=new Bitmap(150,200)){
     picture.DrawToBitmap(output,new Rectangle(0,0,150,200));check(Near(output.GetPixel(75,100),Color.FromArgb(25,90,150))&&Near(output.GetPixel(1,1),picture.BackColor),"opaque image and outer letterbox keep existing colors");
     check(typeof(PreviewPicture).GetField("lightChecker",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(picture)==null,"ordinary photos allocate no transparency tile");
    }
   }
  }
  // Independent UI fixtures. They are generated shapes, not user artwork.
  public static void WriteFixtures(string folder){Directory.CreateDirectory(folder);foreach(bool white in new[]{false,true})using(var bitmap=new Bitmap(960,640,PixelFormat.Format32bppArgb)){
   using(var graphics=Graphics.FromImage(bitmap))using(var ink=new SolidBrush(white?Color.White:Color.FromArgb(12,15,19)))using(var accent=new SolidBrush(Color.FromArgb(238,96,34)))using(var font=new Font("Segoe UI",60,FontStyle.Bold)){
    graphics.Clear(Color.Transparent);graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;graphics.FillEllipse(ink,310,145,150,150);graphics.FillEllipse(ink,490,145,150,150);graphics.FillRectangle(ink,355,235,240,140);graphics.FillRectangle(ink,375,375,60,70);graphics.FillRectangle(ink,520,375,60,70);graphics.FillEllipse(accent,280,300,50,50);graphics.DrawString("PNG",font,ink,365,470);
   }bitmap.Save(Path.Combine(folder,white?"02-white-transparent.png":"01-black-transparent.png"),ImageFormat.Png);
  }using(var bitmap=new Bitmap(960,640,PixelFormat.Format24bppRgb)){using(var graphics=Graphics.FromImage(bitmap)){graphics.Clear(Color.SteelBlue);graphics.FillRectangle(Brushes.Goldenrod,180,160,600,320);}bitmap.Save(Path.Combine(folder,"03-opaque.jpg"),ImageFormat.Jpeg);}}
 }
}
