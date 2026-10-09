using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

namespace QingJie {
    public static class Native {
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        public static readonly IntPtr Topmost = new IntPtr(-1);
    }
    public sealed class Preferences {
        public double Stroke = 3;
        public string Color = "#EF4444";
        // Canonical '+'-separated optional languages; legacy single values still work.
        public string OcrLanguage = "";
        public string TranslationProvider = "tencent";
        public ShortcutSet Shortcuts = new ShortcutSet();
        public string SaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        internal static string TestFilePath;
        static string FilePath { get { return TestFilePath??Path.Combine(MoriDesk.Entry.DataRoot, "capture", "settings.json"); } }
        public static Preferences Load() {
            try {
                if (File.Exists(FilePath)) {
                    var p = new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(FilePath));
                    p.Stroke = Geometry.Clamp(p.Stroke, 1, 25);
                    ColorConverter.ConvertFromString(p.Color);
                    if(p.Shortcuts==null||p.Shortcuts.Error()!=null)p.Shortcuts=new ShortcutSet();
                    if(p.TranslationProvider!="google"&&p.TranslationProvider!="offline")p.TranslationProvider="tencent";
                    if (!Directory.Exists(p.SaveFolder)) p.SaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    return p;
                }
            } catch { }
            return new Preferences();
        }
        public void Save() {try{SaveChecked();}catch{}}
        public void SaveChecked(){Directory.CreateDirectory(Path.GetDirectoryName(FilePath));string temp=FilePath+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this));if(File.Exists(FilePath))File.Replace(temp,FilePath,null);else File.Move(temp,FilePath);}finally{if(File.Exists(temp))File.Delete(temp);}}
    }
    public static class Geometry {
        public static bool IsHangul(char c){return (c>='\uac00'&&c<='\ud7a3')||(c>='\u1100'&&c<='\u11ff')||(c>='\u3130'&&c<='\u318f');}
        public static bool NeedsWordSpace(char previous,char next){return (previous<0x2e80&&next<0x2e80)||IsHangul(previous)||IsHangul(next);}
        public static double Clamp(double n, double min, double max) { if (double.IsNaN(n) || double.IsInfinity(n)) return min; return Math.Max(min, Math.Min(max, n)); }
        public static Rect FromPoints(Point a, Point b) { return new Rect(new Point(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y)), new Point(Math.Max(a.X,b.X), Math.Max(a.Y,b.Y))); }
        public static Int32Rect PixelCrop(Rect r, double scaleX, double scaleY, int width, int height) {
            int x=(int)Clamp(Math.Floor(r.X*scaleX),0,width-1), y=(int)Clamp(Math.Floor(r.Y*scaleY),0,height-1);
            int right=(int)Clamp(Math.Ceiling(r.Right*scaleX),x+1,width), bottom=(int)Clamp(Math.Ceiling(r.Bottom*scaleY),y+1,height);
            return new Int32Rect(x,y,right-x,bottom-y);
        }
        public static string JoinWords(IEnumerable<WordBox> words) {
            var result = new System.Text.StringBuilder(); int line=-1;
            foreach (var w in words) {
                if (result.Length>0) {
                    if (line != w.Line) result.AppendLine();
                    else if (w.Text.Length>0 && NeedsWordSpace(result[result.Length-1],w.Text[0])) result.Append(' ');
                }
                result.Append(w.Text); line=w.Line;
            }
            return result.ToString();
        }
    }
    public sealed class Snapshot {
        public System.Drawing.Rectangle Bounds;
        public BitmapSource Image;
        public static Snapshot Screen(Forms.Screen screen) {
            var bounds=screen.Bounds;
            using(var bitmap=new System.Drawing.Bitmap(bounds.Width,bounds.Height,System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Location,System.Drawing.Point.Empty,bounds.Size,System.Drawing.CopyPixelOperation.SourceCopy);
                var handle=bitmap.GetHbitmap();
                try { var image=Imaging.CreateBitmapSourceFromHBitmap(handle,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); return new Snapshot { Bounds=bounds, Image=image }; }
                finally { Native.DeleteObject(handle); }
            }
        }
        public static BitmapSource Load(string path) {
            var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.UriSource=new Uri(Path.GetFullPath(path)); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
    }
    public static class ImageFiles {
        public static void Write(BitmapSource image,string path) { using(var stream=File.Create(path)) { var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(image));e.Save(stream); } }
        public static bool Save(BitmapSource image, Window owner) {
            var dialog=new Microsoft.Win32.SaveFileDialog { Filter="PNG 图片|*.png", FileName="Luma_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".png", InitialDirectory=AppState.Settings.SaveFolder };
            if(dialog.ShowDialog(owner)!=true) return false;
            Write(image,dialog.FileName); AppState.Settings.SaveFolder=Path.GetDirectoryName(dialog.FileName); AppState.Settings.Save(); return true;
        }
        public static void Copy(BitmapSource image) { Clipboard.SetImage(image); }
    }
}
