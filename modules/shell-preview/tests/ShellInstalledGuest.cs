using System;
using System.Collections.Generic;
using System.Diagnostics;
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

// Read-only integration evidence for the real installed guest application.
// Does not register a COM class, alter associations, launch Nook, or install it.
internal static class ShellInstalledGuest {
 const string Audit=@"C:\NookLab\Audit", Icon="{612E3A5A-3C2C-4709-8C54-9C3EA927C086}", Thumb="{FA45A458-130E-47BA-AAE0-1202D6930A79}", Category="{e357fccd-a995-4576-b01f-234630154e96}";
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=8*1024*1024,RecursionLimit=40};
 static readonly List<object> checks=new List<object>();
 static int failures;
 [StructLayout(LayoutKind.Sequential)]struct Size{public int width,height;public Size(int w,int h){width=w;height=h;}}
 [ComImport,Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IShellItemImageFactory{[PreserveSig]int GetImage(Size size,uint flags,out IntPtr bitmap);}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)]static extern int SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItemImageFactory result);
 [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]static extern int AssocQueryString(uint flags,uint str,string association,string extra,StringBuilder value,ref uint length);
 [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr bitmap);
 static void Require(bool yes,string message){if(!yes)throw new InvalidOperationException(message);}
 static void Guest(){bool yes=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject item in query.Get())using(item){yes|=System.Text.RegularExpressions.Regex.IsMatch(Convert.ToString(item["Manufacturer"])+" "+Convert.ToString(item["Model"]),"QEMU|KVM|Bochs",System.Text.RegularExpressions.RegexOptions.IgnoreCase);}Require(yes&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester VM required.");}
 static void NoLinks(string path){for(string at=Path.GetFullPath(path);!string.IsNullOrEmpty(at);at=Path.GetDirectoryName(at))if((File.Exists(at)||Directory.Exists(at))&&(File.GetAttributes(at)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked integration path refused.");}
 static string Hash(string file){using(var input=File.OpenRead(file))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
 static object ReadKey(RegistryKey root,string path){using(var key=root.OpenSubKey(path,false)){if(key==null)return null;var result=new SortedDictionary<string,object>(StringComparer.Ordinal);foreach(string name in key.GetValueNames()){object value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);if(value is byte[])value=Convert.ToBase64String((byte[])value);result[name]=value;}return result;}}
 static string Value(RegistryKey root,string path,string name=""){using(var key=root.OpenSubKey(path,false)){if(key==null)return null;object raw=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);return raw==null?null:Convert.ToString(raw);}}
 static object RegistrySnapshot(){
  var entries=new List<object>();
  string[] extensions={".svg",".png",".jpg",".jpeg",".gif",".webp",".bmp",".tif",".tiff",".ico",".avif",".heic",".heif",".mp4",".webm",".mov",".mkv",".pdf",".ai",".psd",".txt",".zip"};
  foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})using(var root=RegistryKey.OpenBaseKey(hive,view)){
   var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   paths.Add(@"Software\Classes\.svg");paths.Add(@"Software\Classes\SystemFileAssociations\image\shellex\"+Category);
   string generic=Value(root,@"Software\Classes\SystemFileAssociations\image\shellex\"+Category);if(string.IsNullOrEmpty(generic))using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))generic=Value(machine,@"Software\Classes\SystemFileAssociations\image\shellex\"+Category);Guid genericId;if(Guid.TryParse(generic,out genericId)){paths.Add(@"Software\Classes\CLSID\"+generic);paths.Add(@"Software\Classes\CLSID\"+generic+@"\InprocServer32");paths.Add(@"Software\Classes\CLSID\"+generic+@"\LocalServer32");}
   string svgDefault=Value(root,@"Software\Classes\.svg"),perceived=Value(root,@"Software\Classes\.svg","PerceivedType");
   if(!string.IsNullOrEmpty(perceived)&&perceived.IndexOf('\\')<0)paths.Add(@"Software\Classes\SystemFileAssociations\"+perceived+@"\shellex\"+Category);
   var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);for(int hop=0;hop<8&&!string.IsNullOrEmpty(svgDefault)&&svgDefault.IndexOf('\\')<0&&visited.Add(svgDefault);hop++){paths.Add(@"Software\Classes\"+svgDefault+@"\shellex\"+Category);paths.Add(@"Software\Classes\"+svgDefault+@"\CurVer");svgDefault=Value(root,@"Software\Classes\"+svgDefault+@"\CurVer");}
   foreach(string suffix in new[]{"DefaultIcon","Application","shell\\open\\command","shellex\\IconHandler"})paths.Add(@"Software\Classes\Applications\Mori.exe\"+suffix);
   foreach(string id in new[]{Icon,Thumb}){paths.Add(@"Software\Classes\CLSID\"+id);paths.Add(@"Software\Classes\CLSID\"+id+@"\InprocServer32");}
   foreach(string ext in extensions){
    paths.Add(@"Software\Classes\"+ext+@"\shellex\"+Category);paths.Add(@"Software\Classes\SystemFileAssociations\"+ext+@"\shellex\"+Category);
    string choice=@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\"+ext+@"\UserChoice";paths.Add(choice);
    string progId=Value(root,choice,"ProgId");if(!string.IsNullOrEmpty(progId)&&progId.IndexOf('\\')<0){paths.Add(@"Software\Classes\"+progId+@"\shellex\"+Category);paths.Add(@"Software\Classes\"+progId+@"\DefaultIcon");}
   }
   foreach(string path in paths.OrderBy(p=>p,StringComparer.OrdinalIgnoreCase))entries.Add(new{hive=hive.ToString(),view=view.ToString(),path,values=ReadKey(root,path)});
  }return entries;
 }
 static void Check(bool passed,string name,object detail){checks.Add(new{name,passed,detail});if(!passed)failures++;}
 static object Components(string program){string shell=Path.Combine(program,"Launcher","Shell");NoLinks(shell);var result=new List<object>();if(!Directory.Exists(shell))return result;var directories=Directory.GetDirectories(shell);Require(directories.Length<=16,"Bounded component inventory required.");foreach(string dir in directories){NoLinks(dir);string manifest=Path.Combine(dir,"manifest.json");if(!File.Exists(manifest)){result.Add(new{directory=dir,manifestExists=false});continue;}Require(new FileInfo(manifest).Length<=1024*1024,"Bounded component manifest required.");var value=Json.DeserializeObject(File.ReadAllText(manifest)) as Dictionary<string,object>;var records=new List<object>();object raw;if(value!=null&&value.TryGetValue("files",out raw))foreach(var item in (object[])raw){Require(records.Count<512,"Bounded component file inventory required.");var record=(Dictionary<string,object>)item;string relative=Convert.ToString(record["path"]),file=Path.GetFullPath(Path.Combine(dir,relative));Require(file.StartsWith(dir+"\\",StringComparison.OrdinalIgnoreCase),"Component manifest escape.");NoLinks(file);string expected=Convert.ToString(record["sha256"]),actual=File.Exists(file)?Hash(file):null;records.Add(new{relative,expected,actual,match=string.Equals(expected,actual,StringComparison.OrdinalIgnoreCase)});}result.Add(new{directory=dir,manifestExists=true,manifestSha256=Hash(manifest),files=records});}return result;}
 sealed class Picture{public int hr,width,height;public bool nonUniform;public string hash,path;}
 static Picture Image(string path,string destination){var result=new Picture();IShellItemImageFactory factory=null;IntPtr handle=IntPtr.Zero;Guid iid=new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");try{result.hr=SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out factory);if(result.hr>=0)result.hr=factory.GetImage(new Size(256,256),8,out handle);if(result.hr>=0&&handle!=IntPtr.Zero)using(var image=ShellBitmapEvidence.Read(handle)){image.Save(destination,ImageFormat.Png);ShellBitmapEvidence.SaveVisible(image,Path.ChangeExtension(destination,"visible.png"));result.width=image.Width;result.height=image.Height;result.path=destination;result.hash=Hash(destination);int first=image.GetPixel(0,0).ToArgb();for(int y=0;y<image.Height;y+=Math.Max(1,image.Height/20))for(int x=0;x<image.Width;x+=Math.Max(1,image.Width/20))result.nonUniform|=image.GetPixel(x,y).ToArgb()!=first;}}finally{if(handle!=IntPtr.Zero)DeleteObject(handle);if(factory!=null)Marshal.FinalReleaseComObject(factory);}return result;}
 static void Verify(string package,string program,string label){
  string registered=Value(Registry.CurrentUser,@"Software\Classes\CLSID\"+Thumb+@"\InprocServer32");string shell=Path.Combine(program,"Launcher","Shell")+"\\";
  bool stable=!string.IsNullOrEmpty(registered)&&Path.GetFullPath(registered).StartsWith(shell,StringComparison.OrdinalIgnoreCase)&&File.Exists(registered);Check(stable,"installed_thumbnail_dll_uses_stable_launcher_shell",registered);
  Check(Value(Registry.CurrentUser,@"Software\Classes\SystemFileAssociations\.svg\shellex\"+Category)==Thumb,"actual_svg_thumbnail_registration_active",null);
  Check(Value(Registry.CurrentUser,@"Software\Classes\Applications\Mori.exe\DefaultIcon")=="%1","file_icon_dispatcher_active",null);
  Check(Value(Registry.CurrentUser,@"Software\Classes\Applications\Mori.exe\shellex\IconHandler")==Icon,"actual_file_icon_handler_active",null);
  foreach(string id in new[]{Icon,Thumb})Check(Value(Registry.CurrentUser,@"Software\Classes\CLSID\"+id,"NookOwner")=="Nook shell visuals v1","installed_owner_"+(id==Icon?"icon":"thumbnail"),null);
  Check(Value(Registry.CurrentUser,@"Software\Classes\CLSID\"+Thumb,"DisableProcessIsolation")==null,"thumbnail_surrogate_isolation_preserved",null);
  string folder=Path.Combine(Audit,"data","installed-shell-"+label+"-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(folder);
  foreach(string name in new[]{"actual-user.svg","counting.mp4"}){string input=Path.Combine(package,"fixtures",name);NoLinks(input);Require(File.Exists(input),"Hash-pinned fixture missing.");string copy=Path.Combine(folder,name);File.Copy(input,copy);var timer=Stopwatch.StartNew();var picture=Image(copy,Path.Combine(folder,name+".png"));Check(picture.hr>=0&&picture.nonUniform,"installed_shell_real_thumbnail_"+Path.GetExtension(name).Trim('.'),new{picture,elapsedMilliseconds=timer.ElapsedMilliseconds});if(File.Exists(picture.path))File.WriteAllText(Path.Combine(package,"audit-installed-shell-image-"+label+"-"+Path.GetExtension(name).Trim('.')+".txt"),Convert.ToBase64String(File.ReadAllBytes(picture.path)));}
  string png=Path.Combine(folder,"native.png");using(var image=new Bitmap(160,80))using(var g=Graphics.FromImage(image)){g.Clear(Color.White);g.FillEllipse(Brushes.Blue,10,10,100,60);image.Save(png,ImageFormat.Png);}var native=Image(png,Path.Combine(folder,"native-result.png"));Check(native.hr>=0&&native.nonUniform,"installed_shell_png_thumbnail_retained",native);
  uint count=32768;var app=new StringBuilder((int)count);int hr=AssocQueryString(2,23,"Mori.exe",null,app,ref count);string launcher=Path.Combine(program,"Launcher","Mori.exe");Check(hr>=0&&app.ToString().IndexOf(launcher,StringComparison.OrdinalIgnoreCase)>=0,"openwith_application_uses_stable_launcher",new{hr,reference=app.ToString()});
 }
 [STAThread]static int Main(string[] args){try{Guest();Require(args.Length==2&&(args[0]=="--snapshot"||args[0]=="--verify")&&System.Text.RegularExpressions.Regex.IsMatch(args[1],"\\A[a-z0-9-]{1,32}\\z"),"Explicit guest snapshot/verify and label required.");string package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Require(System.Text.RegularExpressions.Regex.IsMatch(package,@"\AC:\\NookLab\\Audit\\apps\\[A-Fa-f0-9]{12}\z"),"Hash-pinned guest package required.");NoLinks(package);string program=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk");NoLinks(program);if(args[0]=="--verify")Verify(package,program,args[1]);var loaded=new List<object>();foreach(var process in Process.GetProcessesByName("dllhost"))using(process){try{foreach(ProcessModule module in process.Modules)if(module.ModuleName.Equals("Nook.ShellPreview.dll",StringComparison.OrdinalIgnoreCase))loaded.Add(new{pid=process.Id,path=module.FileName});}catch{}}
  var report=new{schemaVersion=1,guestOnly=true,registryModified=false,physicalDesktopTouched=false,passed=failures==0,failures,programRoot=program,registry=RegistrySnapshot(),components=Components(program),loadedShellModules=loaded,checks,utc=DateTime.UtcNow.ToString("O")};string destination=Path.Combine(package,"audit-installed-shell-"+args[1]+".json");Require(!File.Exists(destination),"Evidence labels cannot overwrite prior output.");File.WriteAllText(destination,Json.Serialize(report),new UTF8Encoding(false));return failures==0?0:1;
 }catch(Exception e){Console.Error.WriteLine(e);return 90;}}
}
