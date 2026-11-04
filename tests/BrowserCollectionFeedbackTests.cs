using System;
using System.Collections.Generic;
using System.IO;

namespace MoriDesk {
 internal static class BrowserCollectionFeedbackTests {
  public static void Run(string root,Action<bool,string> check){
   string launcher=Path.Combine(root,"Launcher","Nook.exe");
   var start=BrowserCollectionFeedback.StartInfo(launcher);
   check(start.FileName==launcher&&start.Arguments==""&&!start.UseShellExecute,"open library uses only the resolved entry, no page arguments or shell");
   check(start.WorkingDirectory==Path.GetDirectoryName(launcher),"open entry retains its own working directory");
   check(!start.EnvironmentVariables.ContainsKey("NOOK_UPDATE_REQUEST")&&!start.EnvironmentVariables.ContainsKey("NOOK_UPDATE_EXPECTED_VERSION"),"open library cannot accidentally resume an installer candidate");
   string previous=Environment.GetEnvironmentVariable("MORI_DESK_DATA");
   try{
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"feedback-isolated","MoriDesk"));
   check(!BrowserCollectionFeedback.CanOpen,"isolated tests never advertise opening the user's installed Nook");
   bool loaded=false,rejected=false;string untouched=Path.Combine(root,"feedback-no-library");
   try{Mori.NativeBridge.HandleAtCurrentLibrary(new Dictionary<string,object>{{"action","openLibrary"},{"_extensionId",new string('a',32)}},untouched,()=>{loaded=true;throw new Exception("unexpected settings load");});}
   catch(InvalidOperationException){rejected=true;}
   check(rejected&&!loaded&&!Directory.Exists(untouched),"isolated open is refused before settings, library initialization or process launch");
   rejected=false;try{BrowserCollectionFeedback.Open("https://example.invalid");}catch(ArgumentException){rejected=true;}
   check(rejected,"untrusted caller identity cannot open the application");
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",previous);}
  }
 }
}
