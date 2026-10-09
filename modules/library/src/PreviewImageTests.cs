using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class PreviewImageTests {
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   byte[] bgra={30,70,180,255, 120,40,20,128};
   var source=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,bgra,8);
   using(var result=PreviewImage.FromBitmapSource(source)){
    var first=result.GetPixel(0,0);var second=result.GetPixel(1,0);
    check(result.Width==2&&result.Height==1,"WIC fallback preserves dimensions");
    check(first.R==180&&first.G==70&&first.B==30&&first.A==255,"WIC fallback preserves RGB channel order");
    check(second.R==20&&second.G==40&&second.B==120&&second.A==128,"WIC fallback preserves alpha without premultiplication halos");
   }
   var gray=BitmapSource.Create(2,1,96,96,PixelFormats.Gray8,null,new byte[]{45,190},2);
   using(var result=PreviewImage.FromBitmapSource(gray))check(result.GetPixel(0,0).R==45&&result.GetPixel(1,0).B==190,"WIC fallback converts non-BGRA formats");
   string png=Path.Combine(output,"sample.png");
   using(var bitmap=new Bitmap(2,2)){bitmap.SetPixel(0,0,System.Drawing.Color.Red);bitmap.Save(png,ImageFormat.Png);}
   byte[] before=File.ReadAllBytes(png);
   using(var loaded=PreviewImage.Open(png))check(!loaded.UsedSystemDecoder&&loaded.Image.Width==2,"PNG retains existing lightweight GDI path");
   using(var unlocked=new FileStream(png,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(unlocked.Length==before.Length,"preview disposal releases original file handle");
   string invalid=Path.Combine(output,"bad.webp");File.WriteAllText(invalid,"not a valid image");bool rejected=false;try{using(var loaded=PreviewImage.Open(invalid)) {}}catch{rejected=true;}
   check(rejected,"invalid image rejected safely");
   using(var unlocked=new FileStream(invalid,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(unlocked.Length>0,"failed decoder releases original file handle");
  }
 }
}
