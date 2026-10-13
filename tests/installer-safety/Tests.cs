using System;
using System.Diagnostics;
using System.IO;

namespace MoriDeskInstaller {
 internal static class InstallerSafetyTests {
  static int checks;
  static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
  public static int Main(string[] args){
   try{
    Check(Program.MatchesRunningProcess(20,1,"Mori Desk",10,1),"same-session Mori Desk blocks installation");
    Check(Program.MatchesRunningProcess(20,1,"mori desk",10,1),"product-name comparison is case insensitive");
    Check(!Program.MatchesRunningProcess(10,1,"Mori Desk",10,1),"installer never blocks on itself");
    Check(!Program.MatchesRunningProcess(20,2,"Mori Desk",10,1),"another Windows session is not changed or blocked");
    foreach(string name in new[]{null,"","Mori","Luma","QingJie","Mori.exe","Mori Desk Helper","Mori Desk Installer","Not Mori Desk"})Check(!Program.MatchesRunningProcess(20,1,name,10,1),"unrelated or unknown product does not block installation");
    string root=Path.Combine(Path.GetTempPath(),"MoriDesk-installer-pure-"+Guid.NewGuid().ToString("N"));
    string previousTemp=Environment.GetEnvironmentVariable("TEMP"),previousTmp=Environment.GetEnvironmentVariable("TMP"),previousData=Environment.GetEnvironmentVariable("MORI_DESK_DATA");
    string exe=Path.Combine(root,"Mori.exe");var start=Program.VerificationStart(exe,root);
    Check(start.FileName==exe&&start.Arguments=="--suite-test","verification starts only the extracted suite test");
    Check(!start.UseShellExecute&&start.CreateNoWindow,"verification is a hidden child with explicit environment");
    Check(start.EnvironmentVariables["TEMP"]==Path.Combine(root,"runtime-temp"),"verification isolates TEMP");
    Check(start.EnvironmentVariables["TMP"]==start.EnvironmentVariables["TEMP"],"verification isolates TMP too");
    Check(start.EnvironmentVariables["MORI_DESK_DATA"]==Path.Combine(root,"test-data","MoriDesk"),"verification isolates personal data separately from program payload");
    Check(Environment.GetEnvironmentVariable("TEMP")==previousTemp&&Environment.GetEnvironmentVariable("TMP")==previousTmp&&Environment.GetEnvironmentVariable("MORI_DESK_DATA")==previousData,"verification child configuration does not alter the parent process environment");
    Check(!Directory.Exists(root),"pure guard/configuration tests do not create directories or mutate personal data");
    if(args.Length==1){string product=FileVersionInfo.GetVersionInfo(Path.GetFullPath(args[0])).ProductName;Check(Program.MatchesRunningProcess(20,1,product,10,1),"real suite assembly metadata matches the guard");}
    Console.WriteLine("PASS "+checks+" installer safety checks; no process inspection, registry writes or install performed.");return 0;
   }catch(Exception error){Console.Error.WriteLine("FAIL after "+checks+" checks: "+error.Message);return 1;}
  }
 }
}
