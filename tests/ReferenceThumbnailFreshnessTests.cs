using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Mori;

namespace MoriDesk {
 internal static class ReferenceThumbnailFreshnessTests {
  static void Image(string path,int size,Color color,DateTime modified){using(var bitmap=new Bitmap(size,size)){using(var g=Graphics.FromImage(bitmap))g.Clear(color);bitmap.Save(path,ImageFormat.Png);}File.SetLastWriteTimeUtc(path,modified);}
  static Color Pixel(string path){using(var bitmap=new Bitmap(path))return bitmap.GetPixel(0,0);}
  internal static void Run(string testRoot,Action<bool,string> check){
   string root=Path.Combine(testRoot,"reference-thumbnail-freshness");Directory.CreateDirectory(root);
   string source=Path.Combine(root,"live.png"),library=Path.Combine(root,"library");var stamp=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
   Image(source,32,Color.Red,stamp);
   string id,thumb;
   using(var lib=new Library(library)){
    lib.EnableReferenceAssets();id=lib.AddReferenceAssets(new[]{source},"").Single();var item=lib.All().Single();lib.BuildThumbnail(item);thumb=lib.ThumbnailPath(item);
    check(File.Exists(thumb)&&item.Width==32&&Pixel(thumb).R==255,"reference first thumbnail built and indexed");
    check(!string.IsNullOrEmpty(lib.All().Single().ThumbnailSourceStamp),"reference source fingerprint persists with cache metadata");
    var cacheTime=stamp.AddDays(-1);File.SetLastWriteTimeUtc(thumb,cacheTime);
    using(var locked=File.Open(source,FileMode.Open,FileAccess.ReadWrite,FileShare.None))lib.BuildThumbnail(lib.All().Single());
    check(File.GetLastWriteTimeUtc(thumb)==cacheTime,"unchanged reference returns cached thumbnail without decoding original");
    Image(source,32,Color.Blue,stamp.AddSeconds(1));lib.BuildThumbnail(lib.All().Single());
    check(Pixel(thumb).B==255&&Pixel(thumb).R==0,"same-size reference edit bypasses WPF URI cache and refreshes pixels");
    Image(source,64,Color.Lime,stamp.AddSeconds(2));var sourceBytes=File.ReadAllBytes(source);var sourceTime=File.GetLastWriteTimeUtc(source);lib.BuildThumbnail(lib.All().Single());var changed=lib.All().Single();
    check(changed.Width==64&&changed.Height==64&&changed.Bytes==sourceBytes.Length,"changed reference refreshes dimensions and byte count");
    check(Pixel(thumb).G==255&&Pixel(thumb).B==0,"changed reference replaces old cached image");
    check(File.ReadAllBytes(source).SequenceEqual(sourceBytes)&&File.GetLastWriteTimeUtc(source)==sourceTime,"cache refresh never modifies referenced original");
   }
   using(var lib=new Library(library)){
    lib.EnableReferenceAssets();var persisted=lib.All().Single();var oldTime=File.GetLastWriteTimeUtc(thumb);lib.BuildThumbnail(persisted);
    check(persisted.Width==64&&File.GetLastWriteTimeUtc(thumb)==oldTime,"reference fingerprint survives library reopen without unnecessary rebuild");
    byte[] good=File.ReadAllBytes(thumb);string validStamp=persisted.ThumbnailSourceStamp;
    File.WriteAllBytes(source,new byte[]{1,2,3,4,5,6,7,8,9,10,11,12});File.SetLastWriteTimeUtc(source,stamp.AddSeconds(3));lib.BuildThumbnail(lib.All().Single());
    check(File.ReadAllBytes(thumb).SequenceEqual(good)&&lib.All().Single().ThumbnailSourceStamp==validStamp,"invalid source preserves last valid thumbnail and fingerprint");
    File.Delete(source);lib.BuildThumbnail(lib.All().Single());
    check(File.ReadAllBytes(thumb).SequenceEqual(good),"temporarily missing original preserves last available preview");
    Image(source,64,Color.Lime,stamp.AddSeconds(2));
    string relocated=Path.Combine(root,"relinked.png");Image(relocated,64,Color.Magenta,stamp.AddSeconds(2));
    var refs=new ReferenceStore(lib.Db);refs.Relink(id.Substring(4),relocated);lib.BuildThumbnail(lib.All().Single());
    check(Pixel(thumb).R==255&&Pixel(thumb).B==255&&Pixel(thumb).G==0,"relink invalidates cache even when dimensions and modification time match");
    lib.Db.Exec("UPDATE desk_refs SET thumb_stamp='' WHERE id="+Database.Q(id.Substring(4)));Image(relocated,64,Color.Cyan,stamp.AddSeconds(4));lib.BuildThumbnail(lib.All().Single());
    check(Pixel(thumb).R==0&&Pixel(thumb).B==255&&lib.All().Single().ThumbnailSourceStamp!="","legacy caches with no fingerprint refresh lazily on first request");
    check(Directory.GetFiles(Path.Combine(library,"原图"),"*",SearchOption.AllDirectories).Length==0,"reference previews never copy originals into the library");
   }
  }
 }
}
