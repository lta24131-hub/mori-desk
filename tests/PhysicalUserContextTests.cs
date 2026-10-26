using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MoriDesk {
 internal static class PhysicalUserContextTests {
  internal static void Run(string root,Action<bool,string> check){
   string previousData=Environment.GetEnvironmentVariable("MORI_DESK_DATA");
   try{
    foreach(string isolated in new[]{Path.Combine(root,"isolated-install")," "}){
     Environment.SetEnvironmentVariable("MORI_DESK_DATA",isolated);bool blocked=false;
     try{PhysicalUserContext.RequireInstalledContext();}catch(InvalidOperationException){blocked=true;}
     check(blocked&&Environment.GetEnvironmentVariable("MORI_DESK_DATA")==isolated,"installed update rejects every non-empty data override without silently changing it");
    }
    check(!Directory.Exists(Path.Combine(root,"isolated-install")),"rejected installed context does not initialize an alternate data directory");
   }finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",previousData);}
   Directory.CreateDirectory(root);string settings=Path.Combine(root,"settings.json");File.WriteAllText(settings,"existing settings fixture",Encoding.UTF8);byte[] before=File.ReadAllBytes(settings);
   string expected=Path.Combine(root,"file.json");
   check(PhysicalUserContext.SamePhysicalFile(expected,@"\\?\"+expected),"physical handle accepts Windows extended path");
   check(PhysicalUserContext.SamePhysicalFile(expected,expected.ToUpperInvariant()),"physical handle comparison follows Windows case semantics");
   check(!PhysicalUserContext.SamePhysicalFile(expected,Path.Combine(root,"Packages","LocalCache","file.json")),"NO_PACKAGE alone cannot authorize a redirected file");
   check(!PhysicalUserContext.SamePhysicalFile(expected,expected+".other"),"similar path is not proof of physical identity");
   check(!PhysicalUserContext.SamePhysicalFile(expected,@"\\?\UNC\server\share\file.json")&&!PhysicalUserContext.SamePhysicalFile(expected,null),"unavailable or network observation fails closed");
   bool rejected=false;try{PhysicalUserContext.Probe(root,file=>Path.Combine(root,"LocalCache",Path.GetFileName(file.Name)));}catch(InvalidOperationException ex){rejected=ex.Message==PhysicalUserContext.FailureMessage;}
   check(rejected&&Directory.GetFiles(root,".nook-physical-*.tmp").Length==0,"redirected file blocks work and cleans its probe");
   rejected=false;try{PhysicalUserContext.Probe(root,file=>{throw new IOException("probe unavailable");});}catch(IOException){rejected=true;}
   check(rejected&&Directory.GetFiles(root,".nook-physical-*.tmp").Length==0,"failed kernel observation leaves no probe and no success");
   PhysicalUserContext.Probe(root,file=>@"\\?\"+file.Name);
   check(Directory.GetFiles(root,".nook-physical-*.tmp").Length==0&&File.ReadAllBytes(settings).SequenceEqual(before),"successful probe preserves existing settings and removes marker");
   // This path is an isolated fixture. Exercise the real Windows file handle,
   // not only the synthetic redirected observation above.
   PhysicalUserContext.RequireRoot(root);
   check(File.ReadAllBytes(settings).SequenceEqual(before)&&Directory.GetFiles(root).Length==1,"real kernel probe leaves only pre-existing fixture content");
  }
 }
}
