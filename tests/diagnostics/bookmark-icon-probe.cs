// Standalone synthetic diagnostic; not part of the production build. No personal data.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Media.Imaging;

namespace MoriDesk.Diagnostics {
 public static class BookmarkIconProbe {
  static byte[] Png(){using(var image=new Bitmap(128,96)){using(var g=Graphics.FromImage(image))g.Clear(Color.Teal);using(var bytes=new MemoryStream()){image.Save(bytes,ImageFormat.Png);return bytes.ToArray();}}}
  static object Call(Type type,string name,params object[] args){return type.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).Invoke(null,args);}
  [STAThread] public static int Main(string[] args){
   if(args.Length!=2){Console.Error.WriteLine("Usage: bookmark-icon-probe.exe <candidate Mori.exe> <isolated output>");return 2;}
   var log=new List<string>();
   try{
    string binary=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
    var assembly=Assembly.LoadFrom(binary);var icons=assembly.GetType("Mori.BookmarkIcons",true);icons.GetField("NetworkEnabled",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,false);
    byte[] png=Png();string site="https://example.com/path?private=value",stem=(string)Call(icons,"PathFor",root,site);
    log.Add("Root: "+root);log.Add("Root chars="+root.Length+", stem chars="+stem.Length+", hint chars="+(stem+".hint").Length+", temporary hint chars="+(stem+".hint."+Guid.NewGuid().ToString("N")+".tmp").Length);
    var sizes=new Dictionary<string,int>();for(int i=0;i<100;i++){var image=(BitmapSource)Call(icons,"Decode",png);string size=image.PixelWidth+"x"+image.PixelHeight+" frozen="+image.IsFrozen;int count;sizes.TryGetValue(size,out count);sizes[size]=count+1;}
    foreach(var pair in sizes)log.Add("Direct Decode (100 iterations): "+pair.Key+" count="+pair.Value);
    Call(icons,"Remember",root,site,"https://cdn.example.com/icons/site.png");log.Add("Hint exists after Remember="+File.Exists(stem+".hint"));
    Uri requested=null;Func<Uri,byte[]> fetch=delegate(Uri uri){requested=uri;return png;};var first=(BitmapSource)Call(icons,"Load",root,site,fetch);
    log.Add("Load requested="+(requested==null?"<none>":requested.AbsoluteUri)+", result="+(first==null?"<null>":first.PixelWidth+"x"+first.PixelHeight+" frozen="+first.IsFrozen)+", cached PNG="+File.Exists(stem+".png"));
    try{Call(icons,"Write",stem+".diagnostic",Encoding.UTF8.GetBytes("synthetic-only diagnostic"));log.Add("Direct Write succeeded");}catch(TargetInvocationException ex){log.Add("Direct Write error: "+ex.InnerException.GetType().FullName+": "+ex.InnerException.Message);}
    int checks=0;Action<bool,string> check=delegate(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks++;};
    string regressions=Path.Combine(root,"regression");
    try{assembly.GetType("Mori.BookmarkIconTests",true).GetMethod("Run").Invoke(null,new object[]{regressions,check});log.Add("BookmarkIconTests: PASS "+checks+" checks");}
    catch(TargetInvocationException ex){log.Add("BookmarkIconTests: "+ex.InnerException+" after "+checks+" checks");}
    File.WriteAllLines(Path.Combine(root,"bookmark-icon-probe.txt"),log,Encoding.UTF8);foreach(string line in log)Console.WriteLine(line);return 0;
   }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  }
 }
}
