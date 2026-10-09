using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace QingJie {
    public sealed class OcrLanguagePack {
        public string Id,Name,Sha256;public long Bytes;
        public override string ToString(){return Name+" · "+(Bytes/1048576.0).ToString("0.0")+" MB";}
    }
    public static class OcrLanguagePacks {
        public static readonly OcrLanguagePack[] Available={
            new OcrLanguagePack{Id="kor",Name="韩文",Bytes=1677415,Sha256="6B85E11D9BBF07863B97B3523B1B112844C43E713DF8B66418A081FD1060B3B2"},
            new OcrLanguagePack{Id="jpn",Name="日文",Bytes=2471260,Sha256="1F5DE9236D2E85F5FDF4B3C500F2D4926F8D9449F28F5394472D9E8D83B91B4D"},
            new OcrLanguagePack{Id="rus",Name="俄文",Bytes=3861738,Sha256="E16E5E036CCE1D9EC2B00063CF8B54472625B9E14D893A169E2B0DEDEB4DF225"},
            new OcrLanguagePack{Id="fra",Name="法语",Bytes=1130365,Sha256="CED037562E8C80C13122DECE28DD477D399AF80911A28791A66A63AC1E3445CA"},
            new OcrLanguagePack{Id="deu",Name="德语",Bytes=1525436,Sha256="19D219BBB6672C869D20A9636C6816A81EB9A71796CB93EBE0CB1530E2CDB22D"},
            new OcrLanguagePack{Id="spa",Name="西班牙语",Bytes=2294433,Sha256="6F2E04D02774A18F01BED44B1111F2CD7F3BA7AC9DC4373CD3F898A40EA6B464"},
            new OcrLanguagePack{Id="por",Name="葡萄牙语",Bytes=1982756,Sha256="C4932B937207A9514B7514D518B931A99938C02A28A5A5A553F8599ED58B7DEB"},
            new OcrLanguagePack{Id="ita",Name="意大利语",Bytes=2701314,Sha256="B8F89E1E785118DAC4D51AE042C029A64EDB5C3EE42EF73027A6D412748D8827"},
            new OcrLanguagePack{Id="ara",Name="阿拉伯语",Bytes=1432056,Sha256="E3206D3DC87FD50C24A0FB9F01838615911D25168F4E64415244B67D2BB3E729"},
            new OcrLanguagePack{Id="tha",Name="泰语",Bytes=1072600,Sha256="294227CC2D1292B0ACB28D61D4115C88252B96D466CA90B417CF4CF0C67BF07C"},
            new OcrLanguagePack{Id="vie",Name="越南语",Bytes=531275,Sha256="79DF64CAF7BCFB2A27DF5042ECB6121E196EADA34DA774956995747636D5BFA1"}
        };
        internal static string TestSelection;
        internal static string TestFolder;
        public static string Folder{get{return TestFolder??Path.Combine(MoriDesk.Entry.DataRoot,"capture","languages");}}
        public static OcrLanguagePack Find(string id){return Available.FirstOrDefault(p=>p.Id==id);}
        public static string[] ParseSelection(string value){var ids=new System.Collections.Generic.HashSet<string>((value??"").Split('+'));return Available.Where(p=>ids.Contains(p.Id)).Select(p=>p.Id).ToArray();}
        public static string NormalizeSelection(string value){return string.Join("+",ParseSelection(value));}
        public static string SelectionKey{get{return NormalizeSelection(TestSelection??Preferences.Load().OcrLanguage);}}
        // Missing optional files do not disable Chinese/English or reuse stale OCR.
        public static string EffectiveSelectionKey{get{return string.Join("+",ParseSelection(SelectionKey).Where(Installed));}}
        public static string ModelPath(string id){if(Find(id)==null)throw new ArgumentException("Unknown OCR language");return Path.Combine(Folder,id+".traineddata");}
        public static bool Installed(string id){var pack=Find(id);return pack!=null&&File.Exists(ModelPath(id))&&new FileInfo(ModelPath(id)).Length==pack.Bytes;}
        public static bool Verify(string path,OcrLanguagePack pack){if(new FileInfo(path).Length!=pack.Bytes)return false;using(var input=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","")==pack.Sha256;}
        public static void Import(string path,string id){var pack=Find(id);if(pack==null||!Verify(path,pack))throw new InvalidOperationException("语言包版本或校验不正确，请使用指定的官方语言包。");Directory.CreateDirectory(Folder);Commit(path,ModelPath(id));}
        static void Commit(string source,string target){string pending=target+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.Copy(source,pending);if(File.Exists(target))File.Replace(pending,target,null);else File.Move(pending,target);}finally{if(File.Exists(pending))File.Delete(pending);}}
        public static async Task Install(string id,IProgress<int> progress,CancellationToken cancel){
            var pack=Find(id);if(pack==null)throw new ArgumentException("Unknown OCR language");
            string legacy=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tessdata",id+".traineddata");
            if(File.Exists(legacy)&&Verify(legacy,pack)){cancel.ThrowIfCancellationRequested();Import(legacy,id);return;}
            Directory.CreateDirectory(Folder);string pending=Path.Combine(Folder,id+"."+Guid.NewGuid().ToString("N")+".download");
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            try{using(var client=new HttpClient(){Timeout=TimeSpan.FromSeconds(60)})
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                string url="https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/87416418657359cb625c412a48b6e1d6d41c29bd/"+id+".traineddata";
                using(var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,timeout.Token))using(timeout.Token.Register(()=>response.Dispose())){
                    response.EnsureSuccessStatusCode();using(var input=await response.Content.ReadAsStreamAsync())using(var output=File.Create(pending)){
                        byte[] buffer=new byte[16384];long total=0;int read;
                        while((read=await input.ReadAsync(buffer,0,buffer.Length,timeout.Token))>0){total+=read;if(total>pack.Bytes)throw new InvalidDataException("语言包大小异常。");await output.WriteAsync(buffer,0,read,timeout.Token);if(progress!=null)progress.Report((int)(total*100/pack.Bytes));}
                    }
                }
                timeout.Token.ThrowIfCancellationRequested();Import(pending,id);
            }}finally{if(File.Exists(pending))File.Delete(pending);}
        }
        public static bool IsLatin(string id){return id=="fra"||id=="deu"||id=="spa"||id=="por"||id=="ita"||id=="vie";}
        public static bool IsScript(char c,string id){
            if(id=="kor")return Geometry.IsHangul(c);if(id=="jpn")return c>='\u3040'&&c<='\u30ff';if(id=="rus")return c>='\u0400'&&c<='\u052f';
            if(id=="ara")return (c>='\u0600'&&c<='\u06ff')||(c>='\ufb50'&&c<='\ufeff');if(id=="tha")return c>='\u0e00'&&c<='\u0e7f';
            return IsLatin(id)&&char.IsLetter(c)&&c<0x2e80;
        }
    }
}
