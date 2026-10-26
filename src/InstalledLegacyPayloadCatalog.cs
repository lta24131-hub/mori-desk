using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using MoriDeskInstaller;

namespace MoriDesk {
 internal sealed class LegacyPayload {
  public string archiveSha256;
  public Dictionary<string,string> files;
  public string[] directories;
 }
 internal sealed class LegacyPayloadRecord {
  public int schemaVersion;
  public LegacyPayload[] payloads;
 }
 internal static class InstalledLegacyPayloadCatalog {
  // Generated only from hash-pinned release archives, never installed trees.
  const string Resource="Nook.LegacyInstalledPayloads.json";
  const string CatalogueSha256="5E4FF8FF802BA165EA8B09FAF92A410DCB4607326AF52A8F86B4BF246C4B39C7";
  static readonly Lazy<LegacyPayload[]> catalogue=new Lazy<LegacyPayload[]>(Load);
  static string Digest(Stream input){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
  internal static LegacyPayload[] Parse(byte[] bytes){
   if(bytes==null||bytes.Length<1||bytes.Length>1024*1024)throw new InvalidDataException("历史安装认证资料缺失。");
   using(var input=new MemoryStream(bytes,false))if(Digest(input)!=CatalogueSha256)throw new InvalidDataException("历史安装认证资料校验失败。");
   var data=new JavaScriptSerializer{MaxJsonLength=1024*1024,RecursionLimit=8}.Deserialize<LegacyPayloadRecord>(new UTF8Encoding(false,true).GetString(bytes));
   if(data==null||data.schemaVersion!=1||data.payloads==null||data.payloads.Length!=5)throw new InvalidDataException("历史安装认证资料版本不符。");
   return data.payloads;
  }
  internal static LegacyPayload[] Load(){
   using(var stream=typeof(InstalledLegacyPayloadCatalog).Assembly.GetManifestResourceStream(Resource)){
    if(stream==null||stream.Length<1||stream.Length>1024*1024)throw new InvalidDataException("历史安装认证资料不可用。");
    using(var output=new MemoryStream()){stream.CopyTo(output);return Parse(output.ToArray());}
   }
  }
  internal static bool CertifyWith(string folder,IEnumerable<LegacyPayload> payloads,Func<string,string> mainHash,Action<string,IDictionary<string,string>,IEnumerable<string>> verify){
   if(payloads==null||mainHash==null||verify==null)throw new ArgumentNullException();
   string hash=mainHash(Path.Combine(folder,"Mori.exe"));if(hash==null)return false;
   Exception last=null;
   foreach(var payload in payloads.Where(p=>p.files.ContainsKey("Mori.exe")&&p.files["Mori.exe"]==hash)){
    try{verify(folder,payload.files,payload.directories);return true;}
    catch(IOException error){last=error;}catch(InvalidDataException error){last=error;}catch(UnauthorizedAccessException error){last=error;}
   }
   // Known program but an altered/unknown extra file is a protected pending
   // case, never an excuse to infer ownership from the executable alone.
   if(last!=null)throw new IOException("历史版本与已发布安装包不完全一致，已保留待核对。",last);
   return false;
  }
  internal static bool TryCertify(string folder){
   return CertifyWith(folder,catalogue.Value,file=>{
    try{using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))return Digest(input);}
    catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}
   },InstalledVersionCleanup.WriteVerifiedLegacyManifest);
  }
 }
}
