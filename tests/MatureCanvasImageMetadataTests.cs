using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace MoriDesk {
 internal static class MatureCanvasImageMetadataTests {
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=64*1024*1024};}
  static Dictionary<string,object> Obj(object value){return (Dictionary<string,object>)value;}
  static Dictionary<string,object> Map(params object[] values){var map=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)map.Add((string)values[i],values[i+1]);return map;}
  static Dictionary<string,object> Scene(MatureCanvasDocument doc){return Obj(Json().DeserializeObject(doc.SceneJson));}
  static Dictionary<string,object> Records(Dictionary<string,object> scene){return Obj(Obj(scene["document"])["store"]);}
  static Dictionary<string,object> Props(Dictionary<string,object> scene){return Obj(Obj(Records(scene)["asset:drop"])["props"]);}
  static bool Rejected(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string source=Path.Combine(root,"source.png");
   using(var image=new System.Drawing.Bitmap(16,12)){using(var paint=System.Drawing.Graphics.FromImage(image))paint.Clear(System.Drawing.Color.MediumPurple);image.Save(source,System.Drawing.Imaging.ImageFormat.Png);}
   byte[] original=File.ReadAllBytes(source);var store=new MatureCanvasStore(Path.Combine(root,"data"));var doc=store.Create("drop metadata");var resource=store.RegisterImage(doc,source,"drop");
   var scene=Scene(doc);var records=Records(scene);
   records["asset:drop"]=Map("id","asset:drop","typeName","asset","type","image","meta",Map(),"props",Map("w",16,"h",12,"name","source.png","mimeType","image/png","src",MatureCanvasStore.ResourceUrl(doc.Id,resource.FileId),"isAnimated",false));
   records["shape:drop"]=Map("id","shape:drop","typeName","shape","type","image","x",17,"y",29,"rotation",0,"index","a1","parentId","page:page","isLocked",false,"opacity",1,"meta",Map(),"props",Map("w",160,"h",120,"assetId","asset:drop","playing",true,"url","","crop",null,"flipX",false,"flipY",false));
   doc.SceneJson=Json().Serialize(scene);store.Save(doc);string baseline=doc.SceneJson;
   foreach(object size in new object[]{1,original.Length,256L*1024*1024,4294967296L,0.5}){
    scene=Scene(doc);Props(scene)["fileSize"]=size;string imported=Json().Serialize(scene);
    check(store.NormalizeScene(imported)==baseline,"optional SDK fileSize normalizes without altering resource identity, dimensions or layout: "+size);
    doc.SceneJson=imported;store.Save(doc);doc=store.Load(doc.Id);
    check(doc.SceneJson==baseline&&doc.ImageCount==1&&doc.Resources.Count==1,"direct store save/reopen keeps old-reader-compatible snapshot: "+size);
   }
   // Missing metadata is valid too: native Add images and old projects omit it.
   store.Save(doc);check(store.Load(doc.Id).SceneJson==baseline,"image assets without optional metadata still round-trip");
   string manifest=Path.Combine(store.Root,doc.Id,MatureCanvasStore.ManifestName);byte[] durable=File.ReadAllBytes(manifest);
   foreach(object size in new object[]{0,-1,"123",null,true,new object[]{123},new Dictionary<string,object>{{"size",123}}}){
    scene=Scene(doc);Props(scene)["fileSize"]=size;string invalid=Json().Serialize(scene);
    check(Rejected(()=>store.NormalizeScene(invalid)),"invalid SDK fileSize cannot be silently discarded: "+Json().Serialize(size));
    var failed=store.Load(doc.Id);failed.SceneJson=invalid;check(Rejected(()=>store.Save(failed))&&File.ReadAllBytes(manifest).SequenceEqual(durable),"invalid fileSize cannot replace a committed scene");
   }
   scene=Scene(doc);Props(scene)["unexpectedField"]="not allowed";var unknown=store.Load(doc.Id);unknown.SceneJson=store.NormalizeScene(Json().Serialize(scene));
   check(Rejected(()=>store.Save(unknown))&&File.ReadAllBytes(manifest).SequenceEqual(durable),"normalization preserves strict rejection of unrelated asset fields");
   var copied=store.CopyProject(doc,"copied metadata project");var reopened=store.Load(copied.Id);
   check(reopened.ImageCount==1&&reopened.Resources.Count==1&&!reopened.SceneJson.Contains("fileSize")&&reopened.SceneJson.Contains(MatureCanvasStore.ResourceUrl(copied.Id,reopened.Resources[0].FileId)),"copy preserves image and uses the new project's resource URL");
   check(File.ReadAllBytes(source).SequenceEqual(original),"metadata normalization never changes the original file");
  }
 }
}
