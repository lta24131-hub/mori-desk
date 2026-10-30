using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using QingJie;

namespace MoriDesk {
 // Metadata-only synthetic history: no pin window, screenshot or real PNG is
 // opened. Empty fixture files deliberately exercise existence/index routing.
 internal static class PinHistoryIndexTests {
  static void Image(string directory,string id){File.WriteAllBytes(Path.Combine(directory,id+".png"),new byte[]{1,2,3});}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);
   string a=Guid.NewGuid().ToString("N"),b=Guid.NewGuid().ToString("N"),orphan=Guid.NewGuid().ToString("N"),missing=Guid.NewGuid().ToString("N");
   Image(root,a);Image(root,b);Image(root,orphan);File.WriteAllBytes(Path.Combine(root,a+".translated.png"),new byte[]{4,5});
   var rows=new[]{new PinRecord{Id=b,Active=false,ClosedAt=123,Width=80},new PinRecord{Id=a,Active=true,Width=600},new PinRecord{Id=b,Active=true,Width=900},null,new PinRecord{Id="invalid"},new PinRecord{Id=missing}};
   File.WriteAllText(Path.Combine(root,"pins.json"),new JavaScriptSerializer().Serialize(rows));
   var store=new PinStore(root);
   check(store.Records.Count==3,"history index skips duplicate invalid and missing records and does not treat translated PNGs as original pins");
   check(store.Records[0].Id==b&&store.Records[1].Id==a&&store.Records[2].Id==orphan,"history index preserves manifest order and appends recoverable orphan images");
   check(!store.Records[0].Active&&store.Records[0].ClosedAt==123&&store.Records[0].Width==80&&store.Records[1].Active,"first duplicate keeps its active state and saved geometry");
   check(!store.Records[2].Active&&store.Records[2].ClosedAt>0,"orphan image is recoverable history and never silently opens on restart");
   string expected=string.Join("|",store.Records.Select(record=>record.Id));store.Save();
   check(string.Join("|",new PinStore(root).Records.Select(record=>record.Id))==expected,"saved history reload keeps the same exact IDs and ordering");
   File.Copy(Path.Combine(root,"pins.json"),Path.Combine(root,"pins.json.bak"),true);File.WriteAllText(Path.Combine(root,"pins.json"),"broken fixture");
   var recovered=new PinStore(root);check(string.Join("|",recovered.Records.Select(record=>record.Id))==expected,"backup history recovery retains the same deduplication and ordering");
   recovered.Save();check(new PinStore(root).Records.Count==3,"repaired manifest does not add phantom duplicate history on restart");
   string caseRoot=Path.Combine(root,"ordinal");Directory.CreateDirectory(caseRoot);string lower="abcdef1234567890abcdef1234567890",upper=lower.ToUpperInvariant();Image(caseRoot,lower);
   File.WriteAllText(Path.Combine(caseRoot,"pins.json"),new JavaScriptSerializer().Serialize(new[]{new PinRecord{Id=upper,Width=240},new PinRecord{Id=lower,Width=360},new PinRecord{Id=upper,Width=480}}));
   var exact=new PinStore(caseRoot);check(exact.Records.Count==2&&exact.Records[0].Id==upper&&exact.Records[0].Width==240&&exact.Records[1].Id==lower,"history index preserves original ordinal case-sensitive ID comparison and first duplicate semantics");
   Benchmark(Path.Combine(root,"large"),3000,check);
  }
  internal static void Benchmark(string root,int count,Action<bool,string> check){
   Directory.CreateDirectory(root);var rows=new List<PinRecord>();
   for(int i=0;i<count;i++){string id=Guid.NewGuid().ToString("N");Image(root,id);rows.Add(new PinRecord{Id=id,Active=false,ClosedAt=i+1,Width=100+i%400});}
   File.WriteAllText(Path.Combine(root,"pins.json"),new JavaScriptSerializer().Serialize(rows));
   var samples=new List<long>();PinStore store=null;
   for(int round=0;round<4;round++){var watch=Stopwatch.StartNew();store=new PinStore(root);watch.Stop();if(round>0)samples.Add(watch.ElapsedMilliseconds);}
   check(store.Records.Count==count&&store.Records.Select(record=>record.Id).SequenceEqual(rows.Select(record=>record.Id)),"large history loads every original record once in manifest order");
   check(store.Records.All(record=>!record.Active)&&store.Records.Last().ClosedAt==count,"large closed history remains closed with its recovery timestamps intact");
   samples.Sort();Console.WriteLine("PIN_HISTORY_LOAD records="+count+" medianMs="+samples[1]+" samplesMs="+string.Join(",",samples));
  }
 }
}
