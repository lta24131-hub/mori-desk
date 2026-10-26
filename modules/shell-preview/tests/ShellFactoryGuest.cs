using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

// Compile on the build host; execute ONLY in the dedicated QEMU test guest.
// Uses the same IShellItemImageFactory path as Explorer, including the isolated
// thumbnail surrogate. Every temporary registry value is journaled/restored.
internal static class ShellFactoryGuest {
 const string Audit=@"C:\NookLab\Audit";
 const string Thumb="{FA45A458-130E-47BA-AAE0-1202D6930A79}";
 const string Icon="{612E3A5A-3C2C-4709-8C54-9C3EA927C086}";
 const string Category="{e357fccd-a995-4576-b01f-234630154e96}";
 const string App=@"Software\Classes\Applications\Mori.exe";
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
 static readonly List<object> Checks=new List<object>();
 static int failures;
 static string folder;
 [StructLayout(LayoutKind.Sequential)] struct Size {public int width,height;public Size(int w,int h){width=w;height=h;}}
 [ComImport,Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 interface IShellItemImageFactory {[PreserveSig]int GetImage(Size size,uint flags,out IntPtr bitmap);}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)]static extern int SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItemImageFactory result);
 [DllImport("shell32.dll")]static extern void SHChangeNotify(uint eventId,uint flags,IntPtr a,IntPtr b);
 [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]static extern int AssocQueryString(uint flags,uint str,string association,string extra,StringBuilder value,ref uint length);
 [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr bitmap);
 sealed class Change {public string path,name;public bool exists;public object value;public RegistryValueKind kind;public string written;}
 sealed class Registration:IDisposable {
  readonly List<Change> changes=new List<Change>();
  readonly HashSet<string> created=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  public void Put(string path,string name,string value){
   var change=new Change{path=path,name=name,written=value};
   using(var prior=Registry.CurrentUser.OpenSubKey(path))if(prior!=null&&prior.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase)){change.exists=true;change.kind=prior.GetValueKind(name);change.value=prior.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);}
   changes.Add(change);
   for(string part=path;part.StartsWith("Software\\Classes\\",StringComparison.OrdinalIgnoreCase);part=part.Substring(0,part.LastIndexOf('\\'))){using(var prior=Registry.CurrentUser.OpenSubKey(part))if(prior==null)created.Add(part);}
   File.WriteAllText(Path.Combine(folder,"registry-before.json"),Json.Serialize(changes));
   using(var key=Registry.CurrentUser.CreateSubKey(path))key.SetValue(name,value,RegistryValueKind.String);
  }
  public void Dispose(){
   bool restored=true;
   for(int i=changes.Count-1;i>=0;i--){var c=changes[i];try{using(var key=Registry.CurrentUser.OpenSubKey(c.path,true)){if(key==null){restored&=!c.exists;continue;}if(!object.Equals(key.GetValue(c.name),c.written)){restored=false;continue;}if(c.exists)key.SetValue(c.name,c.value,c.kind);else key.DeleteValue(c.name,false);}}catch(Exception e){restored=false;Console.WriteLine("restore: "+e.Message);}}
   foreach(string path in created.OrderByDescending(p=>p.Length))try{using(var key=Registry.CurrentUser.OpenSubKey(path))if(key==null||key.SubKeyCount!=0||key.ValueCount!=0)continue;Registry.CurrentUser.DeleteSubKey(path,false);}catch{restored=false;}
   SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);
   Check(restored,"temporary_registrations_restored",null);
   File.WriteAllText(Path.Combine(folder,"registry-restored.json"),Json.Serialize(new{restored,changedValues=changes.Count}));
  }
 }
 sealed class Picture {public int hr,width,height;public string hash,path;public bool nonUniform;public int minAlpha=255,maxAlpha=0;}
 static void Check(bool passed,string name,object detail){Checks.Add(new{name,passed,detail});Console.WriteLine(Json.Serialize(new{name,passed,detail}));if(!passed)failures++;}
 static void Guest(){
  bool valid=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject item in query.Get())using(item){string hardware=Convert.ToString(item["Manufacturer"])+" "+Convert.ToString(item["Model"]);valid|=System.Text.RegularExpressions.Regex.IsMatch(hardware,"QEMU|KVM|Bochs",System.Text.RegularExpressions.RegexOptions.IgnoreCase);}
  if(!valid||!Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Dedicated NookTester VM required before any registry write.");
 }
 static string Scoped(string path){string full=Path.GetFullPath(path);if(!full.StartsWith(Audit+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Audit path required.");for(string at=full;!string.IsNullOrEmpty(at);at=Path.GetDirectoryName(at))if((File.Exists(at)||Directory.Exists(at))&&(File.GetAttributes(at)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked path refused.");return full;}
 static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
 static Picture Image(string input,string name,uint flags){
  var result=new Picture();IShellItemImageFactory factory=null;IntPtr handle=IntPtr.Zero;Guid iid=new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
  try{result.hr=SHCreateItemFromParsingName(input,IntPtr.Zero,ref iid,out factory);if(result.hr>=0)result.hr=factory.GetImage(new Size(256,256),flags,out handle);if(result.hr>=0&&handle!=IntPtr.Zero)using(var image=ShellBitmapEvidence.Read(handle)){result.width=image.Width;result.height=image.Height;result.path=Path.Combine(folder,name+".png");image.Save(result.path,ImageFormat.Png);ShellBitmapEvidence.SaveVisible(image,Path.Combine(folder,name+"-visible.png"));result.hash=Hash(File.ReadAllBytes(result.path));int first=image.GetPixel(0,0).ToArgb();for(int y=0;y<image.Height;y+=Math.Max(1,image.Height/20))for(int x=0;x<image.Width;x+=Math.Max(1,image.Width/20)){Color pixel=image.GetPixel(x,y);result.nonUniform|=pixel.ToArgb()!=first;result.minAlpha=Math.Min(result.minAlpha,pixel.A);result.maxAlpha=Math.Max(result.maxAlpha,pixel.A);}}}
  finally{if(handle!=IntPtr.Zero)DeleteObject(handle);if(factory!=null)Marshal.FinalReleaseComObject(factory);}return result;
 }
 static string Association(uint field,out int hr){uint size=32768;var value=new StringBuilder((int)size);hr=AssocQueryString(2,field,"Mori.exe",null,value,ref size);return value.ToString();}
 static string Value(string path){using(var key=Registry.ClassesRoot.OpenSubKey(path))return key==null?null:Convert.ToString(key.GetValue(""));}
 static void Class(Registration tx,string clsid,string dll){tx.Put("Software\\Classes\\CLSID\\"+clsid,"","Nook shell visual VM fixture");tx.Put("Software\\Classes\\CLSID\\"+clsid+"\\InprocServer32","",dll);tx.Put("Software\\Classes\\CLSID\\"+clsid+"\\InprocServer32","ThreadingModel","Apartment");}
 [STAThread]static int Main(string[] args){
  try{
   Guest();if(args.Length!=3)throw new ArgumentException("DLL, actual SVG, synthetic MP4 required.");string dll=Scoped(args[0]),actual=Scoped(args[1]),video=Scoped(args[2]);
   folder=Scoped(Path.Combine(Audit,"data","shell-factory-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(folder);
   string png=Path.Combine(folder,"native-before.png"),svg=Path.Combine(folder,"native-after.svg"),actualSvg=Path.Combine(folder,"actual-after.svg"),beforeMp4=Path.Combine(folder,"video-before.mp4"),afterMp4=Path.Combine(folder,"video-after.mp4");
   using(var image=new Bitmap(160,80))using(var g=Graphics.FromImage(image)){g.Clear(Color.White);g.FillRectangle(Brushes.Red,20,10,120,60);image.Save(png,ImageFormat.Png);}
   File.WriteAllText(svg,"<svg xmlns='http://www.w3.org/2000/svg' width='160' height='80'><rect x='20' y='10' width='120' height='60' fill='#d02040'/></svg>");File.Copy(actual,actualSvg);File.Copy(video,beforeMp4);File.Copy(video,afterMp4);
   string pngHandler=Value(".png\\shellex\\"+Category),videoHandler=Value(".mp4\\shellex\\"+Category);
   Picture pngBefore=Image(png,"png-baseline",8),videoBefore=Image(beforeMp4,"video-baseline",8);int baselineAppHr;string baselineAppReference=Association(23,out baselineAppHr);
   Check(pngBefore.hr>=0&&pngBefore.nonUniform,"png_baseline_has_real_thumbnail",pngBefore);
   Check(videoBefore.hr>=0&&videoBefore.nonUniform,"mp4_baseline_has_real_thumbnail",videoBefore);
   using(var tx=new Registration()){
    Class(tx,Icon,dll);Class(tx,Thumb,dll);
    tx.Put("Software\\Classes\\SystemFileAssociations\\.svg\\shellex\\"+Category,"",Thumb);
    string appIcon=Path.Combine(Path.GetDirectoryName(dll),"icons","app.ico");
    tx.Put(App+"\\Application","ApplicationIcon","\""+appIcon+"\",0");tx.Put(App+"\\DefaultIcon","","%1");tx.Put(App+"\\shellex\\IconHandler","",Icon);
    tx.Put(@"Software\Classes\.nook-icon-fixture","",@"Applications\Mori.exe");
    SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);
    Picture svgResult=Image(svg,"svg-shell-thumbnail",8),actualResult=Image(actualSvg,"actual-svg-shell-thumbnail",8);
    Check(svgResult.hr>=0&&svgResult.nonUniform&&svgResult.width==256&&svgResult.height==128,"svg_shell_thumbnailonly_has_actual_pixels",svgResult);
    Check(actualResult.hr>=0&&actualResult.nonUniform,"actual_svg_shell_thumbnailonly_has_actual_pixels",actualResult);
    string afterPng=Path.Combine(folder,"native-after.png");File.Copy(png,afterPng);Picture pngAfter=Image(afterPng,"png-after",8),videoAfter=Image(afterMp4,"video-after",8);
    Check(pngAfter.hr>=0&&pngAfter.hash==pngBefore.hash,"png_thumbnail_unchanged",pngAfter);
    Check(videoAfter.hr>=0&&videoAfter.hash==videoBefore.hash,"mp4_thumbnail_unchanged",videoAfter);
    Check(pngHandler==Value(".png\\shellex\\"+Category)&&videoHandler==Value(".mp4\\shellex\\"+Category),"existing_native_thumbnail_handlers_unchanged",null);
    string unknown=Path.Combine(folder,"document.nook-icon-fixture");File.WriteAllText(unknown,"Synthetic Nook icon fixture");Picture fallback=Image(unknown,"fallback-file-icon",4),appPicture=Image(appIcon,"application-icon",4),expectedFallback=Image(Path.Combine(Path.GetDirectoryName(dll),"icons","file.ico"),"expected-fallback",4);
    Check(fallback.hr>=0&&fallback.nonUniform&&fallback.hash!=appPicture.hash&&fallback.hash==expectedFallback.hash,"file_icon_distinct_from_application_logo",new{fallback,appPicture,expectedFallback});
    int hr;string appReference=Association(23,out hr);string executable=appReference.Trim('"');Picture openWithLogo=File.Exists(executable)?Image(executable,"openwith-logo",4):null;
    Check(hr>=0&&baselineAppHr>=0&&appReference==baselineAppReference&&openWithLogo!=null&&openWithLogo.hr>=0&&openWithLogo.hash==appPicture.hash,"openwith_application_icon_reference_preserved",new{hr,appReference,baselineAppReference,openWithLogo});
   }
   foreach(string imageName in new[]{"svg-shell-thumbnail","actual-svg-shell-thumbnail","actual-svg-shell-thumbnail-visible","fallback-file-icon","openwith-logo"}){string imagePath=Path.Combine(folder,imageName+".png");if(File.Exists(imagePath))File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"audit-shell-image-"+imageName+".txt"),Convert.ToBase64String(File.ReadAllBytes(imagePath)));}
   File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"audit-shell-factory.json"),Json.Serialize(new{schemaVersion=1,passed=failures==0,failures,guestOnly=true,physicalDesktopTouched=false,userChoiceModified=false,folder,checks=Checks}));return failures==0?0:1;
  }catch(Exception error){Console.Error.WriteLine(error);return 90;}
 }
}
