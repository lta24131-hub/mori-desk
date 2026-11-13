using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// VM-only bounded thread lease. Does not change the power plan or inject input.
internal static class DisplayLeaseGuest {
 [DllImport("kernel32.dll", SetLastError=true)] static extern uint SetThreadExecutionState(uint flags);
 static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
 static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
 static void Need(bool ok, string why) { if (!ok) throw new InvalidDataException(why); }
 static void Plain(string path) { for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p)) if ((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked path refused"); }
 static void Save(string path, object value) { Plain(path); using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) using(var writer=new StreamWriter(file,Utf8)) { writer.Write(Json.Serialize(value));writer.Flush();file.Flush(true); } }
 static int Main(string[] args) {
  bool acquired=false; string package=null,releaseReason="deadline"; var clock=Stopwatch.StartNew();
  try {
   Need(args.Length==1&&(args[0]=="--bounded-upgrade-resume-lease-3600s"||args[0]=="--release-own-lease"), "Fixed bounded lease or explicit own-package release only");
   bool vm=false;using(var query=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem")) foreach(ManagementObject row in query.Get()) using(row) vm|=Regex.IsMatch(Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);
   Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated QEMU NookTester required");
   package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Regex.IsMatch(package,@"\AC:\\NookLab\\Audit\\apps\\[A-F0-9]{12}\z"),"Hash-addressed audit directory only");Plain(package);
   string marker=Path.Combine(package,"display-resume-lease-release.request.json");Plain(marker);
   if(args[0]=="--release-own-lease") {
    Need(File.Exists(Path.Combine(package,"display-resume-lease-started.json")),"Own-package lease has started");
    Need(!File.Exists(Path.Combine(package,"display-resume-lease-released.json")),"Own-package lease has not already released");
    Save(marker,new {vmGuard=true,userGuard=true,package=package,requesterPid=Process.GetCurrentProcess().Id,releaseOwnPackageOnly=true,utc=DateTime.UtcNow.ToString("o")});
    return 0;
   }
   Need(!File.Exists(marker),"Release marker must not predate the one-shot lease");
   Need(!File.Exists(Path.Combine(package,"display-resume-lease-started.json")),"One lease per immutable package");
   Need(SetThreadExecutionState(0x80000003)!=0,"Acquire thread-scoped system/display lease");acquired=true;
   Save(Path.Combine(package,"display-resume-lease-started.json"),new {vmGuard=true,userGuard=true,pid=Process.GetCurrentProcess().Id,maxSeconds=3600,globalPowerPlanChanged=false,productExecuted=false,utc=DateTime.UtcNow.ToString("o")});
   while(clock.Elapsed.TotalSeconds<3600) { if(File.Exists(marker)) { Plain(marker);releaseReason="explicit-own-package-request";break; } Thread.Sleep(200); }
   return 0;
  } catch(Exception error) { Console.WriteLine(error);return 1; }
  finally { if(acquired) { uint released=SetThreadExecutionState(0x80000000);try { Save(Path.Combine(package,"display-resume-lease-released.json"),new {released=released!=0,reason=releaseReason,elapsedSeconds=clock.Elapsed.TotalSeconds,utc=DateTime.UtcNow.ToString("o")}); } catch {} } }
 }
}
