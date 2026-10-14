using System;
using System.Diagnostics;
using System.IO;

namespace MoriDeskInstaller {
 internal static class InstallerSafetyTests {
  static int checks;
  static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
  public static int Main(string[] args){
   try{
    foreach(string product in new[]{"Mori Desk","mori desk","MORI DESK","Nook","nook","NOOK"}){
     Check(Program.MatchesRunningProcess(20,1,product,"Mori.exe",10,1),"same-session legacy or new product blocks installation: "+product);
     Check(Program.MatchesRunningProcess(20,1,product,"mori.exe",10,1),"stable original-filename comparison is case insensitive: "+product);
     Check(!Program.MatchesRunningProcess(10,1,product,"Mori.exe",10,1),"installer never blocks on itself: "+product);
     Check(!Program.MatchesRunningProcess(20,2,product,"Mori.exe",10,1),"another Windows session is not changed or blocked: "+product);
    }
    foreach(string product in new[]{"Mori Desk","Nook"})foreach(string filename in new[]{null,"","Nook.exe","NookApp.exe","Mori-Desk.exe","MoriDesk.exe","Mori.exe.old","Mori.exe "," Mori.exe","C:\\Other App\\Mori.exe"})
     Check(!Program.MatchesRunningProcess(20,1,product,filename,10,1),"same-named unrelated product or unknown original identity does not block installation");
    foreach(string product in new[]{null,"","Mori","MoriDesk","Luma","QingJie","Mori.exe","Mori Desk Helper","Mori Desk Installer","Not Mori Desk","Nook Helper","Nook Installer","Nook Setup","Nook.exe","Not Nook","Nook Desktop"," Nook","Nook ","Mori Desk "})
     Check(!Program.MatchesRunningProcess(20,1,product,"Mori.exe",10,1),"unrelated, unknown or approximate product does not block installation");
    // Synthetic absolute paths only: ManagedTarget never inspects a process,
    // reads a shortcut, or creates/removes any file or directory.
    string programs=@"C:\Nook-installer-path-fixture\Programs",programRoot=Path.Combine(programs,"MoriDesk");
    string managed=Path.Combine(programRoot,"0.1.19-20261014000000","Mori.exe");
    foreach(string product in new[]{"Mori Desk","Nook"}){
     Check(Program.ManagedTarget(managed,programRoot,product,"Mori.exe"),"controlled installation target accepts old or new product: "+product);
     Check(Program.ManagedTarget(managed,programRoot+"\\",product,"Mori.exe"),"controlled root trailing separator is accepted: "+product);
     Check(Program.ManagedTarget(managed.ToUpperInvariant(),programRoot.ToLowerInvariant(),product.ToLowerInvariant(),"mori.exe"),"managed target path and stable metadata are case insensitive: "+product);
     Check(Program.ManagedTarget(Path.Combine(programRoot,"unused","..","0.1.19-20261014000000","Mori.exe"),programRoot,product,"Mori.exe"),"normalization may remain safely inside the controlled root: "+product);
     foreach(string target in new[]{null,""," ",programRoot,Path.Combine(programRoot,"0.1.19-20261014000000","Nook.exe"),Path.Combine(programRoot,"0.1.19-20261014000000","Mori.exe.old"),Path.Combine(programs,"MoriDeskOther","0.1.19","Mori.exe"),Path.Combine(programs,"MoriDesk-backup","Mori.exe"),Path.Combine(programs,"Other App","Mori.exe"),Path.Combine(programRoot,"..","Other App","Mori.exe"),Path.Combine(programRoot,"version","..","..","Other App","Mori.exe"),@"C:\Elsewhere\Mori.exe"})
      Check(!Program.ManagedTarget(target,programRoot,product,"Mori.exe"),"outside, adjacent, escaping or incorrectly named shortcut target is not owned");
     foreach(string original in new[]{null,"","Nook.exe","MoriDesk.exe","Mori.exe.old"})
      Check(!Program.ManagedTarget(managed,programRoot,product,original),"managed path alone cannot establish the original product identity");
    }
    foreach(string product in new[]{null,"","Mori","Luma","QingJie","Nook Helper","Nook Installer","Other Nook","Nook "})
     Check(!Program.ManagedTarget(managed,programRoot,product,"Mori.exe"),"managed path and filename cannot claim an unrelated product");
    Check(!Program.ManagedTarget(managed,null,"Nook","Mori.exe"),"unknown controlled root is rejected without throwing");
    Check(!Program.ManagedTarget(managed,"","Nook","Mori.exe"),"empty controlled root is rejected without throwing");
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
    if(args.Length==1){var metadata=FileVersionInfo.GetVersionInfo(Path.GetFullPath(args[0]));Check(Program.MatchesRunningProcess(20,1,metadata.ProductName,metadata.OriginalFilename,10,1),"real suite assembly metadata matches the guard");}
    Console.WriteLine("PASS "+checks+" installer safety checks; no process inspection, registry writes or install performed.");return 0;
   }catch(Exception error){Console.Error.WriteLine("FAIL after "+checks+" checks: "+error.Message);return 1;}
  }
 }
}
