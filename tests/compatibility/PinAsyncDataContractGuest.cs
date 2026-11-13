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
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("Nook asynchronous PinStore adjacent-version proof - VM TEST ONLY")]
// Guard and four-process roundtrip adapted from PinDataContractGuest.cs.
// Both selected implementations use the composite translation-cache key.
// A draft candidate baseline is not evidence of a published-release upgrade.
// Never invoke product Main, show a window, invoke OCR/translation, or read real user data.
internal static class PinAsyncDataContractGuest {
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=2*1024*1024,RecursionLimit=30};
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
 static readonly List<string> Checks=new List<string>();
 static readonly BindingFlags Instance=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
 static string Package,Root; static Assembly Product; static bool GuestVerified;
 static Dictionary<string,object> Manifest;
 static void Need(bool condition,string message){if(!condition)throw new InvalidDataException(message);Checks.Add(message);}
 static string Text(object value){return Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);}
 static void NoLinks(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked test path refused: "+p);}
 static string Hash(string path){NoLinks(path);using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
 static string Safe(string relative){string path=Path.GetFullPath(Path.Combine(Root,relative));if(!path.StartsWith(Root+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Fixture escape refused");NoLinks(path);return path;}
 static void NewJson(string path,object value){NoLinks(path);using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,Utf8)){writer.Write(Json.Serialize(value));writer.Flush();file.Flush(true);}}
 static Dictionary<string,object> Read(string path){NoLinks(path);Need(new FileInfo(path).Length<2*1024*1024,"Bounded fixture JSON");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));}
 static object[] Items(object value){return ((IEnumerable)value).Cast<object>().ToArray();}
 static void Guest(){
  bool found=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in query.Get())using(row)found|=Regex.IsMatch(Text(row["Manufacturer"])+" "+Text(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
  Need(found&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated QEMU NookTester guest required before any write");GuestVerified=true;
  Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z"),"Hash-addressed guest package required");NoLinks(Package);
  Manifest=Read(Path.Combine(Package,"pin-async-contract-manifest.json"));Need(Text(Manifest["baselineRole"])=="earlier 0.1.44 draft candidate; not a published-release upgrade", "Baseline is explicitly identified as a draft implementation");var files=Items(Manifest["files"]);Need(files.Length>0&&files.Length<30,"Bounded sealed payload list");
  foreach(var raw in files){var item=(Dictionary<string,object>)raw;string rel=Text(item["path"]);Need(!Path.IsPathRooted(rel)&&!rel.Split('/','\\').Contains(".."),"Relative sealed payload path");string path=Path.GetFullPath(Path.Combine(Package,rel));Need(path.StartsWith(Package+"\\",StringComparison.OrdinalIgnoreCase)&&Hash(path)==Text(item["sha256"]),"Sealed payload verified: "+rel);}
 }
 static object Get(object value,string name){var field=value.GetType().GetField(name,Instance);if(field!=null)return field.GetValue(value);return value.GetType().GetProperty(name,Instance).GetValue(value,null);}
 static void Set(object value,string name,object update){value.GetType().GetField(name,Instance).SetValue(value,update);}
 static object Call(object target,string name,params object[] args){var methods=target.GetType().GetMethods(Instance).Where(m=>m.Name==name&&m.GetParameters().Length==args.Length&&!m.IsGenericMethod).ToArray();Need(methods.Length==1,"Unique model API: "+name);return methods[0].Invoke(target,args);}
 static object New(string type,params object[] args){return Activator.CreateInstance(Product.GetType(type,true),args);}
 static object[] Records(object store){return Items(Get(store,"Records"));}
 static string PinState(object store,bool includeCache){return Json.Serialize(Records(store).Select(record=>new SortedDictionary<string,object>(record.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance).Where(f=>includeCache||(f.Name!="TranslationPolicy"&&f.Name!="TranslationProvider"&&f.Name!="ShowTranslation")).ToDictionary(f=>f.Name,f=>f.GetValue(record)),StringComparer.Ordinal)).ToArray());}
 static SortedDictionary<string,string> Images(){var result=new SortedDictionary<string,string>(StringComparer.Ordinal);foreach(string path in Directory.GetFiles(Safe("pins"),"*.png"))result[Path.GetFileName(path)]=Hash(path);return result;}
 static void OnlyCacheChanged(SortedDictionary<string,string> before,string allowed,string label){var after=Images();Need(before.Keys.SequenceEqual(after.Keys),label+": no original/cache file added or removed");foreach(var pair in before)if(pair.Key!=allowed)Need(after[pair.Key]==pair.Value,label+": unrelated PNG retained byte-for-byte: "+pair.Key);Need(before[allowed]!=after[allowed],label+": intended replacement has new pixel bytes");}
 static Dictionary<string,object> Snapshot(object store){return new Dictionary<string,object>{{"records",PinState(store,true)},{"durable",PinState(store,false)},{"images",Images()}};}
 static void Same(object a,object b,string message){Need(Json.Serialize(a)==Json.Serialize(b),message);}
 static BitmapSource Picture(byte red){byte[] pixels=new byte[32*24*4];for(int n=0;n<pixels.Length;n+=4){pixels[n]=47;pixels[n+1]=119;pixels[n+2]=red;pixels[n+3]=255;}var image=BitmapSource.Create(32,24,96,96,PixelFormats.Bgra32,null,pixels,128);image.Freeze();return image;}
 static BitmapSource Load(string path){var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();return image;}
 static string Pixels(BitmapSource image){var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);byte[] pixels=new byte[converted.PixelWidth*converted.PixelHeight*4];converted.CopyPixels(pixels,converted.PixelWidth*4,0);using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(pixels));}
 static void Display(object store,object record,bool cacheLoaded,bool showingCache,string label){
  var before=Snapshot(store);var original=Load(Text(Call(store,"ImagePath",record)));Window window=null;
  try{
   window=(Window)New("QingJie.PinWindow",store,record,original,null);
   Need(!window.IsVisible&&new WindowInteropHelper(window).Handle==IntPtr.Zero,label+": constructor never shows native window");
   Need((Get(window,"translated")!=null)==cacheLoaded,label+": actual cache acceptance matches policy");
   var expected=showingCache?Load(Text(Call(store,"TranslationPath",record))):original;
   var displayed=(BitmapSource)Get(window,"displayed");Need(Pixels(displayed)==Pixels(expected),label+": displayed pixels select correct original or translated image");
   int turns=(int)Get(record,"QuarterTurns");Need(displayed.PixelWidth==(turns%2==0?expected.PixelWidth:expected.PixelHeight)&&displayed.PixelHeight==(turns%2==0?expected.PixelHeight:expected.PixelWidth),label+": orientation retains expected dimensions");
  }finally{if(window!=null)Call(window,"Discard");}
  Same(before,Snapshot(store),label+": display/cache inspection preserves all fields and PNG bytes");
 }
 static string Policy(){return Text(Product.GetType("QingJie.ImageTranslation",true).GetField("LayoutPolicy",BindingFlags.Public|BindingFlags.Static).GetRawConstantValue());}
 static string Context(){return Text(Product.GetType("QingJie.Translation",true).GetField("ImageContextVersion",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).GetRawConstantValue());}
 static string CompositePolicy(){return Policy()+":"+Context();}
 static void DisplayExpected(object store,object record,string label){
  bool accepted=(bool)Get(record,"IsTranslation")&&Text(Get(record,"TranslationPolicy"))==CompositePolicy()&&Text(Get(record,"TranslationProvider"))=="tencent";
  Display(store,record,accepted,accepted&&(bool)Get(record,"ShowTranslation"),label);
 }
 static BitmapSource ExpectedPrepared; static int Presentations;
 static void PresentWithoutWindow(object record,Int32Rect bounds,BitmapSource prepared){
  Need(ReferenceEquals(prepared,ExpectedPrepared)&&prepared.IsFrozen,"Async creator presents the exact frozen input after committing bytes");Presentations++;
 }
 static void AddAsynchronously(object store){
  ExpectedPrepared=Picture(85);Presentations=0;
  var method=store.GetType().GetMethods(Instance).Single(m=>m.Name=="CreatePinAsync"&&m.GetParameters().Length==5);
  var presenter=Delegate.CreateDelegate(method.GetParameters()[3].ParameterType,typeof(PinAsyncDataContractGuest).GetMethod("PresentWithoutWindow",BindingFlags.NonPublic|BindingFlags.Static));
  var task=(System.Threading.Tasks.Task)method.Invoke(store,new object[]{ExpectedPrepared,new Int32Rect(-300,45,32,24),System.Threading.CancellationToken.None,presenter,null});task.GetAwaiter().GetResult();
  var added=Records(store).Last();Need(Presentations==1&&Records(store).Length==5,"Candidate asynchronous creation commits exactly one new pin");
  string path=Text(Call(store,"ImagePath",added));Need(Pixels(Load(path))==Pixels(ExpectedPrepared),"Async committed PNG contains the exact prepared pixels");
  Set(added,"Left",-300.0);Set(added,"Top",45.0);Set(added,"Width",144.0);Set(added,"Opacity",.8);Call(store,"Save");
  NewJson(Safe("async-added.json"),new{id=Get(added,"Id"),originalSha256=Hash(path)});ExpectedPrepared=null;
 }
 static void OriginalSeedPreserved(object store,string phase){
  var seeded=(string)Read(Safe("seed-state.json"))["durable"];var rows=Records(store).Take(4).Select(record=>new SortedDictionary<string,object>(record.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance).Where(f=>f.Name!="TranslationPolicy"&&f.Name!="TranslationProvider"&&f.Name!="ShowTranslation").ToDictionary(f=>f.Name,f=>f.GetValue(record)),StringComparer.Ordinal)).ToArray();
  Need(Json.Serialize(rows)==seeded,phase+": all four prior pin identities and durable fields remain unchanged");
 }
 static void Seed(){
  var store=New("QingJie.PinStore",Safe("pins"));
  for(int i=0;i<4;i++){var pin=Call(store,"Add",Picture((byte)(30+i*20)));Set(pin,"Active",i!=2);Set(pin,"Left",-400.0+i*100);Set(pin,"Top",80.0+i*40);Set(pin,"Width",150.0+i*25);Set(pin,"Opacity",.6+i*.1);Set(pin,"QuarterTurns",i);Set(pin,"FlipHorizontal",i==1);Set(pin,"FlipVertical",i==2);Set(pin,"ClosedAt",i==2?123456789L:0L);if(i>0){Set(pin,"IsTranslation",true);Call(store,"SaveTranslation",pin,Picture((byte)(160+i*20)),"tencent");Need(Text(Get(pin,"TranslationPolicy"))==CompositePolicy(),"Earlier candidate saves its expected composite cache key");if(i==3)Set(pin,"ShowTranslation",false);}}
  Call(store,"Save");var reopened=New("QingJie.PinStore",Safe("pins"));Same(Snapshot(store),Snapshot(reopened),"Earlier candidate save/reopen preserves all records and original/cache bytes");
  var records=Records(reopened);Display(reopened,records[0],false,false,"earlier plain pin");DisplayExpected(reopened,records[1],"earlier valid translated cache");DisplayExpected(reopened,records[3],"earlier original view preference");
  NewJson(Safe("seed-state.json"),Snapshot(reopened));NewJson(Safe("earlier-policy.json"),new{policy=CompositePolicy()});
 }
 static void Transition(string phase){
  string previous=phase=="forward"?"seed":phase=="rollback"?"forward":"rollback";var store=New("QingJie.PinStore",Safe("pins"));Same(Read(Safe(previous+"-state.json")),Snapshot(store),phase+": actual version reads previous fields and every PNG without loss");
  var records=Records(store);Display(store,records[0],false,false,phase+": plain pin");
  if(phase=="forward"){
   DisplayExpected(store,records[1],"candidate reads earlier cached translation according to its known policy");DisplayExpected(store,records[2],"candidate preserves inactive translation history");DisplayExpected(store,records[3],"candidate retains original-view preference");
   string durable=PinState(store,false),original=Hash(Text(Call(store,"ImagePath",records[1])));var beforeImages=Images();Call(store,"SaveTranslation",records[1],Picture(230),"tencent");OnlyCacheChanged(beforeImages,Path.GetFileName(Text(Call(store,"TranslationPath",records[1]))),"Candidate translation replacement");Need(Text(Get(records[1],"TranslationPolicy"))==Policy()+":"+Context(),"Candidate writes layout plus translation-context cache key");Need(PinState(store,false)==durable&&Hash(Text(Call(store,"ImagePath",records[1])))==original,"Candidate translation replacement preserves geometry/state and original bytes");Display(store,records[1],true,true,"candidate accepts freshly saved cache");
   AddAsynchronously(store);
  }else if(phase=="rollback"){
   DisplayExpected(store,records[1],"earlier candidate reads newer cached translation by policy");DisplayExpected(store,records[2],"earlier candidate accepts its untouched cached history");
   string before=PinState(store,true);Call(store,"Save");Need(PinState(store,true)==before,"Old save retains new policy string and every record field");
   string durable=PinState(store,false),original=Hash(Text(Call(store,"ImagePath",records[2])));var beforeImages=Images();Call(store,"SaveTranslation",records[2],Picture(240),"tencent");OnlyCacheChanged(beforeImages,Path.GetFileName(Text(Call(store,"TranslationPath",records[2]))),"Earlier translation replacement");Need(Text(Get(records[2],"TranslationPolicy"))==Text(Read(Safe("earlier-policy.json"))["policy"]),"Earlier retranslation writes its composite key without rejecting new records");Need(PinState(store,false)==durable&&Hash(Text(Call(store,"ImagePath",records[2])))==original,"Earlier translation replacement preserves geometry/Active/ClosedAt/original");
   var added=records.Single(record=>Text(Get(record,"Id"))==Text(Read(Safe("async-added.json"))["id"]));Display(store,added,false,false,"earlier candidate reads new asynchronous pin");
   Need(Hash(Text(Call(store,"ImagePath",added)))==Text(Read(Safe("async-added.json"))["originalSha256"]),"Rollback keeps asynchronous original bytes intact");
   Set(added,"Left",211.0);Set(added,"Width",333.0);Set(added,"Active",false);Set(added,"ClosedAt",987654321L);Set(added,"FlipHorizontal",true);Call(store,"Save");
  }else{
   DisplayExpected(store,records[1],"reupgrade accepts intact candidate cache after earlier save");DisplayExpected(store,records[2],"reupgrade reads freshly written earlier cache by policy");DisplayExpected(store,records[3],"reupgrade preserves original-view preference");
   var added=records.Single(record=>Text(Get(record,"Id"))==Text(Read(Safe("async-added.json"))["id"]));Need((double)Get(added,"Left")==211&&(double)Get(added,"Width")==333&&!(bool)Get(added,"Active")&&(long)Get(added,"ClosedAt")==987654321L&&(bool)Get(added,"FlipHorizontal"),"Candidate retains all edits made to its async pin by the earlier implementation");
   Need(Hash(Text(Call(store,"ImagePath",added)))==Text(Read(Safe("async-added.json"))["originalSha256"]),"Reupgrade preserves asynchronous original bytes after earlier edits");Display(store,added,false,false,"reupgrade reads edited asynchronous pin");
  }
  var expected=Snapshot(store);Call(store,"Save");var reopened=New("QingJie.PinStore",Safe("pins"));Same(expected,Snapshot(reopened),phase+": final real save/reopen preserves all fields and images");Need(Records(reopened).Length==5&&Images().Count==8,phase+": all five records, five originals and three translation files retained");Need(Records(reopened).Select(r=>Text(Get(r,"Id"))).Distinct().Count()==5,phase+": stable unique pin identities");
  OriginalSeedPreserved(reopened,phase);NewJson(Safe(phase+"-state.json"),Snapshot(reopened));
 }
 static int Worker(string phase,string run){
  Need(new[]{"seed","forward","rollback","reupgrade"}.Contains(phase)&&Regex.IsMatch(run,"^[a-f0-9]{32}$"),"Known phase and unique fixture ID required");Root=@"C:\NookLab\Audit\pin-async-contract-"+run;NoLinks(Root);Need(Directory.Exists(Root)&&Text(Read(Safe("owner.json"))["package"])==Package,"Fixture owned by sealed package");
  string stage=phase=="seed"||phase=="rollback"?"old":"candidate",directory=Path.Combine(Package,stage),exe=Path.Combine(directory,"Mori.exe");Need(Hash(exe)==Text(Manifest[stage+"AppSha256"]),"Exact selected product hash");Need(FileVersionInfo.GetVersionInfo(exe).FileVersion==Text(Manifest[stage+"Version"]),"Exact selected product file version");
  Environment.SetEnvironmentVariable("MORI_DESK_DATA",Safe("isolated-runtime"));Directory.CreateDirectory(Safe("isolated-runtime"));
  AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string path=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};Product=Assembly.LoadFrom(exe);
  // Set this before AppState initializes Preferences.Load; no real profile read.
  Product.GetType("QingJie.Preferences",true).GetField("TestFilePath",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,Safe("preferences.json"));
  var settings=New("QingJie.Preferences");Set(settings,"TranslationProvider","tencent");var state=Product.GetType("QingJie.AppState",true);state.GetField("Settings").SetValue(null,settings);state.GetField("Quitting").SetValue(null,true);
  try{if(phase=="seed")Seed();else Transition(phase);NewJson(Safe(phase+"-result.json"),new{passed=true,phase,productSha256=Hash(exe),productVersion=FileVersionInfo.GetVersionInfo(exe).FileVersion,checks=Checks,uiShown=false,networkOrOcrCalled=false});return 0;}
  catch(Exception error){if(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;NewJson(Safe(phase+"-result.json"),new{passed=false,phase,error=error.ToString(),checks=Checks});Console.WriteLine(error);return 1;}
 }
 static int Run(){
  string run=Guid.NewGuid().ToString("N");Root=@"C:\NookLab\Audit\pin-async-contract-"+run;NoLinks(Root);Need(!Directory.Exists(Root),"Fresh isolated synthetic fixture root");Directory.CreateDirectory(Root);NewJson(Safe("owner.json"),new{package=Package,run,syntheticOnly=true});var phases=new List<object>();bool passed=true;
  foreach(string phase in new[]{"seed","forward","rollback","reupgrade"}){
   var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--phase "+phase+" "+run){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Package,RedirectStandardOutput=true,RedirectStandardError=true};
   using(var process=Process.Start(start)){var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(45000)){process.Kill();process.WaitForExit();passed=false;}File.WriteAllText(Safe(phase+"-stdout.txt"),stdout.GetAwaiter().GetResult(),Utf8);File.WriteAllText(Safe(phase+"-stderr.txt"),stderr.GetAwaiter().GetResult(),Utf8);passed&=process.ExitCode==0;}
   string report=Safe(phase+"-result.json");if(File.Exists(report))phases.Add(Read(report));else{passed=false;phases.Add(new{passed=false,phase,error="No phase report",stdout=File.ReadAllText(Safe(phase+"-stdout.txt")),stderr=File.ReadAllText(Safe(phase+"-stderr.txt"))});}if(!passed)break;
  }
  NewJson(Path.Combine(Package,"pin-async-contract-result.json"),new{passed,scope="Real earlier-candidate/new-candidate/earlier-candidate/new-candidate PinStore roundtrip including asynchronous pin creation; no native window shown",baselineRole=Manifest["baselineRole"],fixture=Root,oldAppSha256=Manifest["oldAppSha256"],candidateAppSha256=Manifest["candidateAppSha256"],phases,hostExecuted=false,installedApplicationTouched=false});Console.WriteLine(passed?"PASS pin rollback":"FAIL pin rollback");return passed?0:1;
 }
 [STAThread]static int Main(string[] args){try{Guest();if(args.Length==1&&args[0]=="--run")return Run();if(args.Length==3&&args[0]=="--phase")return Worker(args[1],args[2]);throw new InvalidDataException("Explicit VM command required");}catch(Exception error){Console.WriteLine(error);if(GuestVerified&&args.Length==1&&args[0]=="--run"&&Package!=null&&Regex.IsMatch(Package,@"\AC:\\NookLab\\Audit\\apps\\[a-fA-F0-9]{12}\z")){try{string report=Path.Combine(Package,"pin-async-contract-result.json");if(!File.Exists(report))NewJson(report,new{passed=false,error=error.ToString(),checks=Checks});}catch{}}return 1;}}
}
