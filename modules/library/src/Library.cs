using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
    public class Asset : INotifyPropertyChanged {
        public string Id {get;set;} public string Name {get;set;} public string Relative {get;set;} public string Ext {get;set;}
        public string Tags {get;set;} public string Notes {get;set;} public string Source {get;set;} public string Added {get;set;} public string Created {get;set;}
        public long Bytes {get;set;} public bool Favorite {get;set;} public bool Deleted {get;set;}
        public int Width {get;set;} public int Height {get;set;} public string Color {get;set;} public string Perceptual {get;set;}
        bool selected,selecting; ImageSource thumbnail;
        public bool Selected {get{return selected;} set{selected=value; Changed("Selected");}}
        public bool Selecting {get{return selecting;} set{if(selecting==value)return;selecting=value;Changed("Selecting");}}
        public ImageSource Thumbnail {get{return thumbnail;} set{thumbnail=value; Changed("Thumbnail");}}
        public bool IsBookmark {get{return Ext==".url";}}
        public bool IsAudio {get{return MediaTypes.Audio.Contains(Ext);}}
        public bool IsGif {get{return string.Equals(Ext,".gif",StringComparison.OrdinalIgnoreCase);}}
        public bool IsVideo {get{return MediaTypes.Videos.Contains(Ext);}}
        public string SiteName {get{Uri uri;return IsBookmark&&SourceLink.TryParse(Source,out uri)?uri.Host:"";}}
        public string TypeLabel {get{return IsBookmark?"网址":Ext.TrimStart('.').ToUpperInvariant();}}
        public string Detail {get{return (Width>0?Width+" × "+Height+" · ":"")+Size(Bytes);}}
        public string Dimensions {get{return IsBookmark?SiteName:Width>0&&Height>0?Width+" × "+Height:TypeLabel;}}
        public void NotifyDimensions(){Changed("Dimensions");Changed("Detail");}
        public static string Size(long n) {return n<1048576 ? (n/1024.0).ToString("0.#")+" KB" : (n/1048576.0).ToString("0.#")+" MB";}
        public event PropertyChangedEventHandler PropertyChanged;
        void Changed(string p){if(PropertyChanged!=null)PropertyChanged(this,new PropertyChangedEventArgs(p));}
    }
    public class Category {public string Id{get;set;} public string Name{get;set;} public string Parent{get;set;} public string Scope{get;set;} public override string ToString(){return Name;}}
    public class ImportResult {public Asset Item; public bool Duplicate; public bool ChoiceRequired; public bool Skipped; public bool KeptCopy;}
    public sealed partial class Library : IDisposable {
        public readonly string Root;
        public readonly Database Db;
        readonly object writeGate=new object();
        public static readonly HashSet<string> Extensions=MediaTypes.Supported();
        public Library(string root) {
            Root=Path.GetFullPath(root); Directory.CreateDirectory(Root); Directory.CreateDirectory(Path.Combine(Root,".mori","thumbs")); Directory.CreateDirectory(Path.Combine(Root,"原图"));
            Db=new Database(Path.Combine(Root,".mori","library.db"));
            Db.Exec("CREATE TABLE IF NOT EXISTS assets(id TEXT PRIMARY KEY, hash TEXT NOT NULL UNIQUE, name TEXT NOT NULL, path TEXT NOT NULL UNIQUE, ext TEXT NOT NULL, bytes INTEGER NOT NULL, tags TEXT DEFAULT '', notes TEXT DEFAULT '', source TEXT DEFAULT '', added TEXT NOT NULL, favorite INTEGER DEFAULT 0, deleted INTEGER DEFAULT 0, width INTEGER DEFAULT 0, height INTEGER DEFAULT 0, color TEXT DEFAULT '', perceptual TEXT DEFAULT '', created TEXT DEFAULT ''); CREATE TABLE IF NOT EXISTS folders(id TEXT PRIMARY KEY, name TEXT NOT NULL, parent TEXT NOT NULL DEFAULT ''); CREATE TABLE IF NOT EXISTS memberships(asset TEXT NOT NULL REFERENCES assets(id), folder TEXT NOT NULL REFERENCES folders(id), PRIMARY KEY(asset,folder)); CREATE INDEX IF NOT EXISTS membership_folder ON memberships(folder); CREATE INDEX IF NOT EXISTS assets_added ON assets(added);");
            Db.Transaction(delegate{if(!Db.Query("PRAGMA table_info(assets)").Any(c=>c["name"]=="created"))Db.Exec("ALTER TABLE assets ADD COLUMN created TEXT DEFAULT ''");});
            InitializeBookmarkFolders();InitializeDeletionRecovery();
        }
        static string Q(string s){return Database.Q(s);}
        public string FullPath(Asset a){return Inside(Root,a.Relative);}
        public static string Inside(string root,string relative){var full=Path.GetFullPath(Path.Combine(root,relative)); var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar; if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("路径超出目标文件夹"); return full;}
        public static string SafeName(string s) {
            foreach(char c in Path.GetInvalidFileNameChars())s=s.Replace(c,'_'); s=s.Trim().TrimEnd('.'); if(s.Length>120)s=s.Substring(0,120).TrimEnd('.');
            if(string.IsNullOrWhiteSpace(s))s="未命名";
            string stem=s.Split('.')[0].ToUpperInvariant(); if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem))s="_"+s;
            return s;
        }
        public static string FreePath(string dir,string name){string p=Path.Combine(dir,name); string stem=Path.GetFileNameWithoutExtension(name),ext=Path.GetExtension(name); int i=2; while(File.Exists(p)||Directory.Exists(p))p=Path.Combine(dir,stem+" ("+(i++)+")"+ext); return p;}
        public static IEnumerable<string> ExpandFiles(IEnumerable<string> input) {
            foreach(var p in input) {
                if(File.Exists(p)) { if(Extensions.Contains(Path.GetExtension(p)))yield return p; }
                else if(Directory.Exists(p)) {
                    var stack=new Stack<string>();stack.Push(p);
                    while(stack.Count>0){var dir=stack.Pop();string[] files,dirs;try{files=Directory.GetFiles(dir);dirs=Directory.GetDirectories(dir);}catch(UnauthorizedAccessException){continue;}
                        foreach(var file in files)if(Extensions.Contains(Path.GetExtension(file)))yield return file;
                        foreach(var d in dirs)if((File.GetAttributes(d)&FileAttributes.ReparsePoint)==0)stack.Push(d);
                    }
                }
            }
        }
        static string FileCreated(string path){try{DateTime value=File.GetCreationTimeUtc(path);return value>DateTime.FromFileTimeUtc(0)?value.ToString("o",CultureInfo.InvariantCulture):"";}catch(IOException){return "";}catch(UnauthorizedAccessException){return "";}}
        static string CreatedValue(string value){DateTime parsed;return DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsed)&&parsed.Kind==DateTimeKind.Utc?parsed.ToString("o",CultureInfo.InvariantCulture):"";}
        public ImportResult Import(string source,string folder,string name,string url,string tags,string notes,string created=null,string duplicateAction="reuse",bool fillMissingSource=false) {
            if(string.Equals(Path.GetExtension(source),".url",StringComparison.OrdinalIgnoreCase))return ImportBookmark(ReadBookmark(source),name??Path.GetFileNameWithoutExtension(source),folder,tags,notes,duplicateAction);
            return ImportCore(source,folder,name,url,tags,notes,created,duplicateAction,fillMissingSource);
        }
        ImportResult ImportCore(string source,string folder,string name,string url,string tags,string notes,string created,string duplicateAction,bool fillMissingSource) {
            lock(writeGate) {
                if(duplicateAction!="ask"&&duplicateAction!="reuse"&&duplicateAction!="keep"&&duplicateAction!="skip")throw new ArgumentException("无效的重复文件处理方式", "duplicateAction");
                string ext=Path.GetExtension(source).ToLowerInvariant();if(!Extensions.Contains(ext))throw new IOException("暂不支持此格式："+ext);
                if(!string.IsNullOrEmpty(folder)){
                    var destination=Folders().FirstOrDefault(f=>f.Id==folder);if(destination==null)throw new IOException("分类已不存在，请重新选择");
                    // Media pasted while browsing bookmarks remains an unfiled media
                    // item, never an invisible member of a website-only folder.
                    if(ext!=".url"&&destination.Scope=="bookmark")folder="";
                }
                string title=string.IsNullOrWhiteSpace(name)?Path.GetFileNameWithoutExtension(source):name;
                // Keep the same read handle open through hashing/copying so a source cannot
                // change between the duplicate check and the bytes actually imported.
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)){
                    string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");
                    long bytes=input.Length;ImportResult result=null;string dest=null,temp=null;bool committed=false;
                    try{
                        // The transaction also serializes duplicate decisions across the app
                        // and independent native-host processes sharing the same library.
                        Db.Transaction(delegate{
                            var matches=Db.Query("SELECT * FROM assets WHERE hash="+Q(hash));
                            Asset existing=matches.Count==0?null:ReadAsset(matches[0]);
                            if(duplicateAction=="skip"){result=new ImportResult{Item=existing,Duplicate=existing!=null,Skipped=true};return;}
                            if(existing!=null&&duplicateAction=="ask"){result=new ImportResult{Item=existing,Duplicate=true,ChoiceRequired=true};return;}
                            if(existing!=null&&duplicateAction=="reuse"){
                                Db.Exec("UPDATE assets SET deleted=0 WHERE id="+Q(existing.Id));
                                Uri sourceUri;if(fillMissingSource&&string.IsNullOrWhiteSpace(existing.Source)&&SourceLink.TryParse(url,out sourceUri)){
                                    Db.Exec("UPDATE assets SET source="+Q(url.Trim())+" WHERE id="+Q(existing.Id));existing.Source=url.Trim();
                                }
                                if(!string.IsNullOrEmpty(folder))AddToFolder(new[]{existing.Id},folder);
                                existing.Deleted=false;result=new ImportResult{Item=existing,Duplicate=true};return;
                            }
                            string id=Guid.NewGuid().ToString("N"),month=DateTime.Now.ToString("yyyy-MM");
                            string dir=Path.Combine(Root,"原图",month);Directory.CreateDirectory(dir);
                            dest=Path.Combine(dir,SafeName(title)+"--"+id.Substring(0,10)+ext);temp=dest+".partial";
                            input.Position=0;using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output,131072);output.Flush(true);}
                            File.Move(temp,dest);string relative=dest.Substring(Root.TrimEnd('\\').Length+1);
                            string originalCreated=created==null?FileCreated(source):CreatedValue(created);
                            // Canonical rows always retain the exact SHA. A deliberate extra
                            // copy gets its own unique key; later imports still find canonical.
                            string uniqueHash=existing==null?hash:hash+":"+id;
                            Db.Exec("INSERT INTO assets(id,hash,name,path,ext,bytes,tags,notes,source,added,created) VALUES("+Q(id)+","+Q(uniqueHash)+","+Q(title)+","+Q(relative)+","+Q(ext)+","+bytes+","+Q(tags)+","+Q(notes)+","+Q(url)+","+Q(DateTime.UtcNow.ToString("o"))+","+Q(originalCreated)+ ")");
                            if(!string.IsNullOrEmpty(folder))AddToFolder(new[]{id},folder);
                            result=new ImportResult{Item=ReadAsset(Db.Query("SELECT * FROM assets WHERE id="+Q(id))[0]),Duplicate=existing!=null,KeptCopy=existing!=null};
                        });
                        committed=true;return result;
                    }finally{
                        if(temp!=null&&File.Exists(temp))File.Delete(temp);
                        if(!committed&&dest!=null&&File.Exists(dest))File.Delete(dest);
                    }
                }
            }
        }
        static Asset ReadAsset(Dictionary<string,string> r){return new Asset{Id=r["id"],Name=r["name"],Relative=r["path"],Ext=r["ext"],Bytes=long.Parse(r["bytes"]),Tags=r["tags"],Notes=r["notes"],Source=r["source"],Added=r["added"],Created=r["created"],Favorite=r["favorite"]=="1",Deleted=r["deleted"]=="1",Width=int.Parse(r["width"]),Height=int.Parse(r["height"]),Color=r["color"],Perceptual=r["perceptual"]};}
        public List<Asset> All(){return Db.Query("SELECT * FROM assets ORDER BY added DESC").Select(ReadAsset).ToList();}
        public List<Category> Folders(){return Db.Query("SELECT * FROM folders ORDER BY name COLLATE NOCASE").Select(r=>new Category{Id=r["id"],Name=r["name"],Parent=r["parent"],Scope=r["scope"]}).ToList();}
        public List<Category> Folders(bool bookmarks){return Folders().Where(f=>(f.Scope=="bookmark")==bookmarks).ToList();}
        public string CreateFolder(string name,string parent){return CreateFolder(name,parent,false);}
        public string CreateFolder(string name,string parent,bool bookmarks){if(string.IsNullOrWhiteSpace(name)||name.Length>120||name.Any(char.IsControl))throw new Exception("请输入 1–120 字的文件夹名称"); if(!string.IsNullOrEmpty(parent)&&!Folders(bookmarks).Any(f=>f.Id==parent))throw new Exception("上级文件夹不存在"); string id=Guid.NewGuid().ToString("N");Db.Exec("INSERT INTO folders(id,name,parent,scope) VALUES("+Q(id)+","+Q(name.Trim())+","+Q(parent)+","+Q(bookmarks?"bookmark":"media")+")");return id;}
        public void RenameFolder(string id,string name){
            if(string.IsNullOrWhiteSpace(name)||name.IndexOf('\0')>=0)throw new ArgumentException("请输入有效的分类名称");
            lock(writeGate)Db.Transaction(delegate{
                if(!Folders().Any(f=>f.Id==id))throw new InvalidOperationException("分类已不存在，请刷新后重试");
                Db.Exec("UPDATE folders SET name="+Q(name.Trim())+" WHERE id="+Q(id));
            });
        }
        // Delete only this logical category, never assets or descendants. Children
        // are promoted in the same transaction so no branch becomes inaccessible.
        public string DeleteFolder(string id){
            string parent="";
            lock(writeGate)Db.Transaction(delegate{
                var folders=Folders();var folder=folders.FirstOrDefault(f=>f.Id==id);
                if(folder==null)throw new InvalidOperationException("分类已不存在，请刷新后重试");
                parent=folder.Parent;
                if(parent==id||(!string.IsNullOrEmpty(parent)&&!folders.Any(f=>f.Id==parent)))parent="";
                Db.Exec("UPDATE folders SET parent="+Q(parent)+" WHERE parent="+Q(id));
                Db.Exec("DELETE FROM memberships WHERE folder="+Q(id));
                Db.Exec("DELETE FROM folders WHERE id="+Q(id));
            });
            return parent;
        }
        public void AddToFolder(IEnumerable<string> ids,string folder){Db.Transaction(delegate{
            var destination=Folders().FirstOrDefault(f=>f.Id==folder);if(destination==null)throw new InvalidOperationException("分类已不存在，请重新选择");
            foreach(string id in ids){var asset=Db.Query("SELECT ext FROM assets WHERE id="+Q(id));if(asset.Count!=1||(asset[0]["ext"]==".url")!=(destination.Scope=="bookmark"))throw new InvalidOperationException("图片、视频和文档不能与网址使用同一分类");Db.Exec("INSERT OR IGNORE INTO memberships VALUES("+Q(id)+","+Q(folder)+")");}
        });}
        public HashSet<string> Members(string folder,bool children){var folders=new HashSet<string>{folder};if(children){bool change=true;var all=Folders();while(change){change=false;foreach(var f in all)if(folders.Contains(f.Parent)&&folders.Add(f.Id))change=true;}}return new HashSet<string>(Db.Query("SELECT asset FROM memberships WHERE folder IN ("+string.Join(",",folders.Select(Q))+")").Select(r=>r["asset"]));}
        public HashSet<string> Classified(){return new HashSet<string>(Db.Query("SELECT asset FROM memberships").Select(r=>r["asset"]));}
        public List<Category> AssetFolders(string id){var ids=new HashSet<string>(Db.Query("SELECT folder FROM memberships WHERE asset="+Q(id)).Select(r=>r["folder"]));return Folders().Where(f=>ids.Contains(f.Id)).ToList();}
        public void RemoveFromFolder(IEnumerable<string> ids,string folder){Db.Transaction(delegate {foreach(var id in ids)Db.Exec("DELETE FROM memberships WHERE asset="+Q(id)+" AND folder="+Q(folder));});}
        public void Update(Asset a){Db.Exec("UPDATE assets SET name="+Q(a.Name)+",tags="+Q(a.Tags)+",notes="+Q(a.Notes)+",source="+Q(a.Source)+",favorite="+(a.Favorite?1:0)+" WHERE id="+Q(a.Id));}
        public void Trash(IEnumerable<string> ids,bool value){Db.Transaction(delegate {foreach(var id in ids)Db.Exec("UPDATE assets SET deleted="+(value?1:0)+" WHERE id="+Q(id));});}
        public const string ThumbnailSuffix=".512.png";
        public string ThumbnailPath(Asset a){return Path.Combine(Root,".mori","thumbs",a.Id+ThumbnailSuffix);}
        public void BuildThumbnail(Asset a) {
            if(!MediaTypes.Contains(a,"pictures")&&!MediaTypes.Contains(a,"videos"))return;
            string path=ThumbnailPath(a);if(File.Exists(path))return;
            try {
                BitmapSource bitmap;
                if(MainWindow.IsVideo(a)){int width,height;bitmap=VideoThumbnail.Render(FullPath(a),out width,out height);if(bitmap==null)return;if(width>0&&height>0){a.Width=width;a.Height=height;}}
                else if(a.Ext==".svg"){int width,height;bitmap=SvgRaster.Render(FullPath(a),512,out width,out height);a.Width=width;a.Height=height;}
                else{
                using(var stream=File.OpenRead(FullPath(a))){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);a.Width=decoder.Frames[0].PixelWidth;a.Height=decoder.Frames[0].PixelHeight;}
                if(a.Width<1||a.Height<1)return;
                double scale=Math.Min(1,512.0/Math.Max(a.Width,a.Height));
                var raster=new BitmapImage();raster.BeginInit();raster.CacheOption=BitmapCacheOption.OnLoad;raster.DecodePixelWidth=Math.Max(1,(int)(a.Width*scale));raster.DecodePixelHeight=Math.Max(1,(int)(a.Height*scale));raster.UriSource=new Uri(FullPath(a));raster.EndInit();raster.Freeze();bitmap=raster;
                }
                var tiny=new TransformedBitmap(bitmap,new ScaleTransform(9.0/bitmap.PixelWidth,8.0/bitmap.PixelHeight));var pixels=new FormatConvertedBitmap(tiny,PixelFormats.Bgra32,null,0);byte[] data=new byte[9*8*4];pixels.CopyPixels(data,36,0);long red=0,green=0,blue=0,alpha=0;ulong bits=0;
                for(int y=0;y<8;y++)for(int x=0;x<9;x++){int p=(y*9+x)*4;red+=(long)data[p+2]*data[p+3];green+=(long)data[p+1]*data[p+3];blue+=(long)data[p]*data[p+3];alpha+=data[p+3];if(x<8){int next=p+4;int light=data[p]*114+data[p+1]*587+data[p+2]*299;int right=data[next]*114+data[next+1]*587+data[next+2]*299;if(light>right)bits|=1UL<<(y*8+x);}}
                if(alpha>0)a.Color=string.Format("#{0:X2}{1:X2}{2:X2}",red/alpha,green/alpha,blue/alpha);a.Perceptual=bits.ToString("X16");
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));string pending=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{using(var output=File.Create(pending))encoder.Save(output);if(!File.Exists(path))File.Move(pending,path);}finally{if(File.Exists(pending))File.Delete(pending);}
                Db.Exec("UPDATE assets SET width="+a.Width+",height="+a.Height+",color="+Q(a.Color)+",perceptual="+Q(a.Perceptual)+" WHERE id="+Q(a.Id));
            }catch(NotSupportedException){}catch(FileFormatException){}catch(IOException){}catch(ArgumentException){}
        }
        public static int Similarity(string a,string b){ulong x,y;if(!ulong.TryParse(a,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out x)||!ulong.TryParse(b,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out y))return 65;ulong v=x^y;int n=0;while(v!=0){n++;v&=v-1;}return n;}
        public int Export(IEnumerable<Asset> assets,string destination,bool hierarchy,string renamePrefix) {
            var list=assets.ToList();Directory.CreateDirectory(destination);int count=0,index=1;var folders=Folders();
            foreach(var a in list) {
                var destinations=new List<string>();if(hierarchy){foreach(var f in AssetFolders(a.Id)){var parts=new List<string>();var seen=new HashSet<string>();var current=f;while(current!=null&&seen.Add(current.Id)){parts.Insert(0,SafeName(current.Name));current=folders.FirstOrDefault(t=>t.Id==current.Parent);}destinations.Add(parts.Count>0?Inside(destination,Path.Combine(parts.ToArray())):destination);}}
                if(destinations.Count==0)destinations.Add(hierarchy?Path.Combine(destination,"未分类"):destination);
                foreach(var dir in destinations.Distinct(StringComparer.OrdinalIgnoreCase)){Directory.CreateDirectory(dir);string filename=SafeName(string.IsNullOrWhiteSpace(renamePrefix)?a.Name:renamePrefix+"-"+index.ToString("D4"))+a.Ext;File.Copy(FullPath(a),FreePath(dir,filename),false);count++;}index++;
            }return count;
        }
        public void Backup(string output) {
            lock(writeGate){EnsureDeletionComplete();if(File.Exists(output))throw new IOException("备份文件已存在，请换一个名称");string snapshot=Path.Combine(Root,".mori","snapshot-"+Guid.NewGuid().ToString("N")+".db");string partial=output+".partial";
                try{Db.Exec("VACUUM INTO "+Q(snapshot));List<Asset> snapshotAssets;using(var snapshotDb=new Database(snapshot))snapshotAssets=snapshotDb.Query("SELECT * FROM assets").Select(ReadAsset).ToList();using(var archive=ZipFile.Open(partial,ZipArchiveMode.Create)){archive.CreateEntryFromFile(snapshot,".mori/library.db",CompressionLevel.Optimal);foreach(var a in snapshotAssets)archive.CreateEntryFromFile(FullPath(a),a.Relative.Replace('\\','/'),CompressionLevel.NoCompression);}File.Move(partial,output);}
                finally{if(File.Exists(snapshot))File.Delete(snapshot);if(File.Exists(partial))File.Delete(partial);}
            }
        }
        public static void Restore(string zip,string destination) {
            if(Directory.Exists(destination)&&Directory.EnumerateFileSystemEntries(destination).Any())throw new IOException("请选一个空文件夹，避免覆盖原有数据");
            using(var archive=ZipFile.OpenRead(zip)){if(!archive.Entries.Any(e=>e.FullName==".mori/library.db"))throw new IOException("不是 Mori 图库备份");foreach(var e in archive.Entries){Inside(destination,e.FullName);if(!e.FullName.StartsWith("原图/",StringComparison.Ordinal)&&e.FullName!=".mori/library.db")throw new IOException("备份包含未知文件");}foreach(var e in archive.Entries){string target=Inside(destination,e.FullName);Directory.CreateDirectory(Path.GetDirectoryName(target));e.ExtractToFile(target,false);}}
        }
        static Dictionary<string,object> ObjectJson(string path){var json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024};return json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));}
        static string Str(Dictionary<string,object> d,string key){return d.ContainsKey(key)&&d[key]!=null?Convert.ToString(d[key]):"";}
        static IEnumerable<object> Array(Dictionary<string,object> d,string key){var array=d.ContainsKey(key)?d[key] as IEnumerable:null;return array==null?Enumerable.Empty<object>():array.Cast<object>();}
        static string EagleCreated(Dictionary<string,object> data){
            if(!data.ContainsKey("btime"))return null;
            long milliseconds;if(!long.TryParse(Str(data,"btime"),NumberStyles.Integer,CultureInfo.InvariantCulture,out milliseconds)||milliseconds<=0)return "";
            try{return new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(milliseconds).ToString("o",CultureInfo.InvariantCulture);}catch(ArgumentOutOfRangeException){return "";}
        }
        internal static Dictionary<string,object> EagleMetadataForFile(string source){
            // Only a matching original in Eagle's images/<id>.info directory may
            // supply metadata. Ordinary sidecars, thumbnails and malformed files
            // must not attach somebody else's source URL to an imported image.
            try{
                var file=new FileInfo(source);var dir=file.Directory;
                if(!file.Exists||dir==null||!dir.Name.EndsWith(".info",StringComparison.OrdinalIgnoreCase)||dir.Parent==null||!dir.Parent.Name.Equals("images",StringComparison.OrdinalIgnoreCase))return null;
                string meta=Path.Combine(dir.FullName,"metadata.json");if(!File.Exists(meta)||new FileInfo(meta).Length>4*1024*1024)return null;
                var data=ObjectJson(meta);if(data==null)return null;
                string id=Str(data,"id"),ext=Str(data,"ext").TrimStart('.'),name=Str(data,"name");
                if(string.IsNullOrEmpty(id)||!dir.Name.Equals(id+".info",StringComparison.OrdinalIgnoreCase)||!file.Extension.Equals("."+ext,StringComparison.OrdinalIgnoreCase))return null;
                if(!file.Name.Equals(id+"."+ext,StringComparison.OrdinalIgnoreCase)&&!file.Name.Equals(name+"."+ext,StringComparison.OrdinalIgnoreCase))return null;
                return data;
            }catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(InvalidOperationException){return null;}
        }
        static string EagleSource(Dictionary<string,object> data){string value=Str(data,"url").Trim();Uri uri;return SourceLink.TryParse(value,out uri)?value:"";}
        public ImportResult ImportLocalFile(string source,string folder,string duplicateAction="ask"){
            var data=EagleMetadataForFile(source);
            return data==null?Import(source,folder,null,"","","",null,duplicateAction):Import(source,folder,Str(data,"name"),EagleSource(data),string.Join(", ",Array(data,"tags").Select(Convert.ToString)),Str(data,"annotation"),EagleCreated(data),duplicateAction,true);
        }
        internal void MergeEagleMetadata(Asset item,Dictionary<string,object> data){
            // Full-library merge keeps Mori edits, while retaining additional Eagle
            // tags/notes/links belonging to byte-identical originals.
            var tags=(item.Tags??"").Split(new[]{',','，',';'},StringSplitOptions.RemoveEmptyEntries).Select(t=>t.Trim()).ToList();
            foreach(string tag in Array(data,"tags").Select(Convert.ToString).Where(t=>!string.IsNullOrWhiteSpace(t)))if(!tags.Contains(tag.Trim(),StringComparer.OrdinalIgnoreCase))tags.Add(tag.Trim());
            item.Tags=string.Join(", ",tags);
            string note=Str(data,"annotation").Trim();if(note!=""&&(item.Notes??"").IndexOf(note,StringComparison.Ordinal)<0)item.Notes=string.IsNullOrWhiteSpace(item.Notes)?note:item.Notes+"\n\n[Eagle]\n"+note;
            string url=EagleSource(data);if(url!=""&&item.Source!=url&&(item.Notes??"").IndexOf(url,StringComparison.Ordinal)<0)item.Notes=(item.Notes??"").TrimEnd()+"\n\nEagle 来源："+url;
            Update(item);
            int w,h;if((item.Width<=0||item.Height<=0)&&int.TryParse(Str(data,"width"),out w)&&int.TryParse(Str(data,"height"),out h)&&w>0&&h>0&&w<=1000000&&h<=1000000){item.Width=w;item.Height=h;Db.Exec("UPDATE assets SET width="+w+",height="+h+" WHERE id="+Q(item.Id));}
        }
        public string MigrateEagle(string source,Action<string> progress) {
            if(string.Equals(Path.GetFullPath(source),Root,StringComparison.OrdinalIgnoreCase))throw new IOException("源图库和目标不能相同");string images=Path.Combine(source,"images");if(!Directory.Exists(images))throw new IOException("所选目录中没有 Eagle 的 images 文件夹");
            var mapping=new Dictionary<string,string>();string container=CreateFolder("Eagle 导入 "+DateTime.Now.ToString("MM-dd HHmm"),"");
            Action<IEnumerable<object>,string> parse=null;parse=delegate(IEnumerable<object> values,string parent){foreach(var value in values){var f=value as Dictionary<string,object>;if(f==null)continue;string old=Str(f,"id");string id=CreateFolder(Str(f,"name"),parent);if(!string.IsNullOrEmpty(old))mapping[old]=id;parse(Array(f,"children"),id);}};
            string meta=Path.Combine(source,"metadata.json");if(File.Exists(meta))parse(Array(ObjectJson(meta),"folders"),container);
            int added=0,duplicates=0,skipped=0;var errors=new List<string>();foreach(var dir in Directory.EnumerateDirectories(images,"*.info")) {
                try{string path=Path.Combine(dir,"metadata.json");if(!File.Exists(path)){skipped++;continue;}var data=ObjectJson(path);if(Str(data,"isDeleted").Equals("True",StringComparison.OrdinalIgnoreCase)){skipped++;continue;}string ext=Str(data,"ext").TrimStart('.');string id=Str(data,"id");string file=Path.Combine(dir,id+"."+ext);if(!File.Exists(file)){file=Directory.GetFiles(dir).FirstOrDefault(f=>Path.GetExtension(f).Equals("."+ext,StringComparison.OrdinalIgnoreCase)&&!Path.GetFileName(f).StartsWith("_"));}if(file==null||!File.Exists(file)){skipped++;continue;}
                    var result=Import(file,container,Str(data,"name"),EagleSource(data),string.Join(", ",Array(data,"tags").Select(Convert.ToString)),Str(data,"annotation"),EagleCreated(data),"reuse",true);MergeEagleMetadata(result.Item,data);foreach(var old in Array(data,"folders")){string fid;if(mapping.TryGetValue(Convert.ToString(old),out fid))AddToFolder(new[]{result.Item.Id},fid);}if(result.Duplicate)duplicates++;else added++;progress("迁移：新增 "+added+"，重复 "+duplicates);
                }catch(Exception ex){skipped++;errors.Add(Path.GetFileName(dir)+": "+ex.Message);}
            }
            return "迁移完成：新增 "+added+"，合并重复 "+duplicates+"，跳过 "+skipped+"。源图库未改动。"+(errors.Count>0?"\n"+string.Join("\n",errors.Take(8)):"");
        }
        public void Dispose(){Db.Dispose();}
    }
}
