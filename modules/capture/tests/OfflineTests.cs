using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingJie;

static class OfflineTests {
    static OfflineCatalog Catalog(int revision){var catalog=OfflinePacks.Parse(OfflinePacks.Resource("QingJie.OfflineCatalog.json"));catalog.Revision=revision;return catalog;}
    static string Stage(){string stage=Path.Combine(OfflinePacks.Folder,"stage-"+Guid.NewGuid().ToString("N"));foreach(var file in new[]{"runtime/python.exe","runtime/python313.dll","worker.py","models/en_zh/model/model.bin","models/en_zh/sentencepiece.model","models/zh_en/model/model.bin","models/zh_en/sentencepiece.model"}){string path=Path.Combine(stage,file);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"synthetic placeholder, not executable");}return stage;}
    static bool Fails(Action action){try{action();return false;}catch{return true;}}
    public static void Run(Action<bool,string> check){
        string before=OfflinePacks.TestFolder;string sandbox=Path.Combine(Path.GetTempPath(),"QingJie-Offline-Tests-"+Guid.NewGuid().ToString("N"));OfflinePacks.TestFolder=Path.Combine(sandbox,"offline-translation");Directory.CreateDirectory(OfflinePacks.Folder);
        try{
            var catalog=Catalog(1);check(catalog.DownloadBytes==189350759&&catalog.Identity.StartsWith("pack-1-"),"offline manifest pins exact bilingual runtime sizes and hashes");
            check(OfflinePacks.Installed==null&&Translation.TextCacheKey("hello","zh","offline")!=Translation.TextCacheKey("hello","zh","tencent"),"missing offline pack cannot share online cache");
            check(Fails(()=>Translation.Translate("hello",CancellationToken.None,"zh","offline").GetAwaiter().GetResult()),"missing offline pack fails without network fallback");
            check(OfflineTranslation.Supported("Hello，中文 123!")&&!OfflineTranslation.Supported("안녕하세요")&&!OfflineTranslation.Supported("画像を保存")&&!OfflineTranslation.Supported("Bonjour é"),"bilingual pack rejects unsupported scripts instead of pretending multilingual support");
            catalog.Artifacts[0].Url="http://www.python.org/insecure.zip";check(Fails(()=>catalog.Validate()),"offline manifest rejects insecure download URLs");catalog=Catalog(1);catalog.Artifacts[0].Url="https://untrusted.example/model.zip";check(Fails(()=>catalog.Validate()),"offline manifest rejects untrusted hosts");catalog=Catalog(1);catalog.Artifacts[0].Sha256="bad";check(Fails(()=>catalog.Validate()),"offline manifest requires SHA256 for every component");catalog=Catalog(1);catalog.Schema=2;check(Fails(()=>catalog.Validate()),"incompatible offline schema requires app update");
            foreach(string path in new[]{"../escape.txt","/absolute.txt","C:/outside.txt","safe/../../outside.txt","safe/evil.txt:stream","safe/../file","safe./evil"})check(Fails(()=>OfflinePacks.ArchivePath(OfflinePacks.Folder,path)),"offline extraction rejects unsafe path "+path);
            string zip=Path.Combine(sandbox,"unsafe.zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){using(var writer=new StreamWriter(archive.CreateEntry("../outside.txt").Open()))writer.Write("bad");}
            check(Fails(()=>OfflinePacks.Extract(zip,OfflinePacks.Folder,"python",CancellationToken.None))&&!File.Exists(Path.Combine(sandbox,"outside.txt")),"archive traversal is rejected before writing outside staging");
            var artifact=Catalog(1).Artifacts[0];check(!OfflinePacks.Verify(zip,artifact),"corrupt archive cannot pass component checksum");
            string pins=Path.Combine(sandbox,"pins");Directory.CreateDirectory(pins);File.WriteAllText(Path.Combine(pins,"keep.txt"),"user pin sentinel");
            check(Fails(()=>OfflinePacks.SafeDelete(pins))&&Fails(()=>OfflinePacks.SafeDelete(OfflinePacks.Folder)),"offline deletion refuses sibling pin folder and its own broad root");
            string first=Stage();OfflinePacks.Activate(first,Catalog(1),100,CancellationToken.None);check(OfflinePacks.Installed.Catalog.Revision==1&&!Directory.Exists(first),"verified pack activates by atomic pointer");string key=Translation.TextCacheKey("hello","zh","offline");
            using(var stopped=new CancellationTokenSource()){stopped.Cancel();string pending=Stage();check(Fails(()=>OfflinePacks.Activate(pending,Catalog(2),200,stopped.Token))&&OfflinePacks.Installed.Catalog.Revision==1,"cancelled activation retains the working offline pack");OfflinePacks.SafeDelete(pending);check(Fails(()=>OfflinePacks.Install(Catalog(2),null,stopped.Token).GetAwaiter().GetResult())&&OfflinePacks.Installed.Catalog.Revision==1,"cancelled download leaves old pack active");}
            string pointer=Path.Combine(OfflinePacks.Folder,"current.json");File.SetAttributes(pointer,FileAttributes.ReadOnly);string failed=Stage();try{check(Fails(()=>OfflinePacks.Activate(failed,Catalog(2),200,CancellationToken.None))&&OfflinePacks.Installed.Catalog.Revision==1,"pointer write failure rolls back new files and retains old pack");}finally{File.SetAttributes(pointer,FileAttributes.Normal);}
            string second=Stage();OfflinePacks.Activate(second,Catalog(2),200,CancellationToken.None);check(OfflinePacks.Installed.Catalog.Revision==2&&!Directory.Exists(OfflinePacks.PackPath(Catalog(1)))&&key!=Translation.TextCacheKey("hello","zh","offline"),"successful update changes cache version and reclaims old pack");
            OfflinePacks.Gate.Wait();try{using(var stopped=new CancellationTokenSource()){var waiting=OfflineTranslation.Translate(new[]{"hello"},"zh",stopped.Token);stopped.Cancel();check(Fails(()=>waiting.GetAwaiter().GetResult()),"queued offline translation cancels without starting model");}}finally{OfflinePacks.Gate.Release();}
            OfflinePacks.Uninstall(CancellationToken.None).GetAwaiter().GetResult();check(OfflinePacks.Installed==null&&Directory.GetDirectories(OfflinePacks.Folder).Length==0&&File.ReadAllText(Path.Combine(pins,"keep.txt"))=="user pin sentinel","uninstall reclaims only offline pack files and preserves pins");
        }finally{OfflinePacks.TestFolder=before;}
    }
    public static void Live(Action<bool,string> check){
        string before=OfflinePacks.TestFolder;OfflinePacks.TestFolder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"offline-live-test");
        try{
            var catalog=OfflinePacks.Bundled;var watch=System.Diagnostics.Stopwatch.StartNew();
            if(OfflinePacks.Installed==null)OfflinePacks.Install(catalog,new Progress<string>(Console.WriteLine),CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("OFFLINE installed bytes="+OfflinePacks.Installed.Bytes+" setup_ms="+watch.ElapsedMilliseconds);
            watch.Restart();var english=Translation.Translate("这个零件需要改成黑色，交货时间是下周五。",CancellationToken.None,"en","offline").GetAwaiter().GetResult();Console.WriteLine("OFFLINE zh_en ms="+watch.ElapsedMilliseconds+" result="+english);check(english.IndexOf("black",StringComparison.OrdinalIgnoreCase)>=0&&english.IndexOf("Friday",StringComparison.OrdinalIgnoreCase)>=0&&!english.Contains("\u2581"),"installed offline model translates Chinese input without tokenizer artifacts");
            watch.Restart();var chinese=Translation.TranslateImage(new[]{"Save image","Copy text","Please confirm the delivery date.","中文"},CancellationToken.None,"zh","offline").GetAwaiter().GetResult();Console.WriteLine("OFFLINE en_zh image ms="+watch.ElapsedMilliseconds+" results="+string.Join(" | ",chinese));check(chinese.Length==4&&chinese[0].Contains("保存")&&chinese[1].Contains("复制")&&chinese[2].Contains("日期")&&chinese[3]=="中文","offline image batch keeps exact region count and same-target text");
            watch.Restart();check(Translation.Translate("这个零件需要改成黑色，交货时间是下周五。",CancellationToken.None,"en","offline").GetAwaiter().GetResult()==english&&watch.ElapsedMilliseconds<100,"repeated offline translation uses short-lived versioned cache");
            using(var stop=new CancellationTokenSource()){stop.CancelAfter(100);check(Fails(()=>Translation.Translate(string.Join(" ",Enumerable.Repeat("Please confirm the order and save this image.",90)),stop.Token,"zh","offline").GetAwaiter().GetResult()),"active offline translation cancellation terminates child");}
            check(Fails(()=>Translation.Translate("안녕하세요",CancellationToken.None,"zh","offline").GetAwaiter().GetResult()),"unsupported offline language never falls through to online provider");
            bool child=System.Diagnostics.Process.GetProcessesByName("python").Any(p=>{try{return p.MainModule.FileName.StartsWith(OfflinePacks.Folder,StringComparison.OrdinalIgnoreCase);}catch{return false;}finally{p.Dispose();}});check(!child,"offline model process is absent after success and cancellation");
        }finally{OfflinePacks.TestFolder=before;}
    }
}
