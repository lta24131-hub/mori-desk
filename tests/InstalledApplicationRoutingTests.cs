using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace MoriDesk {
 internal static class InstalledApplicationRoutingTests {
  static void Rejected(Action action,Action<bool,string> check,string message){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}catch(IOException){rejected=true;}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}check(rejected,message);}
  static string CopyProgram(string root,string folder){string target=Path.Combine(root,folder,"Mori.exe");Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(typeof(Entry).Assembly.Location,target);return target;}
  static string Read(RegistryKey scope,string name){using(var key=scope.OpenSubKey(OpenWithRegistration.ApplicationKey+name))return key==null?null:key.GetValue("") as string;}
  static void Command(RegistryKey scope,string value){using(var key=scope.CreateSubKey(OpenWithRegistration.ApplicationKey+"\\shell\\open\\command"))key.SetValue("",value);}
  internal static void Run(string fixtures,Action<bool,string> check){
   string previousData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),previousRequest=Environment.GetEnvironmentVariable("NOOK_UPDATE_REQUEST"),previousVersion=Environment.GetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION");
   try{
    string isolated=Path.Combine(fixtures,"isolated-update-data");Environment.SetEnvironmentVariable("MORI_DESK_DATA",isolated);Environment.SetEnvironmentVariable("NOOK_UPDATE_REQUEST",Path.Combine(fixtures,"request.json"));Environment.SetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION","0.1.27");
    Rejected(()=>{int exit;InstalledApplicationRouting.TryRoute(new string[0],out exit);},check,"candidate cannot bypass installed-context validation through alternate-data exemption");
    Rejected(()=>{int exit;InstalledApplicationRouting.TryRoute(new[]{"--self-test"},out exit);},check,"test-mode arguments cannot turn an inherited candidate into a successful installed update");
    Rejected(()=>AppUpdateCoordinator.BeginAsync(null,null,null,null,System.Threading.CancellationToken.None).GetAwaiter().GetResult(),check,"online update rejects alternate data before owner checks or update staging");
    check(!AppUpdateCoordinator.ReportStartupReady(null),"isolated-data candidate cannot publish installed startup success");
    Environment.SetEnvironmentVariable("NOOK_UPDATE_REQUEST",null);Environment.SetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION",null);
    Rejected(()=>InstalledApplicationRouting.RegistrationExecutable(),check,"isolated-data process cannot select a production registration executable");
    Rejected(()=>BrowserConnection.Register("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),check,"isolated-data process cannot write real browser host registration");
    Rejected(()=>BrowserExtensionManager.EnsureInstalled(),check,"isolated-data process cannot install a production managed browser extension");
    Rejected(()=>BrowserExtensionManager.Status(),check,"production extension status cannot initialize an isolated-data managed store");
    Rejected(()=>BrowserExtensionManager.RequestUpdate(),check,"isolated-data process cannot update a production managed browser extension");
    Rejected(()=>OpenWithRegistration.RepairInstalled(typeof(Entry).Assembly.Location),check,"direct Open-with repair refuses an isolated-data process before real registry access");
    Rejected(()=>OpenWithRegistration.RepairStableInstalled(typeof(Entry).Assembly.Location),check,"direct stable Open-with repair refuses an isolated-data process before real registry access");
    var previousNotify=DeskRuntime.Notify;try{DeskRuntime.Notify=null;BrowserConnection.StartLocal();check(BrowserConnection.LocalState=="installation-blocked","isolated-data process cannot take the production fallback port or write a connection status log");}finally{DeskRuntime.Notify=previousNotify;}
    check(Environment.GetEnvironmentVariable("MORI_DESK_DATA")==isolated&&!Directory.Exists(isolated),"blocked installed entry points neither clear alternate data nor initialize a replacement library");
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",previousData);Environment.SetEnvironmentVariable("NOOK_UPDATE_REQUEST",previousRequest);Environment.SetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION",previousVersion);}
   string root=Path.Combine(fixtures,"installed-routing"),executable=Path.Combine(root,"0.1.27-first","Mori.exe"),hash=new string('A',64);
   foreach(string arg in new[]{"--ui-test","--suite-test","--self-test","--native-test","--image-codec","--video-thumbnail","--svg-render","--pdf-text-worker","--pdf-render-worker","--ocr-worker=fixture","--browser-bridge-test","--browser-update-host-test"})check(InstalledApplicationRouting.IsExempt(new[]{arg},null),"explicit fixture or worker is not rerouted: "+arg);
   check(InstalledApplicationRouting.IsExempt(new string[0],"fixture-data"),"alternate-data mode stays isolated from installed shell identity");
   foreach(string arg in new[]{"--library","--startup","--register-extension","chrome-extension://abcdefghijklmnopabcdefghijklmnop/"})check(!InstalledApplicationRouting.IsExempt(new[]{arg},null),"production entry remains subject to committed identity: "+arg);
   check(InstalledApplicationRouting.ManagedPayload(executable,root),"version payload belongs to its exact managed parent");
   foreach(string path in new[]{Path.Combine(root,"Launcher","Mori.exe"),Path.Combine(root,"Mori.exe"),Path.Combine(root,"version","child","Mori.exe"),Path.Combine(root+"Other","version","Mori.exe"),Path.Combine(root,"version","Other.exe")})check(!InstalledApplicationRouting.ManagedPayload(path,root),"launcher, root, nested, adjacent and foreign programs are not payloads");
   string shell=InstalledApplicationRouting.ShellInstanceName(root,"S-1-5-21-123");
   check(shell==InstalledApplicationRouting.ShellInstanceName(root.ToUpperInvariant()+"\\","S-1-5-21-123")&&shell.StartsWith("Global\\Nook.Shell.",StringComparison.Ordinal),"shell identity is case-normalized and spans Windows sessions");
   check(shell!=InstalledApplicationRouting.ShellInstanceName(root,"S-1-5-21-456"),"different Windows users never share shell identity");
   check(Mori.Program.ShellActivationName(shell,"unused").StartsWith("Local\\Nook.Shell.",StringComparison.Ordinal),"activation remains session-local so another user's desktop is not falsely activated");
   check(Mori.Program.ShellActivationName(null,"Local\\fixture")=="Local\\fixture-activate","portable and preview activation retains its independent fixture identity");
   var json=new JavaScriptSerializer();string record=json.Serialize(new{schemaVersion=1,expectedVersion="0.1.27",executable=executable,sha256=hash});
   InstalledApplicationRouting.ValidateCandidateRecord(record,executable,"0.1.27",hash.ToLowerInvariant());check(true,"candidate version, absolute executable and hash are independently verified");
   foreach(string bad in new[]{record.Replace("0.1.27\"","0.1.26\""),record.Replace(hash,new string('B',64)),record.Replace("\"schemaVersion\":1","\"schemaVersion\":2"),record.Replace("\"schemaVersion\":1","\"schemaVersion\":\"1\""),record.Insert(1,"\"unknown\":1,"),record.Insert(1,"\"schemaVersion\":1,"),record.Insert(1,"\"schemaVersion\":2,"),"{}","null"})Rejected(()=>InstalledApplicationRouting.ValidateCandidateRecord(bad,executable,"0.1.27",hash),check,"invalid or duplicate candidate fields cannot authorize service ownership");
   Rejected(()=>InstalledApplicationRouting.ValidateCandidateRecord(record,Path.Combine(root,"other","Mori.exe"),"0.1.27",hash),check,"a different executable cannot reuse another candidate's receipt");
   check(InstalledApplicationRouting.CandidateFailure(false,null,false,119999)==null,"candidate waits without side effects while installer holds commit lease");
   check(InstalledApplicationRouting.CandidateFailure(false,"installed",false,1000)==null,"receipt written before installer releases its lease is not mistaken for failure");
   foreach(string status in new[]{"error","cancelled"})check(InstalledApplicationRouting.CandidateFailure(false,status,false,1)!=null,"terminal "+status+" stops candidate service retry");
   check(InstalledApplicationRouting.CandidateFailure(true,null,false,1)!=null&&InstalledApplicationRouting.CandidateFailure(false,null,false,120000)!=null,"cancelled and abandoned candidates have bounded lifetime");
   check(InstalledApplicationRouting.CandidateFailure(false,"installed",true,1)!=null,"a success receipt alone cannot override the installed identity");
   check(InstalledApplicationRouting.CandidateFailure(false,null,true,1)!=null,"a crashed installer without a result releases its candidate and shell immediately once the install lease is free");
   using(var output=new MemoryStream()){
    InstalledApplicationRouting.WriteNativeFailure(output);byte[] bytes=output.ToArray();int length=BitConverter.ToInt32(bytes,0);
    var error=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(bytes,4,bytes.Length-4));
    check(length==bytes.Length-4&&(bool)error["ok"]==false&&(string)error["errorCode"]=="stale-installation","stale native process emits exactly one framed failure without consuming or replaying a collection request");
   }
   check(InstalledApplicationRouting.RegistrationExecutableInScope(Path.Combine(root,"not-installed"),executable)==executable,"portable first use does not require an installed program tree");
   string current=CopyProgram(root,"0.1.27-first"),laterNamedStale=CopyProgram(root,"0.1.27-zzz"),launcher=CopyProgram(root,"Launcher");
   check(InstalledApplicationRouting.RegistrationExecutableInScope(root,current)==current,"legacy installations retain compatibility before a committed ledger exists");
   var descriptor=InstalledApplicationState.CreateDescriptor(root,current,new string('a',64));InstalledApplicationState.Commit(root,null,descriptor);
   check(InstalledApplicationRouting.RegistrationExecutableInScope(root,current)==launcher,"active browser registration uses only the stable launcher");
   Rejected(()=>InstalledApplicationRouting.RegistrationExecutableInScope(root,laterNamedStale),check,"same-version payload with a later folder name cannot take browser registration");
   string registryPath="Software\\Nook.Tests\\InstalledRouting-"+Guid.NewGuid().ToString("N");
   try{using(var scope=Registry.CurrentUser.CreateSubKey(registryPath)){
    string target="\""+launcher+"\" \"%1\"";Command(scope,"\""+laterNamedStale+"\" \"%1\"");
    check(OpenWithRegistration.RepairInScope(scope,current,root,null)&&Read(scope,"\\shell\\open\\command")==target,"committed payload replaces a later-named stale Open-with route with the fixed launcher");
    check(!OpenWithRegistration.RepairInScope(scope,laterNamedStale,root,null)&&Read(scope,"\\shell\\open\\command")==target,"stale startup cannot undo fixed Windows Open-with registration");
    check(!OpenWithRegistration.RepairInScope(scope,current,root,null),"stable active registration is idempotent");
    string foreign="cmd.exe /c Mori.exe \"%1\"";Command(scope,foreign);
    check(!OpenWithRegistration.RepairInScope(scope,current,root,null)&&Read(scope,"\\shell\\open\\command")==foreign,"unrecognized application ownership remains untouched");
    // A malformed committed ledger is an error, never permission to resurrect
    // the old timestamp-based registration algorithm.
    File.WriteAllText(Path.Combine(root,InstalledApplicationState.FileName),"{broken",new UTF8Encoding(false));
    Rejected(()=>InstalledApplicationRouting.RegistrationExecutableInScope(root,current),check,"invalid active ledger blocks browser registration");
    Rejected(()=>OpenWithRegistration.RepairInScope(scope,current,root,null),check,"invalid active ledger blocks Windows registration");
    check(Read(scope,"\\shell\\open\\command")==foreign,"failed ledger validation leaves previous registry state intact");
   }}finally{
    if(!registryPath.StartsWith("Software\\Nook.Tests\\InstalledRouting-",StringComparison.Ordinal)||registryPath.Length!="Software\\Nook.Tests\\InstalledRouting-".Length+32)throw new InvalidOperationException("Unsafe fixture registry cleanup target");
    Registry.CurrentUser.DeleteSubKeyTree(registryPath);
   }
  }
 }
}
