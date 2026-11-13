using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("Nook public 42 to 45 installed fixture - VM TEST ONLY")]
// Adapted from the October InstalledFixtureGuest. Explicit model-only fixture
// creation and comparison; never invokes Main, a Window, or an installer.
internal static class InstalledUpgradeFixtureGuest {
 const string OldHash="0023F5CF9C7B6021ED8071E9936EB20282C1FBBE467716B33146AA52110662EB";
 const string NewHash="97B122310B1033BE7EBDA63730F929C256D8D0972CBC3497C7A7D6539CCA50D6";
 const string Fixture=@"C:\NookLab\Upgrade42To45-20261113-r3-healthy";
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=48};
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static string Package,Data,Program,App;static Assembly Product;static readonly List<string> Checks=new List<string>();
 static void Need(bool value,string reason){if(!value)throw new InvalidDataException(reason);Checks.Add(reason);}
 static void NoLinks(string path){for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))if((Directory.Exists(part)||File.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked fixture refused.");}
 static string Hash(string file){NoLinks(file);using(var input=File.OpenRead(file))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
 static Dictionary<string,object> Read(string path){NoLinks(path);Need(new FileInfo(path).Length<=4*1024*1024,"Bounded fixture JSON.");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));}
 static void Save(string path,object value){NoLinks(path);using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,Utf8)){writer.Write(Json.Serialize(value));writer.Flush();file.Flush(true);}}
 static string Text(object value){return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
 static object Call(object instance,string method,params object[] args){var matches=instance.GetType().GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(m=>m.Name==method&&m.GetParameters().Length==args.Length).ToArray();Need(matches.Length==1,"Exact model method: "+method);return matches[0].Invoke(instance,args);}
 static object Get(object value,string property){return value.GetType().GetProperty(property).GetValue(value,null);}
 static object Field(object value,string name){return value.GetType().GetField(name).GetValue(value);}
 static void Set(object value,string name,object item){value.GetType().GetProperty(name).SetValue(value,item,null);}
 static object Create(string type,params object[] args){return Activator.CreateInstance(Product.GetType(type,true),args);}
 static object[] Items(object items){return ((IEnumerable)items).Cast<object>().ToArray();}
 static Dictionary<string,object> Properties(object value,string names){return names.Split(' ').ToDictionary(name=>name,name=>Get(value,name));}
 static void Guest(string expected){
  bool guest=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)guest|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(guest&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated NookTester VM required before data access.");
  Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed package required.");NoLinks(Package);
  // The generic audit broker may supply an isolation override. Remove it only
  // after the hardware/user guard, exactly as the existing real VmDriver does.
  // All model calls below use the actual VM user's default physical profile.
  Environment.SetEnvironmentVariable("MORI_DESK_DATA",null);
  Need(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")),"Actual default profile required; no isolation override reaches model calls.");
  Data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MoriDesk");Program=Path.Combine(Path.GetDirectoryName(Data),"Programs","MoriDesk");
  Need(Data==@"C:\Users\NookTester\AppData\Local\MoriDesk","Exact dedicated-user default profile required.");NoLinks(Data);NoLinks(Program);NoLinks(Fixture);
  foreach(string name in new[]{"Mori","Nook","Nook-Setup"}){var processes=Process.GetProcessesByName(name);try{Need(processes.Length==0,"Close installed "+name+" before model fixture work.");}finally{foreach(var process in processes)process.Dispose();}}
  var active=Read(Path.Combine(Program,"active-install.json"));string relative=Text(active["executable"]);Need(Regex.IsMatch(relative,@"\A[0-9][A-Za-z0-9.-]{1,120}\\Mori\.exe\z"),"Strict installed executable record.");App=Path.Combine(Program,relative);Need(Hash(App)==expected&&Text(active["sha256"]).Equals(expected,StringComparison.OrdinalIgnoreCase),"Exact genuine installed application hash.");
  Product=Assembly.LoadFrom(App);
 }
 static BitmapSource Image(){byte[] pixels=new byte[192*128*4];for(int y=0;y<128;y++)for(int x=0;x<192;x++){int i=(y*192+x)*4;pixels[i]=(byte)(x<96?90:220);pixels[i+1]=(byte)(y<64?170:80);pixels[i+2]=(byte)((x+y)%256);pixels[i+3]=255;}var image=BitmapSource.Create(192,128,96,96,PixelFormats.Bgra32,null,pixels,192*4);image.Freeze();return image;}
 static void Seed(){
  Need(!Directory.Exists(Fixture)&&!File.Exists(Fixture),"Fresh generated nondefault fixture root required.");
  string pinsPath=Path.Combine(Data,"capture","pins"),canvasPath=Path.Combine(Data,"free-canvas");
  if(Directory.Exists(pinsPath)){Need(!Directory.EnumerateDirectories(pinsPath).Any(),"No existing nested pin data may be adopted.");foreach(string file in Directory.GetFiles(pinsPath)){NoLinks(file);Need((Path.GetFileName(file)=="pins.json"||Path.GetFileName(file)=="pins.json.bak")&&new FileInfo(file).Length<8192&&Json.Deserialize<object[]>(File.ReadAllText(file)).Length==0,"Only empty official pin indexes may preexist.");}}
  Need(!Directory.Exists(canvasPath)||!Directory.EnumerateFileSystemEntries(canvasPath).Any(),"No prior canvas is overwritten.");
  string settingsPath=Path.Combine(Data,"settings.json");if(File.Exists(settingsPath)){var settings=Read(settingsPath);object library;Need(!settings.TryGetValue("LibraryPath",out library)||string.IsNullOrEmpty(Text(library))||Text(library)==Path.Combine(Data,"Library"),"Only a fresh default-library installation is seeded.");}
  Directory.CreateDirectory(Fixture);Save(Path.Combine(Fixture,"fixture-owner.json"),new{purpose="generated-public42-upgrade-fixture",oldAppSha256=OldHash,newAppSha256=NewHash,createdUtc=DateTime.UtcNow.ToString("O")});
  string source=Path.Combine(Fixture,"source.png");var bitmap=Image();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=new FileStream(source,FileMode.CreateNew)){encoder.Save(output);output.Flush(true);}
  string libraryRoot=Path.Combine(Fixture,"Library"),conversion=Path.Combine(Fixture,"Converted");Directory.CreateDirectory(conversion);
  string folderId,assetId;object libraryModel=Create("Mori.Library",libraryRoot);try{folderId=Text(Call(libraryModel,"CreateFolder","升级保留测试",""));object imported=Call(libraryModel,"Import",source,folderId,"公开版升级测试图片","https://example.invalid/upgrade42to45","upgrade-fixture","Generated VM image; source must be preserved",null,"reuse",false);assetId=Text(Get(Field(imported,"Item"),"Id"));Need(!Convert.ToBoolean(Field(imported,"Duplicate"))&&!string.IsNullOrEmpty(assetId),"Old installed model imports one new generated image.");}finally{((IDisposable)libraryModel).Dispose();}
  object legacy=Create("MoriDesk.FreeCanvasStore",Data),project=Call(legacy,"Create","升级保留测试画布");string projectId=Text(Get(project,"Id"));object item=Call(legacy,"Import",projectId,source,CancellationToken.None);((IList)Get(project,"Items")).Add(item);Call(legacy,"Save",project);object mature=Create("MoriDesk.MatureCanvasStore",Data),document=Call(mature,"CreateFromLegacy",project);Need(Convert.ToInt32(Get(document,"ImageCount"))==1,"Old installed model saves one real canvas image.");
  object pins=Create("QingJie.PinStore",pinsPath),pin=Call(pins,"Add",bitmap);pin.GetType().GetField("Active").SetValue(pin,false);pin.GetType().GetField("Left").SetValue(pin,170d);pin.GetType().GetField("Top").SetValue(pin,130d);pin.GetType().GetField("Width").SetValue(pin,192d);pin.GetType().GetField("FlipHorizontal").SetValue(pin,true);Call(pins,"Save");
  object preferences=Product.GetType("Mori.Settings",true).GetMethod("Load",BindingFlags.Static|BindingFlags.Public).Invoke(null,null);Set(preferences,"LibraryPath",libraryRoot);Set(preferences,"FileConversionOutputPath",conversion);Set(preferences,"Theme","light");Set(preferences,"ThumbnailSize",192);Set(preferences,"CloseToTray",false);Call(preferences,"SaveTo",settingsPath);
  Save(Path.Combine(Fixture,"identities.json"),new{assetId,folderId,projectId,pinId=Text(Field(pin,"Id")),sourceHash=Hash(source)});
 }
 static object Snapshot(){
  var owner=Read(Path.Combine(Fixture,"fixture-owner.json"));Need(Text(owner["oldAppSha256"])==OldHash&&Text(owner["newAppSha256"])==NewHash,"Generated fixture ownership matches this upgrade.");var ids=Read(Path.Combine(Fixture,"identities.json"));
  var raw=Read(Path.Combine(Data,"settings.json"));string[] settingsKeys={"LibraryPath","FileConversionOutputPath","Theme","ThumbnailSize","CloseToTray"};Need(settingsKeys.All(raw.ContainsKey),"Persisted fixture settings present.");var settings=settingsKeys.ToDictionary(key=>key,key=>raw[key]);
  Need(Text(settings["LibraryPath"])==Path.Combine(Fixture,"Library")&&Text(settings["FileConversionOutputPath"])==Path.Combine(Fixture,"Converted"),"Configured paths retain the generated nondefault locations.");
  object assetSnapshot,foldersSnapshot,members;string imported;object library=Create("Mori.Library",Path.Combine(Fixture,"Library"));try{var assets=Items(Call(library,"All"));Need(assets.Length==1&&Text(Get(assets[0],"Id"))==Text(ids["assetId"]),"Exactly the original generated asset survives.");assetSnapshot=Properties(assets[0],"Id Name Relative Ext Tags Notes Source Added Created Bytes Favorite Deleted ReferencePath ReferenceSection IsDirectory LocalCollection AddedRecovered HiddenFromAll");imported=Text(Call(library,"FullPath",assets[0]));foldersSnapshot=Items(Call(library,"Folders")).OrderBy(value=>Text(Get(value,"Id"))).Select(value=>Properties(value,"Id Name Parent Scope")).ToArray();members=Items(Call(library,"AssetFolders",Text(ids["assetId"]))).Select(value=>Text(Get(value,"Id"))).OrderBy(value=>value).ToArray();}finally{((IDisposable)library).Dispose();}
  object pins=Create("QingJie.PinStore",Path.Combine(Data,"capture","pins"));var records=Items(Field(pins,"Records"));Need(records.Length==1&&Text(Field(records[0],"Id"))==Text(ids["pinId"]),"Exactly the original generated pin survives.");var pin=records[0].GetType().GetFields(BindingFlags.Public|BindingFlags.Instance).OrderBy(field=>field.Name).ToDictionary(field=>field.Name,field=>field.GetValue(records[0]));string pinImage=Text(Call(pins,"ImagePath",records[0]));
  object store=Create("MoriDesk.MatureCanvasStore",Data),document=Call(store,"Load",Text(ids["projectId"]));Need(Convert.ToInt32(Get(document,"ImageCount"))==1,"Actual installed model reopens the original one-image canvas.");var resources=Items(Get(document,"Resources")).ToDictionary(resource=>Text(Get(resource,"FileId")),resource=>Hash(Text(Call(store,"ResourcePath",document,resource))));
  var canvas=Properties(document,"Version Engine EngineVersion Id Name Updated Pinned SceneJson ElementCount ImageCount TextCount");
  string input=Path.Combine(Fixture,"source.png");Need(Hash(input)==Text(ids["sourceHash"])&&Hash(imported)==Text(ids["sourceHash"]),"Generated source and imported original bytes survive.");
  return new{settings,asset=assetSnapshot,folders=foldersSnapshot,members,pin,canvas,resources,originalHash=Hash(input),importedHash=Hash(imported),pinImageHash=Hash(pinImage)};
 }
 [STAThread]static int Main(string[] args){string operation=args.Length==1?args[0]:"";try{Need(new[]{"--seed42","--snapshot42","--verify45"}.Contains(operation),"Explicit fixture stage required.");Guest(operation=="--verify45"?NewHash:OldHash);if(operation=="--seed42")Seed();else if(operation=="--snapshot42")Save(Path.Combine(Fixture,"baseline42.json"),Snapshot());else{string before=Json.Serialize(Read(Path.Combine(Fixture,"baseline42.json"))),after=Json.Serialize(Snapshot());Need(before==after,"All fixture user fields, settings, membership, scene, pin state and original/resource hashes survive public 42 to final 45.");}
   Save(Path.Combine(Package,"fixture"+operation.Substring(2)+"-result.json"),new{passed=true,operation,appSha256=Hash(App),checks=Checks,fixture=Fixture,hostExecuted=false,actualInstalledModel=true});return 0;
  }catch(Exception error){if(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;if(Package!=null)try{Save(Path.Combine(Package,"fixture"+operation.TrimStart('-')+"-result.json"),new{passed=false,operation,error=error.ToString(),checks=Checks});}catch{}Console.Error.WriteLine(error);return 1;}}
}
