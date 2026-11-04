using System;
using System.Collections.Generic;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.CSharp;

namespace MoriDesk {
 // Generated fixtures only. The caller supplies an isolated test root; this
 // suite never discovers user data or invokes the production startup queue.
 internal static class UpdateArtifactMaintenanceTests {
  static readonly Version Current=new Version(0,1,37);
  static readonly DateTime Now=new DateTime(2026,11,4,0,0,0,DateTimeKind.Utc);
  static readonly byte[] Payload=Encoding.UTF8.GetBytes("synthetic installer bytes: never execute");
  static string Hash(byte[] value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(value)).Replace("-","");}
  static string Download(string root,Version version,int days,bool partial=false){string folder=Path.Combine(root,"downloads","update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);File.WriteAllBytes(Path.Combine(folder,"Nook-Setup.exe"+(partial?".partial":"")),Payload);UpdateArtifactMaintenance.RecordDownload(folder,new UpdateRelease{Version=version,Size=Payload.Length,ExpectedSha256=Hash(Payload)},Now.AddDays(-days));return folder;}
  static string Backup(string root,int days,string status="installed",bool sealedFiles=false){
   string session=Path.Combine(root,Guid.NewGuid().ToString("N")),backup=Path.Combine(session,"metadata-backup");Directory.CreateDirectory(backup);
   File.WriteAllBytes(Path.Combine(backup,"library.db"),Payload);
   long bytes=Payload.Length;if(sealedFiles)AppUpdateMetadataBackup.SealFiles(backup,ref bytes);
   File.WriteAllText(Path.Combine(backup,"complete.json"),new JavaScriptSerializer().Serialize(new{schemaVersion=1,completedUtc=Now.AddDays(-days).ToString("O"),metadataOnly=true,sourceMediaIncluded=false,credentialsIncluded=false,bytes=bytes}));
   if(status!=null)File.WriteAllText(Path.Combine(session,"result.json"),new JavaScriptSerializer().Serialize(new{schemaVersion=1,status=status,expectedVersion="0.1.37",message="fixture"}));return session;
  }
  static string Case(string root){string path=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
  static void LegacyInstaller(string directory){
   using(var compiler=new CSharpCodeProvider()){
    var options=new CompilerParameters(new[]{"System.dll"},Path.Combine(directory,"Nook-Setup.exe")){GenerateExecutable=true,GenerateInMemory=false};
    var compiled=compiler.CompileAssemblyFromSource(options,"using System.Reflection; [assembly:AssemblyProduct(\"Nook Installer\")][assembly:AssemblyVersion(\"0.1.36.0\")][assembly:AssemblyFileVersion(\"0.1.36.0\")]class Fixture { static void Main() {} }");
    if(compiled.Errors.HasErrors)throw new Exception("Could not compile inert legacy-installer fixture.");
   }
  }
  static UpdateArtifactResult Clean(string root,Action guard=null,string protect=null){return UpdateArtifactMaintenance.Clean(root,Current,Now,new[]{protect??Path.Combine(root,"real-library")},guard??(()=>{}));}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);
   string r=Case(root),d=Download(r,Current,0);var result=Clean(r);check(!Directory.Exists(d)&&result.downloads==1&&result.bytes>Payload.Length,"installed-version download and receipt removed with freed bytes reported");
   check(Clean(r).downloads==0,"second cleanup is idempotent");
   r=Case(root);d=Download(r,new Version(0,1,38),0);Clean(r);check(File.Exists(Path.Combine(d,"Nook-Setup.exe")),"fresh future update retained");
   r=Case(root);d=Download(r,new Version(0,1,38),8);Clean(r);check(!Directory.Exists(d),"unused future download expires after seven days");
   r=Case(root);d=Download(r,Current,0,true);Clean(r);check(File.Exists(Path.Combine(d,"Nook-Setup.exe.partial")),"recent partial download retained");
   r=Case(root);d=Download(r,Current,8,true);Clean(r);check(!Directory.Exists(d),"stale partial with valid receipt removed");
   r=Case(root);d=Download(r,Current,0);File.WriteAllText(Path.Combine(d,"Nook-Setup.exe"),"changed");result=Clean(r);check(File.Exists(Path.Combine(d,"Nook-Setup.exe"))&&result.retained>0,"modified installer retained");
   r=Case(root);d=Download(r,Current,0);File.WriteAllText(Path.Combine(d,"personal.txt"),"keep");Clean(r);check(Directory.GetFiles(d).Length==3,"personal file vetoes entire folder cleanup");
   r=Case(root);d=Download(r,Current,0);Directory.CreateDirectory(Path.Combine(d,"nested"));Clean(r);check(File.Exists(Path.Combine(d,"Nook-Setup.exe")),"unexpected subdirectory vetoes deletion");
   r=Case(root);d=Download(r,Current,0);using(var held=new FileStream(Path.Combine(d,"Nook-Setup.exe"),FileMode.Open,FileAccess.Read,FileShare.Read)){result=Clean(r);check(result.retry&&File.Exists(Path.Combine(d,"Nook-Setup.exe")),"in-use installer retained with bounded retry requested");}Clean(r);check(!Directory.Exists(d),"released installer is cleaned on retry");
   r=Case(root);d=Download(r,Current,0);Clean(r,null,d);check(File.Exists(Path.Combine(d,"Nook-Setup.exe")),"library route overlapping cache protected");
   r=Case(root);d=Download(r,Current,0);int checks=0;Clean(r,()=>{if(++checks>1)throw new IOException("health changed");});check(File.Exists(Path.Combine(d,"Nook-Setup.exe")),"health gate revalidated immediately before deletion");
   r=Case(root);d=Download(r,Current,0);bool rejected=false;try{Clean(r,()=>{throw new IOException("not ready");});}catch(IOException){rejected=true;}check(rejected&&File.Exists(Path.Combine(d,"Nook-Setup.exe")),"unhealthy startup cannot start cleanup");
   r=Case(root);d=Download(r,Current,0);File.WriteAllText(Path.Combine(d,UpdateArtifactMaintenance.Receipt),"{}");Clean(r);check(File.Exists(Path.Combine(d,"Nook-Setup.exe")),"invalid ownership receipt retained");
   r=Case(root);d=Download(r,Current,-1);Clean(r);check(File.Exists(Path.Combine(d,"Nook-Setup.exe")),"future-dated receipt retained");
   r=Case(root);d=Download(r,Current,0);string renamed=Path.Combine(r,"downloads","user-download");Directory.Move(d,renamed);Clean(r);check(File.Exists(Path.Combine(renamed,"Nook-Setup.exe")),"non-generated download folder untouched");
   r=Case(root);d=Download(r,Current,0);File.Delete(Path.Combine(d,UpdateArtifactMaintenance.Receipt));Clean(r);check(File.Exists(Path.Combine(d,"Nook-Setup.exe")),"unmarked arbitrary executable not adopted as legacy installer");
   r=Case(root);d=Path.Combine(r,"downloads","update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(d);LegacyInstaller(d);result=Clean(r);check(!Directory.Exists(d)&&result.downloads==1,"historical cache without receipt cleaned only after real installer identity verification");
   r=Case(root);d=Path.Combine(r,"manual-download");Directory.CreateDirectory(d);LegacyInstaller(d);Clean(r);check(File.Exists(Path.Combine(d,"Nook-Setup.exe")),"manually downloaded installer outside managed cache preserved");
   r=Case(root);string outside=Case(root),link=Path.Combine(r,"downloads","update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(link));File.WriteAllBytes(Path.Combine(outside,"Nook-Setup.exe"),Payload);
   using(var junction=Process.Start(new ProcessStartInfo("cmd.exe","/d /c mklink /J \""+link+"\" \""+outside+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true})){junction.WaitForExit();if(junction.ExitCode!=0)throw new Exception("Cannot create isolated junction fixture.");}
   result=Clean(r);check(File.Exists(Path.Combine(outside,"Nook-Setup.exe"))&&Directory.Exists(link)&&result.retained>0,"directory junction cannot redirect deletion outside cache");
   r=Case(root);string old=Backup(r,5),latest=Backup(r,1);result=Clean(r);check(!Directory.Exists(Path.Combine(old,"metadata-backup"))&&Directory.Exists(Path.Combine(latest,"metadata-backup"))&&result.backups==1,"only newest successful complete metadata backup retained");
   check(File.Exists(Path.Combine(old,"result.json")),"small transaction record preserved after large backup cleanup");
   check(Clean(r).backups==0,"backup cleanup idempotent");
   r=Case(root);old=Backup(r,5);Clean(r);check(Directory.Exists(Path.Combine(old,"metadata-backup")),"sole valid backup is never removed");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1,"error");Clean(r);check(Directory.Exists(Path.Combine(old,"metadata-backup"))&&Directory.Exists(Path.Combine(latest,"metadata-backup")),"failed update cannot authorize deleting last successful backup");
   r=Case(root);old=Backup(r,5,"error");latest=Backup(r,1);Clean(r);check(Directory.Exists(Path.Combine(old,"metadata-backup")),"backup associated with unresolved failure retained");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1);File.Delete(Path.Combine(latest,"metadata-backup","complete.json"));Clean(r);check(Directory.Exists(Path.Combine(old,"metadata-backup")),"incomplete new backup never replaces valid old backup");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1);File.WriteAllText(Path.Combine(latest,"metadata-backup","library.db"),"truncated");Clean(r);check(Directory.Exists(Path.Combine(old,"metadata-backup")),"invalid new backup size preserves prior valid backup");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1);File.WriteAllText(Path.Combine(old,"metadata-backup","photo.png"),"personal");Clean(r);check(File.Exists(Path.Combine(old,"metadata-backup","library.db"))&&File.Exists(Path.Combine(old,"metadata-backup","photo.png")),"personal content in old backup prevents all removal");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1);using(var held=new FileStream(Path.Combine(latest,"metadata-backup","library.db"),FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite)){Clean(r);check(Directory.Exists(Path.Combine(old,"metadata-backup")),"actively changing keeper does not authorize deletion");}
   r=Case(root);old=Backup(r,5);latest=Backup(r,1);checks=0;Clean(r,()=>{if(++checks>1)throw new IOException("cancelled");});check(File.Exists(Path.Combine(old,UpdateArtifactMaintenance.Journal))&&File.Exists(Path.Combine(old,"metadata-backup","library.db")),"cancellation after durable journal preserves all backup data");Clean(r);check(!Directory.Exists(Path.Combine(old,"metadata-backup")),"journal resumes after cancelled deletion");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1);checks=0;Clean(r,()=>{if(++checks>1)throw new IOException("interrupt");});File.Delete(Path.Combine(old,"metadata-backup","library.db"));Clean(r);check(!Directory.Exists(Path.Combine(old,"metadata-backup")),"interrupted deletion resumes using exact saved hashes");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1);checks=0;Clean(r,()=>{if(++checks>1)throw new IOException("interrupt");});File.WriteAllText(Path.Combine(old,"metadata-backup","library.db"),"edited");Clean(r);check(File.ReadAllText(Path.Combine(old,"metadata-backup","library.db"))=="edited","edited backup is not removed by resumed journal");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1);Clean(r,null,Path.Combine(old,"metadata-backup"));check(Directory.Exists(Path.Combine(old,"metadata-backup")),"protected library path inside backups preserved");
   r=Case(root);old=Backup(r,1);latest=Backup(r,1);Clean(r);check(Directory.Exists(Path.Combine(old,"metadata-backup"))&&Directory.Exists(Path.Combine(latest,"metadata-backup")),"ambiguous equal backup timestamps preserved");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1,"installed",true);Clean(r);check(!Directory.Exists(Path.Combine(old,"metadata-backup"))&&Directory.Exists(Path.Combine(latest,"metadata-backup")),"new integrity-sealed backup supersedes legacy backup");
   r=Case(root);old=Backup(r,5);latest=Backup(r,1,"installed",true);var altered=(byte[])Payload.Clone();altered[0]^=1;File.WriteAllBytes(Path.Combine(latest,"metadata-backup","library.db"),altered);Clean(r);check(Directory.Exists(Path.Combine(old,"metadata-backup")),"same-length corruption in new backup cannot authorize old backup deletion");
   r=Case(root);old=Backup(r,5,"installed",true);latest=Backup(r,1,"installed",true);Clean(r);check(!Directory.Exists(Path.Combine(old,"metadata-backup")),"old sealed backup inventory is removed with its verified payload");
   check(InstalledVersionMaintenance.CanRetry(new InstalledMaintenanceAttempt("complete","no-old-versions"){ArtifactRetry=true}),"artifact sharing conflict gets one existing bounded maintenance retry");
   r=Case(root);UpdateArtifactMaintenance.SaveReport(r,new UpdateArtifactResult{downloads=2,backups=1,bytes=1048576,retained=1});string summary=UpdateArtifactMaintenance.ReadSummary(r);check(summary.Contains("2 个安装包")&&summary.Contains("1.0 MB")&&summary.Contains("暂时保留"),"maintenance summary describes removed and retained artifacts");
  }
 }
}
