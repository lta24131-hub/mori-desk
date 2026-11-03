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
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("Nook 0.1.33 to 0.1.35 data compatibility - VM TEST ONLY")]
[assembly: AssemblyVersion("0.0.0.1")]

// Model APIs only; never invoke product Main, create windows, register shell
// handlers, read a user profile library, or install either application.
internal static class DataContract34Guest {
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=8*1024*1024,RecursionLimit=50};
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static readonly List<string> Checks=new List<string>();
 static string Package,Root,Phase; static Assembly Product; static bool GuestVerified;
 static Dictionary<string,object> Manifest;
 static void Need(bool condition,string message){if(!condition)throw new InvalidDataException(message);Checks.Add(message);}
 static void NoLinks(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked test path refused: "+p);}
 static string Hash(string path){NoLinks(path);using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
 static string Safe(string relative){string path=Path.GetFullPath(Path.Combine(Root,relative));if(!path.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Fixture escape refused");NoLinks(path);return path;}
 static void NewJson(string path,object value){NoLinks(path);using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,Utf8)){writer.Write(Json.Serialize(value));writer.Flush();file.Flush(true);}}
 static Dictionary<string,object> Read(string path){NoLinks(path);Need(new FileInfo(path).Length<8*1024*1024,"Bounded fixture JSON");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));}
 static string Text(object value){return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
 static string Quote(string value){return "\""+value.Replace("\"","\"\"")+"\"";}
 static void Guest(){
  bool found=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)found|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(found&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated QEMU NookTester guest required before any write");
  GuestVerified=true;
  Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
  Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed guest package required");NoLinks(Package);
  Manifest=Read(Path.Combine(Package,"compatibility-manifest.json"));
  object rawFiles;Need(Manifest.TryGetValue("files",out rawFiles)&&rawFiles is IEnumerable&&!(rawFiles is string),"Sealed payload file list is enumerable");
  var files=Items(rawFiles);Need(files.Length>0&&files.Length<256,"Bounded nonempty sealed payload file list");
  foreach(object raw in files){var item=raw as Dictionary<string,object>;Need(item!=null&&item.ContainsKey("path")&&item.ContainsKey("sha256"),"Sealed payload entry shape");string rel=Text(item["path"]);Need(!Path.IsPathRooted(rel)&&!rel.Split('/','\\').Contains(".."),"Relative sealed payload path");string path=Path.GetFullPath(Path.Combine(Package,rel));Need(path.StartsWith(Package+"\\",StringComparison.OrdinalIgnoreCase)&&Hash(path)==Text(item["sha256"]),"Sealed payload verified: "+rel);}
 }
 static object Call(object target,string name,params object[] args){var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;var methods=target.GetType().GetMethods(flags).Where(m=>m.Name==name&&m.GetParameters().Length==args.Length&&!m.IsGenericMethod).ToArray();Need(methods.Length==1,"Unique model API: "+name);return methods[0].Invoke(target,args);}
 static object Static(string type,string name,params object[] args){var methods=Product.GetType(type,true).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Where(m=>m.Name==name&&m.GetParameters().Length==args.Length&&!m.IsGenericMethod).ToArray();Need(methods.Length==1,"Unique static model API: "+name);return methods[0].Invoke(null,args);}
 static object New(string type,params object[] args){return Activator.CreateInstance(Product.GetType(type,true),args);}
 static object Get(object value,string key){var p=value.GetType().GetProperty(key);return p!=null?p.GetValue(value,null):value.GetType().GetField(key).GetValue(value);}
 static void Set(object value,string key,object update){var p=value.GetType().GetProperty(key);if(p!=null)p.SetValue(value,update,null);else value.GetType().GetField(key).SetValue(value,update);}
 static object[] Items(object value){return ((IEnumerable)value).Cast<object>().ToArray();}
 static Dictionary<string,string>[] Query(object library,string sql){return (Dictionary<string,string>[])Items(Call(Get(library,"Db"),"Query",sql)).Cast<Dictionary<string,string>>().ToArray();}
 static string CanonicalRows(IEnumerable<Dictionary<string,string>> rows,bool sort){var values=rows.Select(row=>Json.Serialize(new SortedDictionary<string,string>(row,StringComparer.Ordinal))).ToArray();if(sort)Array.Sort(values,StringComparer.Ordinal);return string.Join("\n",values);}
 static SortedDictionary<string,object> DatabaseState(object library){
  Need(Query(library,"PRAGMA integrity_check").Single().Values.Single()=="ok","SQLite integrity check passes");Need(Query(library,"PRAGMA foreign_key_check").Length==0,"SQLite foreign keys remain valid");
  var state=new SortedDictionary<string,object>(StringComparer.Ordinal);state["schema"]=CanonicalRows(Query(library,"SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name"),false);
  foreach(var row in Query(library,"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")){
   string table=row["name"];var columns=Query(library,"PRAGMA table_info("+Quote(table)+")");Need(columns.Length>0&&columns.Length<128,"Bounded fixture table columns");state[table+".columns"]=CanonicalRows(columns,false);
   string projection=string.Join(",",columns.Select((c,i)=>"typeof("+Quote(c["name"])+") AS t"+i+",hex(CAST("+Quote(c["name"])+" AS BLOB)) AS v"+i));
   var rows=Query(library,"SELECT "+projection+" FROM "+Quote(table));Need(rows.Length<128,"Bounded synthetic table rows");state[table+".rows"]=CanonicalRows(rows,true);
  }return state;
 }
 static SortedDictionary<string,string> MediaState(object library){var state=new SortedDictionary<string,string>(StringComparer.Ordinal);foreach(var row in Query(library,"SELECT path FROM assets ORDER BY path")){string relative=row["path"];string full=Path.GetFullPath(Path.Combine(Text(Get(library,"Root")),relative));Need(full.StartsWith(Text(Get(library,"Root"))+"\\",StringComparison.OrdinalIgnoreCase),"Managed original stays within fixture library");state[relative]=Hash(full);}return state;}
 static string PinState(object pins){return Json.Serialize(Items(Get(pins,"Records")).Select(record=>new SortedDictionary<string,object>(record.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance).ToDictionary(f=>f.Name,f=>f.GetValue(record)),StringComparer.Ordinal)).ToArray());}
 static SortedDictionary<string,string> PinImages(){var state=new SortedDictionary<string,string>(StringComparer.Ordinal);foreach(string path in Directory.GetFiles(Safe("pins"),"*.png"))state[Path.GetFileName(path)]=Hash(path);return state;}
 static object State(object library,object pins){return new{database=DatabaseState(library),originals=MediaState(library),pins=PinState(pins),pinImages=PinImages(),sourceA=Hash(Safe("sources\\source-a.png")),sourceB=Hash(Safe("sources\\source-b.png"))};}
 static void Same(object expected,object actual,string label){Need(Json.Serialize(expected)==Json.Serialize(actual),label);}
 static BitmapSource Picture(byte red){byte[] pixels=new byte[32*24*4];for(int n=0;n<pixels.Length;n+=4){pixels[n]=47;pixels[n+1]=119;pixels[n+2]=red;pixels[n+3]=255;}var image=BitmapSource.Create(32,24,96,96,PixelFormats.Bgra32,null,pixels,128);image.Freeze();return image;}
 static void PictureFile(string path,BitmapSource image){var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){png.Save(file);file.Flush(true);}}
 static object Import(object library,string source,string folder,string name){return Get(Call(library,"Import",source,folder,name,"https://example.invalid/compat34","compat, synthetic","seed note","2026-10-01T00:00:00.0000000Z","reuse",false),"Item");}
 static object OpenLibrary(string suffix){return New("Mori.Library",Safe(suffix));}
 static object Preferences(){return Static("Mori.Settings","LoadFrom",Safe("settings.json"));}
 static void Routes(object preferences){Need(Text(Get(preferences,"LibraryPath"))==Safe("nondefault\\library")&&Text(Get(preferences,"FileConversionOutputPath"))==Safe("nondefault\\conversion-results"),"Both nondefault data routes are preserved");}
 static object[] Assets(object library){return Items(Call(library,"All"));}
 static void Seed(){
  Directory.CreateDirectory(Safe("sources"));Directory.CreateDirectory(Safe("nondefault\\conversion-results"));PictureFile(Safe("sources\\source-a.png"),Picture(31));PictureFile(Safe("sources\\source-b.png"),Picture(213));
  object library=OpenLibrary("nondefault\\library"),pins=New("QingJie.PinStore",Safe("pins"));
  try{
   string folder=Text(Call(library,"CreateFolder","Old version category",""));var asset=Import(library,Safe("sources\\source-a.png"),folder,"Old version image");Set(asset,"Favorite",true);Call(library,"Update",asset);
   for(int n=0;n<3;n++){var pin=Call(pins,"Add",Picture((byte)(40+n*70)));Set(pin,"Active",n!=2);Set(pin,"Left",-400.0+n*100);Set(pin,"Top",90.0+n*40);Set(pin,"Width",180.0+n*30);Set(pin,"Opacity",.65+n*.1);Set(pin,"QuarterTurns",n);Set(pin,"FlipHorizontal",n==1);Set(pin,"FlipVertical",n==2);Set(pin,"ClosedAt",n==2?123456789L:0L);}Call(pins,"Save");
   var preferences=New("Mori.Settings");Set(preferences,"LibraryPath",Safe("nondefault\\library"));Set(preferences,"FileConversionOutputPath",Safe("nondefault\\conversion-results"));Set(preferences,"Theme","light");Set(preferences,"ThumbnailSize",192);Set(preferences,"CanvasSnap",true);Call(preferences,"SaveTo",Safe("settings.json"));Routes(Preferences());
   Need(!Read(Safe("settings.json")).ContainsKey("IncludePreviewUpdates"),"Old release writes no new preview-update preference");
   NewJson(Safe("seed-state.json"),State(library,pins));NewJson(Safe("seed-settings.json"),Read(Safe("settings.json")));
  }finally{((IDisposable)library).Dispose();}
 }
 static void Forward(){
  object library=OpenLibrary("nondefault\\library"),pins=New("QingJie.PinStore",Safe("pins"));try{
   Same(Read(Safe("seed-state.json")),State(library,pins),"New release reads every old database value, original and pin field unchanged");
   var preferences=Preferences();Routes(preferences);Need((bool)Get(preferences,"IncludePreviewUpdates"),"Missing optional preference defaults to true");Set(preferences,"IncludePreviewUpdates",false);Call(preferences,"SaveTo",Safe("settings.json"));
   var expected=Read(Safe("seed-settings.json"));expected["IncludePreviewUpdates"]=false;Same(new SortedDictionary<string,object>(expected),new SortedDictionary<string,object>(Read(Safe("settings.json"))),"Adding update preference preserves every prior setting");
   var first=Assets(library).Single();Set(first,"Notes","saved by candidate 0.1.35");Call(library,"Update",first);string folder=Text(Call(library,"CreateFolder","New version category",""));Import(library,Safe("sources\\source-b.png"),folder,"New version image");
   var pin=Items(Get(pins,"Records"))[0];Set(pin,"Width",321.0);Set(pin,"QuarterTurns",3);Set(pin,"FlipVertical",true);Call(pins,"Save");
   NewJson(Safe("forward-state.json"),State(library,pins));NewJson(Safe("forward-settings.json"),Read(Safe("settings.json")));
   string backup=Safe("candidate-backup.mori.zip");File.WriteAllText(backup+".partial","unrelated interrupted archive; retain");string sentinel=Hash(backup+".partial");Call(library,"Backup",backup);Need(Hash(backup+".partial")==sentinel,"New backup preserves unrelated prior partial archive");Need(!Directory.GetFiles(Root,".nook-backup-*.partial").Any(),"New backup leaves no owned staging file");
   NewJson(Safe("backup-state.json"),new{database=DatabaseState(library),originals=MediaState(library),sha256=Hash(backup)});
  }finally{((IDisposable)library).Dispose();}
 }
 static void Rollback(){
  object library=OpenLibrary("nondefault\\library"),pins=New("QingJie.PinStore",Safe("pins"));try{
   Same(Read(Safe("forward-state.json")),State(library,pins),"Old release reads all candidate library and pin data without loss");
   var preferences=Preferences();Routes(preferences);Set(preferences,"Theme","dark");Call(preferences,"SaveTo",Safe("settings.json"));var expected=Read(Safe("forward-settings.json"));expected.Remove("IncludePreviewUpdates");expected["Theme"]="dark";
   Same(new SortedDictionary<string,object>(expected),new SortedDictionary<string,object>(Read(Safe("settings.json"))),"Old preference save preserves all original keys and drops only the accepted new optional key");
   var backup=Read(Safe("backup-state.json"));Need(Hash(Safe("candidate-backup.mori.zip"))==Text(backup["sha256"]),"Candidate backup unchanged before old-version restore");Static("Mori.Library","Restore",Safe("candidate-backup.mori.zip"),Safe("restored-by-old"));var restored=OpenLibrary("restored-by-old");try{Same(backup["database"],DatabaseState(restored),"Old Restore preserves all candidate backup table schema and values");Same(backup["originals"],MediaState(restored),"Old Restore preserves every candidate original SHA-256");}finally{((IDisposable)restored).Dispose();}
   var asset=Assets(library).Single(a=>Text(Get(a,"Name"))=="New version image");Set(asset,"Notes","edited after rollback 0.1.33");Set(asset,"Favorite",true);Call(library,"Update",asset);var pin=Items(Get(pins,"Records"))[1];Set(pin,"Width",444.0);Set(pin,"Active",false);Set(pin,"ClosedAt",987654321L);Call(pins,"Save");
   NewJson(Safe("rollback-state.json"),State(library,pins));NewJson(Safe("rollback-settings.json"),Read(Safe("settings.json")));
  }finally{((IDisposable)library).Dispose();}
 }
 static void Reupgrade(){
  object library=OpenLibrary("nondefault\\library"),pins=New("QingJie.PinStore",Safe("pins"));try{
   Same(Read(Safe("rollback-state.json")),State(library,pins),"Re-upgrade preserves all old-version edits, original bytes and pin fields");var preferences=Preferences();Routes(preferences);Need((bool)Get(preferences,"IncludePreviewUpdates"),"Accepted limitation: old-version save resets the optional preview-update choice to default true");Call(preferences,"SaveTo",Safe("settings.json"));var expected=Read(Safe("rollback-settings.json"));expected["IncludePreviewUpdates"]=true;Same(new SortedDictionary<string,object>(expected),new SortedDictionary<string,object>(Read(Safe("settings.json"))),"Re-upgrade changes no settings except restoring the optional default key");
   Need(Assets(library).Length==2&&Items(Call(library,"Folders")).Length==2,"Both versions' images and categories remain present");Need(Items(Get(pins,"Records")).Length==3&&Items(Get(pins,"Records")).Count(p=>(bool)Get(p,"Active"))==1,"One active pin and two independently closed history records survive");
   Call(pins,"Save");Same(Read(Safe("rollback-state.json")),State(library,New("QingJie.PinStore",Safe("pins"))),"Final pin save and reload preserve every field and source hash");
  }finally{((IDisposable)library).Dispose();}
 }
 static Dictionary<string,object> Map(params object[] values){var result=new Dictionary<string,object>();for(int n=0;n<values.Length;n+=2)result.Add((string)values[n],values[n+1]);return result;}
 static Dictionary<string,object> Obj(object value){return (Dictionary<string,object>)value;}
 static Dictionary<string,object> CanvasRecords(Dictionary<string,object> scene){return Obj(Obj(scene["document"])["store"]);}
 static object CanvasState(object store,object doc){var hashes=new SortedDictionary<string,string>();foreach(var resource in Items(Get(doc,"Resources")))hashes[Text(Get(resource,"FileId"))]=Hash(Text(Call(store,"ResourcePath",doc,resource)));return new{id=Get(doc,"Id"),name=Get(doc,"Name"),scene=Get(doc,"SceneJson"),count=Get(doc,"ImageCount"),resources=Json.Serialize(Get(doc,"Resources")),hashes};}
 static void CanvasImage(object store,object doc,string label,string source,bool sdk){
  var resource=Call(store,"RegisterImage",doc,source,label);var scene=Obj(Json.DeserializeObject(Text(Get(doc,"SceneJson"))));var records=CanvasRecords(scene);
  var props=Map("w",32,"h",24,"name",Path.GetFileName(source),"mimeType","image/png","src",Static("MoriDesk.MatureCanvasStore","ResourceUrl",Get(doc,"Id"),Get(resource,"FileId")),"isAnimated",false);if(sdk)props["fileSize"]=new FileInfo(source).Length;
  records["asset:"+label]=Map("id","asset:"+label,"typeName","asset","type","image","meta",Map(),"props",props);
  records["shape:"+label]=Map("id","shape:"+label,"typeName","shape","type","image","x",17,"y",29,"rotation",0,"index",sdk?"a2":"a1","parentId","page:page","isLocked",false,"opacity",1,"meta",Map(),"props",Map("w",160,"h",120,"assetId","asset:"+label,"playing",true,"url","","crop",null,"flipX",false,"flipY",false));
  Set(doc,"SceneJson",Json.Serialize(scene));Call(store,"Save",doc);
 }
 static void CanvasPhase(string phase){
  var store=New("MoriDesk.MatureCanvasStore",Safe("canvas-data"));object doc;
  if(phase=="seed"){
   doc=Call(store,"Create","Old canvas");CanvasImage(store,doc,"old",Safe("sources\\source-a.png"),false);
   NewJson(Safe("canvas-id.json"),new{id=Get(doc,"Id")});NewJson(Safe("canvas-seed.json"),CanvasState(store,doc));Need((int)Get(doc,"ImageCount")==1,"Old release created a real image canvas");return;
  }
  doc=Call(store,"Load",Text(Read(Safe("canvas-id.json"))["id"]));
  string previous=phase=="forward"?"seed":phase=="rollback"?"forward":"rollback";
  Same(Read(Safe("canvas-"+previous+".json")),CanvasState(store,doc),phase+": canvas scene, resource identities and image bytes survive version change");
  if(phase=="forward"){
   CanvasImage(store,doc,"sdk",Safe("sources\\source-b.png"),true);Need(!Text(Get(doc,"SceneJson")).Contains("fileSize")&&(int)Get(doc,"ImageCount")==2,"SDK image metadata is accepted but omitted from the durable backward-compatible snapshot");
   NewJson(Safe("canvas-forward.json"),CanvasState(store,doc));
  }else if(phase=="rollback"){
   var scene=Obj(Json.DeserializeObject(Text(Get(doc,"SceneJson"))));Obj(CanvasRecords(scene)["shape:old"])["x"]=333;Set(doc,"SceneJson",Json.Serialize(scene));Set(doc,"Name","Edited after rollback");Call(store,"Save",doc);
   var copy=Call(store,"CopyProject",doc,"Old-version copy");Need((int)Get(copy,"ImageCount")==2,"Old release can copy the new SDK image canvas without dropping either resource");
   NewJson(Safe("canvas-copy.json"),CanvasState(store,copy));NewJson(Safe("canvas-rollback.json"),CanvasState(store,doc));
  }else{
   var copyState=Read(Safe("canvas-copy.json"));var copy=Call(store,"Load",Text(copyState["id"]));Same(copyState,CanvasState(store,copy),"Re-upgrade preserves the old-version copy and each copied resource hash");
   Call(store,"Save",doc);Same(Read(Safe("canvas-rollback.json")),CanvasState(store,Call(store,"Load",Get(doc,"Id"))),"Final save/reopen retains old-version canvas edits and both images");
  }
 }
 static int Worker(string phase,string run){
  Need(Regex.IsMatch(run,"^[a-f0-9]{32}$"),"Unique fixture ID required");Root=@"C:\NookLab\Audit\compat34-"+run;NoLinks(Root);Need(Directory.Exists(Root)&&Text(Read(Safe("owner.json"))["package"])==Package,"Fixture ownership must match sealed package");
  Phase=phase;string stage=phase=="seed"||phase=="rollback"?"old":"candidate";Need(new[]{"seed","forward","rollback","reupgrade"}.Contains(phase),"Known compatibility phase");
  string executable=Path.Combine(Package,stage,"Mori.exe"),version=stage=="old"?"0.1.33.0":"0.1.35.0";Need(FileVersionInfo.GetVersionInfo(executable).FileVersion==version,"Expected real product version "+version);
  string prefix=Path.Combine(Package,stage);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string file=Path.Combine(prefix,new AssemblyName(e.Name).Name+".dll");return File.Exists(file)?Assembly.LoadFrom(file):null;};
  Environment.SetEnvironmentVariable("MORI_DESK_DATA",Safe("isolated-runtime"));Directory.CreateDirectory(Safe("isolated-runtime"));Product=Assembly.LoadFrom(executable);
  Need(Product.GetName().Version.ToString()==version,"Loaded assembly identity matches selected version");
  try{if(phase=="seed")Seed();else if(phase=="forward")Forward();else if(phase=="rollback")Rollback();else Reupgrade();CanvasPhase(phase);NewJson(Safe(phase+"-result.json"),new{passed=true,phase,productVersion=version,productSha256=Hash(executable),checks=Checks,fixture=Root,uiLaunched=false});return 0;}
  catch(Exception error){if(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;NewJson(Safe(phase+"-result.json"),new{passed=false,phase,error=error.ToString(),checks=Checks,fixture=Root});Console.WriteLine(error);return 1;}
 }
 static int Run(){
  string run=Guid.NewGuid().ToString("N");Root=@"C:\NookLab\Audit\compat34-"+run;NoLinks(Root);Need(!Directory.Exists(Root)&&!File.Exists(Root),"Fresh unique fixture root");Directory.CreateDirectory(Root);NewJson(Safe("owner.json"),new{package=Package,run,syntheticOnly=true});
  var results=new List<object>();bool passed=true;string self=Assembly.GetExecutingAssembly().Location;
  foreach(string phase in new[]{"seed","forward","rollback","reupgrade"}){
   var start=new ProcessStartInfo(self,"--phase "+phase+" "+run){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Package,RedirectStandardOutput=true,RedirectStandardError=true};start.EnvironmentVariables["MORI_DESK_DATA"]=Safe("isolated-runtime");start.EnvironmentVariables["TEMP"]=Safe("temp");start.EnvironmentVariables["TMP"]=Safe("temp");Directory.CreateDirectory(Safe("temp"));
   using(var process=Process.Start(start)){var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(90000)){process.Kill();process.WaitForExit();passed=false;}File.WriteAllText(Safe(phase+"-stdout.txt"),stdout.GetAwaiter().GetResult(),Utf8);File.WriteAllText(Safe(phase+"-stderr.txt"),stderr.GetAwaiter().GetResult(),Utf8);passed&=process.ExitCode==0;}
   string report=Safe(phase+"-result.json");if(File.Exists(report))results.Add(Read(report));else{passed=false;results.Add(new{passed=false,phase,error="No phase report",fixture=Root,stdout=BoundedLog(Safe(phase+"-stdout.txt")),stderr=BoundedLog(Safe(phase+"-stderr.txt"))});}if(!passed)break;
  }
  NewJson(Path.Combine(Package,"audit-data-contract34.json"),new{passed,scope="VM-only real 0.1.33 -> 0.1.35 -> 0.1.33 -> 0.1.35 model data roundtrip",fixture=Root,hostExecuted=false,installedApplicationTouched=false,uiLaunched=false,acceptedRollbackLimitation="IncludePreviewUpdates is dropped only by old preference save, then defaults true on re-upgrade; all original keys and data paths must preserve",phases=results});Console.WriteLine((passed?"PASS":"FAIL")+" compatibility; "+Root);return passed?0:1;
 }
 static string BoundedLog(string path){if(!File.Exists(path))return "missing";string value=File.ReadAllText(path,Utf8);return value.Length<=12000?value:value.Substring(0,12000)+" [truncated]";}
 static void StartupFailure(string[] args,Exception error){
  // Even failure reporting is forbidden on a host or an arbitrary directory.
  // A worker never claims the aggregate path; its owner captures stdout and
  // includes it in the failed phase if preflight happened before its report.
  if(!GuestVerified||args.Length!=1||args[0]!="--run"||string.IsNullOrEmpty(Package)||!Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"))return;
  try{NoLinks(Package);string report=Path.Combine(Package,"audit-data-contract34.json");if(File.Exists(report))return;NewJson(report,new{passed=false,scope="VM-only compatibility startup/coordination failure",stage="entry",error=error.ToString(),checks=Checks,package=Package,fixture=Root,hostExecuted=false,installedApplicationTouched=false,uiLaunched=false});}catch(Exception reportError){Console.WriteLine("Guarded failure report unavailable: "+reportError.Message);}
 }
 [STAThread]static int Main(string[] args){try{Guest();if(args.Length==1&&args[0]=="--run")return Run();if(args.Length==3&&args[0]=="--phase")return Worker(args[1],args[2]);throw new InvalidDataException("Explicit VM compatibility command required");}catch(Exception error){Console.WriteLine(error);StartupFailure(args,error);return 1;}}
}
