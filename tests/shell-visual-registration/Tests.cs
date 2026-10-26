using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

[assembly:AssemblyProduct("Nook")]
[assembly:AssemblyTitle("Nook shell registration fixtures")]
[assembly:AssemblyVersion("0.1.32.0")]
[assembly:AssemblyFileVersion("0.1.32.0")]

namespace MoriDesk {
 internal static class ShellVisualTests {
  static readonly List<string> passed=new List<string>();static string output;static int fixtureSequence;
  static string Hash(byte[] value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(value)).Replace("-","").ToLowerInvariant();}
  static void Check(bool result,string name){if(!result)throw new Exception(name);passed.Add(name);}
  static void Rejected<T>(Action action,string name) where T:Exception {T rejected=null;try{action();}catch(T error){rejected=error;}Check(rejected!=null&&rejected.GetType()==typeof(T),name+" ["+typeof(T).Name+"]");}
  static void Set(RegistryKey scope,string path,string name,object value){using(var key=scope.CreateSubKey(path))key.SetValue(name,value);}
  static string Get(RegistryKey scope,string path,string name){using(var key=scope.OpenSubKey(path))return key==null?null:key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string;}
  static bool Exists(RegistryKey scope,string path){using(var key=scope.OpenSubKey(path))return key!=null;}
  static string Class(string id){return "Software\\Classes\\CLSID\\"+id;}
  static string Thumb(string prefix){return "Software\\Classes\\"+prefix+"\\shellex\\"+ShellVisualRegistration.ThumbnailCategory;}
  static string RegistrySnapshot(RegistryKey key){var result=new List<string>();Snapshot(key,"",result);return string.Join("\n",result);}
  static void Snapshot(RegistryKey key,string relative,List<string> values){values.Add("K:"+relative);foreach(string name in key.GetValueNames().OrderBy(n=>n,StringComparer.Ordinal)){object value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);values.Add(relative+"|"+name+"|"+key.GetValueKind(name)+"|"+new JavaScriptSerializer().Serialize(value));}foreach(string child in key.GetSubKeyNames().OrderBy(n=>n,StringComparer.Ordinal))using(var sub=key.OpenSubKey(child))Snapshot(sub,relative+"\\"+child,values);}
  sealed class Fixture:IDisposable {
   internal readonly string RegistryPath,Root,Executable,Payload;internal readonly RegistryKey Scope,User,Machine;internal int Notices;
   internal Fixture(string name,int rootLength=0){
    // The queue already provides a unique output folder. Repeating scenario
    // names and GUIDs here needlessly exceeded legacy Windows path limits.
    string caseRoot=Path.Combine(output,"f"+(++fixtureSequence).ToString("000"));Directory.CreateDirectory(caseRoot);File.WriteAllText(Path.Combine(caseRoot,"scenario.txt"),name);
    RegistryPath="Software\\Nook.Tests\\ShellVisuals-"+Guid.NewGuid().ToString("N");Root=rootLength==0?Path.Combine(caseRoot,"app"):Path.Combine(caseRoot,new string('p',rootLength-caseRoot.Length-1));Executable=Path.Combine(Root,"0.1.32-20261026000000","Mori.exe");Payload=Path.Combine(Path.GetDirectoryName(Executable),ShellVisualRegistration.PayloadDirectory);
    Directory.CreateDirectory(Path.GetDirectoryName(Executable));File.Copy(Assembly.GetExecutingAssembly().Location,Executable);string launcher=InstalledApplicationState.LauncherPath(Root);Directory.CreateDirectory(Path.GetDirectoryName(launcher));File.Copy(Assembly.GetExecutingAssembly().Location,launcher);WritePayload("1");
    var descriptor=InstalledApplicationState.CreateDescriptor(Root,Executable,new string('a',64));InstalledApplicationState.Commit(Root,null,descriptor);
    Scope=Registry.CurrentUser.CreateSubKey(RegistryPath);User=Scope.CreateSubKey("User");Machine=Scope.CreateSubKey("Machine");string app=ShellVisualRegistration.ApplicationKey;
    Set(User,app,"FriendlyAppName","Nook");Set(User,app+"\\shell\\open\\command","","\""+launcher+"\" \"%1\"");Set(User,app+"\\Application","ApplicationIcon","\""+launcher+"\",0");Set(User,app+"\\DefaultIcon","","\""+launcher+"\",0");
   }
   internal byte[] WritePayload(string version){
    Directory.CreateDirectory(Path.Combine(Payload,"icons"));var files=new List<object>();foreach(string relative in new[]{ShellVisualRegistration.DllName,"resvg.exe","icons/app.ico","icons/file.ico","icons/SVG.ico","LICENSE.txt"}){byte[] data=Encoding.UTF8.GetBytes("fixture only, never executed; "+relative+"; "+version);string file=Path.Combine(Payload,relative.Replace('/',Path.DirectorySeparatorChar));File.WriteAllBytes(file,data);files.Add(new{path=relative,length=data.Length,sha256=Hash(data)});}byte[] bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{schemaVersion=1,componentVersion=version,files=files}));File.WriteAllBytes(Path.Combine(Payload,ShellVisualRegistration.ManifestName),bytes);return bytes;
   }
   internal bool Repair(Action<int> afterWrite=null){return ShellVisualRegistration.RepairInScope(User,Machine,Executable,Root,()=>Notices++,afterWrite);}
   internal string Component {get{return Get(User,Class(ShellVisualRegistration.IconHandlerClsid),"NookComponentDirectory");}}
   public void Dispose(){User.Dispose();Machine.Dispose();Scope.Dispose();if(!RegistryPath.StartsWith("Software\\Nook.Tests\\ShellVisuals-",StringComparison.Ordinal)||RegistryPath.Length!="Software\\Nook.Tests\\ShellVisuals-".Length+32)throw new InvalidOperationException("Invalid fixture cleanup target");Registry.CurrentUser.DeleteSubKeyTree(RegistryPath);}
  }
  static void HappyPath(){using(var f=new Fixture("happy")){
   string app=ShellVisualRegistration.ApplicationKey,choices="Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.svg\\UserChoice";
   Set(f.User,choices,"ProgId","Applications\\Mori.exe");Set(f.User,choices,"Hash","protected-choice");
   foreach(string extension in new[]{".png",".jpg",".mp4",".webm"}){Set(f.Machine,Thumb(extension),"","{OTHER-THUMBNAIL}");Set(f.User,"Software\\Classes\\"+extension,"","Other.Default");}
   string machineBefore=RegistrySnapshot(f.Machine);Check(f.Repair()&&f.Notices==1,"registration reports one successful change");
   Check(Get(f.User,app+"\\DefaultIcon","")=="%1"&&Get(f.User,ShellVisualRegistration.IconHandlerKey,"")==ShellVisualRegistration.IconHandlerClsid,"legacy single Applications handler delegates each file icon by extension");
   Check(Get(f.User,app+"\\Application","ApplicationIcon")=="\""+InstalledApplicationState.LauncherPath(f.Root)+"\",0","application branding keeps stable launcher icon");
   Check(Get(f.User,ShellVisualRegistration.SvgHandlerKey,"")==ShellVisualRegistration.SvgThumbnailClsid,"SVG provider uses SystemFileAssociations extension fallback");
   Check(Get(f.User,choices,"ProgId")=="Applications\\Mori.exe"&&Get(f.User,choices,"Hash")=="protected-choice","protected user default and hash remain unchanged");
   Check(RegistrySnapshot(f.Machine)==machineBefore,"all effective machine thumbnail providers remain byte-for-value unchanged");
   foreach(string extension in new[]{".png",".jpg",".mp4",".webm"})Check(Get(f.User,"Software\\Classes\\"+extension,"")=="Other.Default"&&!Exists(f.User,Thumb(extension)),"existing association and thumbnail fallback unchanged: "+extension);
   foreach(string id in new[]{ShellVisualRegistration.IconHandlerClsid,ShellVisualRegistration.SvgThumbnailClsid}){Check(Get(f.User,Class(id)+"\\InprocServer32","ThreadingModel")=="Apartment","native component uses Apartment threading: "+id);Check(Get(f.User,Class(id),"DisableProcessIsolation")==null,"thumbnail default isolation is not disabled: "+id);}
   Check(ShellVisualRegistration.IsOwnedIconHandler(f.User,f.Root)&&!ShellVisualRegistration.IsOwnedIconHandler(f.User,f.Root+"Other"),"lightweight ownership check requires exact installation root");
   string component=f.Component;Check(Path.GetDirectoryName(component)==Path.Combine(f.Root,"Launcher","Shell")&&Path.GetFileName(component)==Hash(File.ReadAllBytes(Path.Combine(f.Payload,"manifest.json"))),"native shell payload is immutable and independent from replaceable app folders");
   string before=RegistrySnapshot(f.User);DateTime stamp=File.GetLastWriteTimeUtc(Path.Combine(component,ShellVisualRegistration.DllName));Check(!f.Repair()&&f.Notices==1&&RegistrySnapshot(f.User)==before&&File.GetLastWriteTimeUtc(Path.Combine(component,ShellVisualRegistration.DllName))==stamp,"repeat repair performs no registry update, file replacement or cache notification");
   f.WritePayload("2");Check(f.Repair()&&f.Component!=component&&Directory.Exists(component)&&f.Notices==2,"new component uses another immutable folder and retains potentially loaded old DLL");
   Check(ShellVisualRegistration.RemoveOwnedInScope(f.User,f.Root,()=>f.Notices++,null),"owned feature can be removed without file deletion");Check(!Exists(f.User,ShellVisualRegistration.IconHandlerKey)&&!Exists(f.User,ShellVisualRegistration.SvgHandlerKey)&&Directory.Exists(component),"removal deregisters owned handlers while immutable loaded files stay intact");
   Check(Get(f.User,app+"\\DefaultIcon","")=="\""+InstalledApplicationState.LauncherPath(f.Root)+"\",0"&&Get(f.User,choices,"Hash")=="protected-choice","removal restores stable fallback and preserves default choices");Check(!ShellVisualRegistration.RemoveOwnedInScope(f.User,f.Root,null,null),"repeated removal is idempotent");
  }}
  static void WindowsGenericImage(){using(var f=new Fixture("windows-generic-image")){
   const string windows="{C7657C4A-9F68-40fa-A4DF-96BC08EB3551}";
   string generic=Thumb("SystemFileAssociations\\image"),cls=Class(windows),server=cls+"\\InprocServer32",svg="Software\\Classes\\.svg";
   string choices="Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.svg\\UserChoice";
   Set(f.Machine,generic,"",windows);Set(f.Machine,cls,"","Photo Thumbnail Provider");Set(f.Machine,server,"",@"%SystemRoot%\system32\PhotoMetadataHandler.dll");Set(f.Machine,server,"ThreadingModel","Both");
   Set(f.Machine,svg,"","svgfile");Set(f.Machine,svg,"Content Type","image/svg+xml");Set(f.User,choices,"ProgId","MSEdgeHTM");Set(f.User,choices,"Hash","unchanged-edge-choice");
   string before=RegistrySnapshot(f.Machine);
   Check(!ShellVisualRegistration.HasForeignSvgProvider(f.User,f.Machine),"real Windows image fallback without an SVG codec does not block SVG registration");
   Check(f.Repair()&&Get(f.User,ShellVisualRegistration.SvgHandlerKey,"")==ShellVisualRegistration.SvgThumbnailClsid&&Exists(f.User,Class(ShellVisualRegistration.SvgThumbnailClsid)),"production registration publishes SVG on the real Windows generic-image baseline");
   Check(RegistrySnapshot(f.Machine)==before&&Get(f.User,choices,"ProgId")=="MSEdgeHTM"&&Get(f.User,choices,"Hash")=="unchanged-edge-choice","Windows generic handler, codec registration and protected default stay unchanged");
   Set(f.Machine,svg,"PerceivedType","image");Check(!ShellVisualRegistration.HasForeignSvgProvider(f.User,f.Machine),"explicit image perceived type keeps the same narrowly allowed Windows fallback");
   ShellVisualRegistration.RemoveOwnedInScope(f.User,f.Root,null,null);
   foreach(string scenario in new[]{"user-class-override","custom-machine-dll","unknown-generic","user-generic-override"}){
    if(scenario=="user-class-override")Set(f.User,server,"",@"%SystemRoot%\system32\PhotoMetadataHandler.dll");
    if(scenario=="custom-machine-dll")Set(f.Machine,server,"",@"C:\ThirdParty\PhotoMetadataHandler.dll");
    if(scenario=="unknown-generic")Set(f.Machine,generic,"","{THIRD-PARTY-IMAGE}");
    if(scenario=="user-generic-override")Set(f.User,generic,"",windows);
    string machineBefore=RegistrySnapshot(f.Machine),userServer=Get(f.User,server,"");
    Check(ShellVisualRegistration.HasForeignSvgProvider(f.User,f.Machine),"generic fallback override remains protected: "+scenario);
    f.Repair();Check(!Exists(f.User,Class(ShellVisualRegistration.SvgThumbnailClsid))&&Get(f.User,ShellVisualRegistration.SvgHandlerKey,"")==null&&RegistrySnapshot(f.Machine)==machineBefore&&Get(f.User,server,"")==userServer,"production registration leaves third-party or overridden SVG handling untouched: "+scenario);
    ShellVisualRegistration.RemoveOwnedInScope(f.User,f.Root,null,null);
    if(scenario=="user-class-override")f.User.DeleteSubKeyTree(cls);
    if(scenario=="custom-machine-dll")Set(f.Machine,server,"",@"%SystemRoot%\system32\PhotoMetadataHandler.dll");
    if(scenario=="unknown-generic")Set(f.Machine,generic,"",windows);
    if(scenario=="user-generic-override")f.User.DeleteSubKeyTree(generic);
   }
   foreach(string scenario in new[]{"user-direct","machine-direct","user-sfa","machine-sfa","selected","default","curver","custom-perceived"}){
    RegistryKey scope=scenario.StartsWith("user-",StringComparison.Ordinal)?f.User:f.Machine;string path;
    if(scenario.EndsWith("direct",StringComparison.Ordinal))path=Thumb(".svg");
    else if(scenario.EndsWith("sfa",StringComparison.Ordinal))path=Thumb("SystemFileAssociations\\.svg");
    else if(scenario=="selected")path=Thumb("MSEdgeHTM");
    else if(scenario=="default")path=Thumb("svgfile");
    else if(scenario=="curver"){Set(f.Machine,"Software\\Classes\\svgfile\\CurVer","","svgfile.2");path=Thumb("svgfile.2");}
    else{Set(f.Machine,svg,"PerceivedType","custom-vector");path=Thumb("SystemFileAssociations\\custom-vector");}
    Set(scope,path,"",windows);Check(ShellVisualRegistration.HasForeignSvgProvider(f.User,f.Machine),"even the Windows CLSID remains protected at a dedicated SVG association: "+scenario);scope.DeleteSubKeyTree(path);
    if(scenario=="curver")f.Machine.DeleteSubKeyTree("Software\\Classes\\svgfile\\CurVer");
    if(scenario=="custom-perceived")Set(f.Machine,svg,"PerceivedType","image");
   }
   Set(f.Machine,cls+"\\TreatAs","","{OTHER-COM-CLASS}");Check(ShellVisualRegistration.HasForeignSvgProvider(f.User,f.Machine),"redirected machine COM identity is not treated as the Windows default");f.Machine.DeleteSubKeyTree(cls+"\\TreatAs");
   Set(f.Machine,server,"ThreadingModel","Apartment");Check(ShellVisualRegistration.HasForeignSvgProvider(f.User,f.Machine),"unexpected machine class metadata is conservatively preserved");
  }}
  static void ForeignProviders(){
   foreach(string where in new[]{"machine-sfa","user-direct","machine-direct","selected","selected-application","selected-machine-application","default","curver","perceived","image"})using(var f=new Fixture(where)){
    string path=null;RegistryKey scope=f.User;
    if(where=="machine-sfa"){scope=f.Machine;path=Thumb("SystemFileAssociations\\.svg");}
    if(where=="user-direct")path=Thumb(".svg");if(where=="machine-direct"){scope=f.Machine;path=Thumb(".svg");}
    if(where=="selected"){Set(f.User,"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.svg\\UserChoice","ProgId","Other.Svg");path=Thumb("Other.Svg");}
    if(where=="selected-application"){Set(f.User,"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.svg\\UserChoice","ProgId","Applications\\Mori.exe");path=Thumb("Applications\\Mori.exe");}
    if(where=="selected-machine-application"){Set(f.User,"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.svg\\UserChoice","ProgId","Applications\\Mori.exe");scope=f.Machine;path=Thumb("Applications\\Mori.exe");}
    if(where=="default"){Set(f.Machine,"Software\\Classes\\.svg","","Other.Svg");scope=f.Machine;path=Thumb("Other.Svg");}
    if(where=="curver"){Set(f.User,"Software\\Classes\\.svg","","Other.Svg");Set(f.Machine,"Software\\Classes\\Other.Svg\\CurVer","","Other.Svg.2");scope=f.Machine;path=Thumb("Other.Svg.2");}
    if(where=="perceived"){Set(f.Machine,"Software\\Classes\\.svg","PerceivedType","custom-vector");scope=f.Machine;path=Thumb("SystemFileAssociations\\custom-vector");}
    if(where=="image"){scope=f.Machine;path=Thumb("SystemFileAssociations\\image");}
    Set(scope,path,"","{FOREIGN-THUMBNAIL}");Check(ShellVisualRegistration.HasForeignSvgProvider(f.User,f.Machine),"foreign effective provider discovered: "+where);Check(f.Repair()&&Get(f.User,ShellVisualRegistration.SvgHandlerKey,"")==null&&Get(scope,path,"")=="{FOREIGN-THUMBNAIL}","foreign provider is preserved while independent icon feature works: "+where);
   }
   using(var f=new Fixture("foreign-icon")){Set(f.Machine,ShellVisualRegistration.IconHandlerKey,"","{OTHER-ICON}");Check(f.Repair()&&Get(f.User,ShellVisualRegistration.IconHandlerKey,"")==null&&Get(f.User,ShellVisualRegistration.SvgHandlerKey,"")==ShellVisualRegistration.SvgThumbnailClsid,"foreign icon handler does not block unrelated SVG fallback or get overwritten");}
   foreach(string id in new[]{ShellVisualRegistration.IconHandlerClsid,ShellVisualRegistration.SvgThumbnailClsid})using(var f=new Fixture("class-collision")){Set(f.User,Class(id),"","Another registered component");Check(f.Repair()&&Get(f.User,Class(id),"")=="Another registered component","unowned CLSID collision is preserved: "+id);}
   using(var f=new Fixture("foreign-on-remove")){f.Repair();string folder=f.Component;Set(f.User,ShellVisualRegistration.IconHandlerKey,"","{NEW-FOREIGN-ICON}");Set(f.User,Class(ShellVisualRegistration.IconHandlerClsid),"UnknownValue","preserve");ShellVisualRegistration.RemoveOwnedInScope(f.User,f.Root,null,null);Check(Get(f.User,ShellVisualRegistration.IconHandlerKey,"")=="{NEW-FOREIGN-ICON}"&&Get(f.User,Class(ShellVisualRegistration.IconHandlerClsid),"UnknownValue")=="preserve"&&Directory.Exists(folder),"removal preserves subsequently changed handler and unowned registry values");}
  }
  static void InvalidInventory(){
   foreach(string type in new[]{"changed","extra-file","extra-folder","missing","bad-manifest","duplicate-manifest-key"})using(var f=new Fixture("inventory-"+type)){
    string before=RegistrySnapshot(f.User);if(type=="changed")File.AppendAllText(Path.Combine(f.Payload,ShellVisualRegistration.DllName),"changed");if(type=="extra-file")File.WriteAllText(Path.Combine(f.Payload,"extra.dll"),"unlisted");if(type=="extra-folder")Directory.CreateDirectory(Path.Combine(f.Payload,"extra"));if(type=="missing")File.Delete(Path.Combine(f.Payload,"icons","SVG.ico"));if(type=="bad-manifest")File.WriteAllText(Path.Combine(f.Payload,"manifest.json"),"{invalid");if(type=="duplicate-manifest-key"){string json=File.ReadAllText(Path.Combine(f.Payload,"manifest.json"));File.WriteAllText(Path.Combine(f.Payload,"manifest.json"),json.Replace("\"schemaVersion\":1","\"schemaVersion\":2,\"schemaVersion\":1"));}
    Rejected<InvalidDataException>(()=>f.Repair(),"invalid source inventory is refused: "+type);Check(RegistrySnapshot(f.User)==before&&f.Notices==0,"invalid inventory cannot partially change associations: "+type);
   }
   using(var f=new Fixture("existing-tamper")){f.Repair();File.AppendAllText(Path.Combine(f.Component,ShellVisualRegistration.DllName),"tamper");string before=RegistrySnapshot(f.User);Rejected<InvalidDataException>(()=>f.Repair(),"changed published component is never silently repaired or reused");Check(RegistrySnapshot(f.User)==before&&f.Notices==1,"changed published component preserves existing registration for explicit recovery");}
   using(var f=new Fixture("inactive")){string inactive=Path.Combine(f.Root,"0.1.32-20261026000001","Mori.exe");Directory.CreateDirectory(Path.GetDirectoryName(inactive));File.Copy(f.Executable,inactive);string before=RegistrySnapshot(f.User);Check(!ShellVisualRegistration.RepairInScope(f.User,f.Machine,inactive,f.Root,null,null)&&RegistrySnapshot(f.User)==before&&!Directory.Exists(Path.Combine(f.Root,"Launcher","Shell")),"uncommitted candidate cannot publish resources or claim registrations");}
   using(var f=new Fixture("unknown-command")){Set(f.User,ShellVisualRegistration.ApplicationKey+"\\shell\\open\\command","","unknown.exe %1");string before=RegistrySnapshot(f.User);Check(!f.Repair()&&RegistrySnapshot(f.User)==before,"unknown existing application command is never adopted");}
  }
  static void ManifestValidation(){using(var f=new Fixture("parse")){
   byte[] bytes=File.ReadAllBytes(Path.Combine(f.Payload,"manifest.json"));string valid=Encoding.UTF8.GetString(bytes);Check(ShellVisualRegistration.ParseManifest(bytes).files.Length==6,"exact bounded manifest fixture parses");
   foreach(string bad in new[]{"../outside.dll","icons/../../outside.dll","C:/outside.dll","/root.dll","icons//file.ico","icons\\file.ico","icons/CON.ico","icons/file.ico.","icons/file.ico:ads","icons/../file.ico"}){string text=valid.Replace("icons/SVG.ico",bad.Replace("\\","\\\\"));Rejected<InvalidDataException>(()=>ShellVisualRegistration.ParseManifest(Encoding.UTF8.GetBytes(text)),"unsafe inventory path rejected: "+bad);}
   foreach(string text in new[]{valid.Replace("\"schemaVersion\":1","\"schemaVersion\":2"),valid.Replace("\"componentVersion\":\"1\"","\"componentVersion\":\"1\",\"unknown\":true"),valid.Replace("icons/SVG.ico","icons/FILE.ico"),valid.Replace("\"schemaVersion\":1","\"schemaVersion\":2,\"\\u0073chemaVersion\":1"),valid.Replace("\"length\":", "\"length\":1.5,\"discard\":"),valid+"{}"})Rejected<InvalidDataException>(()=>ShellVisualRegistration.ParseManifest(Encoding.UTF8.GetBytes(text)),"strict schema rejects coercion, duplicate aliases, extra fields or trailing records");
  }}
  static void Rollback(){
   int writes=0;using(var f=new Fixture("count-writes")){f.Repair(n=>writes=n);}Check(writes>8,"fault tests exercise multiple independent registry values");
   for(int failure=1;failure<=writes;failure++)using(var f=new Fixture("rollback-"+failure)){string before=RegistrySnapshot(f.User);int point=failure;Rejected<IOException>(()=>f.Repair(n=>{if(n==point)throw new IOException("Injected registry failure");}),"write fault propagates at point "+point);Check(RegistrySnapshot(f.User)==before&&f.Notices==0,"write fault restores exact prior registry at point "+point);}
   using(var f=new Fixture("rollback-concurrent")){string key=Class(ShellVisualRegistration.IconHandlerClsid);Rejected<IOException>(()=>f.Repair(n=>{if(n==2){Set(f.User,key,"","Changed by another owner");throw new IOException("Injected concurrent update");}}),"concurrent value fault is detected");Check(Get(f.User,key,"")=="Changed by another owner"&&Get(f.User,key,"NookOwner")==null,"rollback compare-and-restore preserves another writer's replacement");}
   using(var f=new Fixture("provider-arrives")){Rejected<IOException>(()=>f.Repair(n=>{if(n==2)Set(f.User,ShellVisualRegistration.SvgHandlerKey,"","{NEW-PROVIDER}");}),"provider installed during component registration aborts our competing claim");Check(Get(f.User,ShellVisualRegistration.SvgHandlerKey,"")=="{NEW-PROVIDER}"&&!Exists(f.User,Class(ShellVisualRegistration.SvgThumbnailClsid))&&f.Notices==0,"mid-transaction foreign thumbnail provider remains intact after rollback");}
   using(var f=new Fixture("icon-arrives")){Rejected<IOException>(()=>f.Repair(n=>{if(n==2)Set(f.User,ShellVisualRegistration.IconHandlerKey,"","{NEW-ICON}");}),"icon handler installed during class writes aborts our competing claim");Check(Get(f.User,ShellVisualRegistration.IconHandlerKey,"")=="{NEW-ICON}"&&!Exists(f.User,Class(ShellVisualRegistration.IconHandlerClsid)),"mid-transaction foreign icon handler remains intact after rollback");}
   using(var f=new Fixture("remove-fault")){f.Repair();string before=RegistrySnapshot(f.User);Rejected<IOException>(()=>ShellVisualRegistration.RemoveOwnedInScope(f.User,f.Root,null,n=>{if(n==3)throw new IOException("Injected removal fault");}),"removal faults propagate");Check(RegistrySnapshot(f.User)==before,"failed removal restores exact working registrations");}
  }
  static void JunctionChecks(){using(var f=new Fixture("source-junction")){
   string target=Path.Combine(output,"junction-target-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(target);string link=Path.Combine(f.Payload,"linked");
   var start=new ProcessStartInfo("cmd.exe","/d /c mklink /J \""+link+"\" \""+target+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};using(var process=Process.Start(start)){string message=process.StandardOutput.ReadToEnd()+process.StandardError.ReadToEnd();if(!process.WaitForExit(5000)||process.ExitCode!=0)throw new Exception("Unable to create required VM junction fixture: "+message);}
   string before=RegistrySnapshot(f.User);Rejected<IOException>(()=>f.Repair(),"junction inside source inventory is refused");Check(RegistrySnapshot(f.User)==before,"source junction never changes registrations");
  }}
  static void LongPathFailsClosed(){using(var f=new Fixture("long-component-path",180)){
   // The app payload fits legacy limits, but adding the immutable 64-character
   // component identity does not. Optional integration must stop before writes.
   string before=RegistrySnapshot(f.User);Rejected<PathTooLongException>(()=>f.Repair(),"long component installation path fails closed");Check(RegistrySnapshot(f.User)==before&&f.Notices==0,"long-path failure leaves all associations unchanged");
  }}
  static int Main(string[] args){
   if(args.Length!=2||(args[0]!="--isolated-fixtures"&&args[0]!="--generic-image-fixtures")){Console.Error.WriteLine("Run only in the development VM with --isolated-fixtures or --generic-image-fixtures and a fresh output directory.");return 2;}
   try{bool guest=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row){guest|=System.Text.RegularExpressions.Regex.IsMatch(Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]),"QEMU|KVM|Bochs",System.Text.RegularExpressions.RegexOptions.IgnoreCase);}if(!guest||!Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Dedicated NookTester development VM required before fixture writes.");}catch(Exception error){Console.Error.WriteLine(error.Message);return 2;}
   output=Path.GetFullPath(args[1]);if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any()){Console.Error.WriteLine("Fixture directory must be empty.");return 2;}Directory.CreateDirectory(output);
   try{WindowsGenericImage();if(args[0]=="--isolated-fixtures"){HappyPath();ForeignProviders();InvalidInventory();ManifestValidation();Rollback();JunctionChecks();LongPathFailsClosed();}File.WriteAllText(Path.Combine(output,"results.json"),new JavaScriptSerializer().Serialize(new{passed=true,count=passed.Count,checks=passed}),new UTF8Encoding(false));Console.WriteLine("PASS "+passed.Count+" isolated shell registration checks; "+output);return 0;}
   catch(Exception error){File.WriteAllText(Path.Combine(output,"results.json"),new JavaScriptSerializer().Serialize(new{passed=false,count=passed.Count,checks=passed,error=error.ToString()}),new UTF8Encoding(false));Console.Error.WriteLine(error);return 1;}
  }
 }
}
