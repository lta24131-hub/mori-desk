using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// Fixed read-only diagnostic export. No product assembly, process launch,
// registry writes, cleanup API, deletion or native handle manipulation.
internal static class CleanupEvidenceGuest {
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=12*1024*1024,RecursionLimit=40};
 static readonly List<object> Artifacts=new List<object>();
 static string Package;
 const string Session="6ec57c3bede94645935f8e041f6a6dd4";
 const string Current="0.1.45-20261113145904278-57e527ec";
 const string Old="0.1.42-20261113142121298-e4e817b1";
 static void Need(bool value,string reason){if(!value)throw new InvalidDataException(reason);}
 static void Plain(string path){for(string p=Path.GetFullPath(path);!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked path refused: "+p);}
 static string Hash(byte[] bytes){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","");}
 static void New(string path,byte[] bytes){Plain(path);using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){f.Write(bytes,0,bytes.Length);f.Flush(true);}}
 static void Artifact(string name,string source,byte[] bytes){Need(Regex.IsMatch(name,@"\A[a-z0-9][a-z0-9.-]+\z")&&bytes.Length<=8*1024*1024,"Bounded named artifact");string encoded=Convert.ToBase64String(bytes);var parts=new List<string>();for(int offset=0,index=0;offset<encoded.Length;offset+=100000,index++){string part=name+".b64."+index.ToString("D3")+".txt";New(Path.Combine(Package,part),Utf8.GetBytes(encoded.Substring(offset,Math.Min(100000,encoded.Length-offset))));parts.Add(part);}Artifacts.Add(new{name,source,exists=true,bytes=bytes.Length,sha256=Hash(bytes),parts});}
 static void Capture(string name,string path){Plain(path);if(!File.Exists(path)){Artifacts.Add(new{name,source=path,exists=false});return;}using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){Need(f.Length<=8*1024*1024,"Bounded diagnostic source");var bytes=new byte[(int)f.Length];int offset=0;while(offset<bytes.Length){int count=f.Read(bytes,offset,bytes.Length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}Artifact(name,path,bytes);}}
 static void Tree(string root,string folder,List<object> records){Plain(folder);foreach(string path in Directory.EnumerateFileSystemEntries(folder)){Need(records.Count<6000,"Bounded old payload tree");Plain(path);var attr=File.GetAttributes(path);bool directory=(attr&FileAttributes.Directory)!=0;records.Add(new{path=path.Substring(root.Length+1),directory,attributes=(int)attr,bytes=directory?(long?)null:new FileInfo(path).Length,writtenUtc=File.GetLastWriteTimeUtc(path).ToString("O")});if(directory)Tree(root,path,records);}}
 static void Guard(){bool vm=false;using(var q=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))foreach(ManagementObject row in q.Get())using(row)vm|=Regex.IsMatch(Convert.ToString(row["Manufacturer"])+" "+Convert.ToString(row["Model"]),"QEMU|KVM|Bochs",RegexOptions.IgnoreCase);Need(vm&&Environment.UserName.Equals("NookTester",StringComparison.OrdinalIgnoreCase),"Dedicated VM and user only");Need(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Equals(@"C:\Users\NookTester\AppData\Local",StringComparison.OrdinalIgnoreCase),"Exact VM profile");Package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');Need(Path.GetDirectoryName(Package).Equals(@"C:\NookLab\Audit\apps",StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(Path.GetFileName(Package),@"\A[A-F0-9]{12}\z"),"Hash-addressed audit output only");Plain(Package);}
 static int Main(string[] args){bool passed=false;string error=null;try{Guard();Need(args.Length==1&&args[0]=="--read-after1304-cleanup","Fixed read-only operation required");string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),program=Path.Combine(local,"Programs","MoriDesk"),data=Path.Combine(local,"MoriDesk"),session=Path.Combine(data,"updates",Session),old=Path.Combine(program,Old);Plain(program);Plain(data);
 Capture("current-native-manifest.json",Path.Combine(data,"browser","app.mori.desk.json"));
 var browserRoot=Path.Combine(data,"browser");var browserEntries=new List<object>();if(Directory.Exists(browserRoot))Tree(browserRoot,browserRoot,browserEntries);Artifact("current-browser-tree.json",browserRoot,Utf8.GetBytes(Json.Serialize(new{exists=Directory.Exists(browserRoot),entries=browserEntries,contentHashesComputed=false,sourceWrites=false})));
 Capture("active-install.json",Path.Combine(program,"active-install.json"));
 foreach(string name in new[]{"installer-cleanup.json","result.json","startup-ready.json","ready.json","candidate.json","request.json","staging.json","activation-attempted.json","cancel.request"})Capture("session-"+name,Path.Combine(session,name));
 Capture("version-cleanup.json",Path.Combine(data,"maintenance","version-cleanup.json"));Capture("staging-cleanup.json",Path.Combine(data,"updates","staging-cleanup.json"));
 Capture("old-installed-files.json",Path.Combine(old,".nook-installed-files.json"));Capture("old-cleanup-progress.json",Path.Combine(old,".nook-cleanup-progress.json"));
 var entries=new List<object>();if(Directory.Exists(old))Tree(old,old,entries);Artifact("old-payload-tree.json",old,Utf8.GetBytes(Json.Serialize(new{exists=Directory.Exists(old),entries,contentHashesComputed=false,sourceWrites=false})));
 string current=Path.Combine(program,Current);Plain(current);
 Capture("current-installed-files.json",Path.Combine(current,".nook-installed-files.json"));Capture("current-cleanup-progress.json",Path.Combine(current,".nook-cleanup-progress.json"));
 var currentEntries=new List<object>();if(Directory.Exists(current))Tree(current,current,currentEntries);Artifact("current-payload-tree.json",current,Utf8.GetBytes(Json.Serialize(new{exists=Directory.Exists(current),entries=currentEntries,contentHashesComputed=false,sourceWrites=false})));
 var roots=new List<object>();foreach(string folder in new[]{program,old,current,session,Path.Combine(data,"maintenance"),Path.Combine(program,".nook-cleanup-journal")}){Plain(folder);bool exists=Directory.Exists(folder);roots.Add(new{path=folder,exists,attributes=exists?(int?)File.GetAttributes(folder):null,writtenUtc=exists?File.GetLastWriteTimeUtc(folder).ToString("O"):null});}Artifact("cleanup-root-metadata.json",program,Utf8.GetBytes(Json.Serialize(new{entries=roots,sourceWrites=false})));
 string journal=Path.Combine(program,".nook-cleanup-journal");Plain(journal);if(Directory.Exists(journal)){int index=0;foreach(string file in Directory.EnumerateFiles(journal)){Need(index<128,"Bounded cleanup journal");Capture("cleanup-journal-"+(index++).ToString("D3")+".txt",file);}}
 passed=true;}catch(Exception e){error=e.ToString();}
 if(Package!=null)New(Path.Combine(Package,"cleanup-evidence-result.json"),Utf8.GetBytes(Json.Serialize(new{passed,error,scope="Read-only fixed after1304 cleanup state; no product execution or source writes",session=Session,oldDirectory=Old,currentDirectory=Current,artifacts=Artifacts,lockOwnersQueried=false,completedUtc=DateTime.UtcNow.ToString("O")})));
 return passed?0:1;}
}
