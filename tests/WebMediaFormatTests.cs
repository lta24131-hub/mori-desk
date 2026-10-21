using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace MoriDesk {
 internal static class WebMediaFormatTests {
  static int Find(byte[] bytes,byte[] value,bool last=false){
   int result=-1;for(int at=0;at<=bytes.Length-value.Length;at++){bool same=true;for(int n=0;n<value.Length;n++)if(bytes[at+n]!=value[n]){same=false;break;}
    if(same){result=at;if(!last)return at;}
   }if(result<0)throw new InvalidOperationException("Fixture marker missing");return result;
  }
  static int Find(byte[] bytes,string text,bool last=false){return Find(bytes,Encoding.ASCII.GetBytes(text),last);}
  static byte[] Change(byte[] bytes,string before,string after,bool last=false){
   var result=(byte[])bytes.Clone();int at=Find(result,before,last);Encoding.ASCII.GetBytes(after).CopyTo(result,at);return result;
  }
  static byte[] CountZero(byte[] bytes){var result=(byte[])bytes.Clone();int at=Find(result,"stsz",true)+12;Array.Clear(result,at,4);return result;}
  static byte[] WithoutVideoHandler(byte[] bytes){
   // "vide" also appears in x264's videolan.org encoder metadata inside mdat.
   // Mutate the bounded hdlr box, not the first incidental byte-string match.
   var result=(byte[])bytes.Clone();int at=Find(result,Encoding.ASCII.GetBytes("hdlr\0\0\0\0\0\0\0\0vide"))+12;
   Encoding.ASCII.GetBytes("soun").CopyTo(result,at);return result;
  }
  internal static IEnumerable<byte[]> InvalidVideos(){
   var mp4=WebMediaFixtures.Mp4();var webm=WebMediaFixtures.WebM();
   yield return mp4.Take(32).ToArray(); // Complete ftyp, no movie/media boxes.
   yield return webm.Take(36).ToArray(); // Complete EBML header, no Segment.
   yield return mp4.Take(mp4.Length-1).ToArray();
   yield return webm.Take(webm.Length-1).ToArray();
   yield return Change(mp4,"mdat","free");
   yield return Change(mp4,"moov","free");
   yield return WithoutVideoHandler(mp4);
   yield return CountZero(mp4);
   var invalidEntry=(byte[])mp4.Clone();int entry=Find(invalidEntry,"avc1",true)-4;Array.Clear(invalidEntry,entry,4);invalidEntry[entry+3]=7;yield return invalidEntry;
   var noVideo=(byte[])webm.Clone();noVideo[Find(noVideo,new byte[]{0x83,0x81,0x01})+2]=2;yield return noVideo;
   var noCluster=(byte[])webm.Clone();noCluster[Find(noCluster,new byte[]{0x1f,0x43,0xb6,0x75})+3]=0x74;yield return noCluster;
   yield return mp4.Concat(new byte[]{0,0,0}).ToArray(); // Truncated next box.
   yield return mp4.Concat(new byte[]{0,0,0,1,102,114,101,101,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff}).ToArray();
   var badWebSize=(byte[])webm.Clone();int tracks=Find(badWebSize,new byte[]{0x16,0x54,0xae,0x6b},true);badWebSize[tracks+4]=0xff;yield return badWebSize; // Unknown-length Tracks is not permitted.
   var wrongTrack=(byte[])webm.Clone();int block=Find(wrongTrack,new byte[]{0xa3,0xbc,0x81,0x00,0x04,0x80});wrongTrack[block+2]=0x83;yield return wrongTrack;
   var emptyBlock=(byte[])webm.Clone();emptyBlock[block+1]=0x84;yield return emptyBlock;
   var badLace=(byte[])webm.Clone();badLace[block+5]=0x82;yield return badLace;
   var noDimensions=(byte[])webm.Clone();noDimensions[Find(noDimensions,new byte[]{0xb0,0x81,0x02})+2]=0;yield return noDimensions;
  }
  public static void Run(Action<bool,string> check){
   var mp4=WebMediaFixtures.Mp4();var webm=WebMediaFixtures.WebM();
   check(mp4.Length==3503&&webm.Length==814,"pinned genuine short-video fixtures retain their complete lengths");
   check(Mori.WebMediaFormat.DetectVideo(mp4)==".mp4"&&Mori.WebMediaFormat.HasCompleteVideo(mp4),"genuine tiny MP4 with a video sample passes container validation");
   check(Mori.WebMediaFormat.DetectVideo(webm)==".webm"&&Mori.WebMediaFormat.HasCompleteVideo(webm),"genuine tiny WebM with a video block passes container validation");
   check(Mori.WebMediaFormat.DetectVideo(mp4.Take(32).ToArray())==".mp4"&&!Mori.WebMediaFormat.HasCompleteVideo(mp4.Take(32).ToArray()),"MP4 header recognition is not proof of a complete video");
   check(Mori.WebMediaFormat.DetectVideo(webm.Take(36).ToArray())==".webm"&&!Mori.WebMediaFormat.HasCompleteVideo(webm.Take(36).ToArray()),"WebM header recognition is not proof of a complete video");
   check(Find(WithoutVideoHandler(mp4),"videolan.org")>=0&&!Mori.WebMediaFormat.HasCompleteVideo(WithoutVideoHandler(mp4)),"audio-only mutation changes the real video handler and preserves incidental encoder metadata");
   int index=0;foreach(var broken in InvalidVideos())check(!Mori.WebMediaFormat.HasCompleteVideo(broken),"header-only, truncated or empty video is rejected (case "+(++index)+")");
   var unknownCodec=Change(mp4,"avc1","zzzz",true);check(Mori.WebMediaFormat.HasCompleteVideo(unknownCodec),"structural validation does not require an installed MP4 codec or decode media");
   unknownCodec=Change(webm,"V_VP8","V_NEW");check(Mori.WebMediaFormat.HasCompleteVideo(unknownCodec),"structural validation does not restrict WebM codec identifiers");
   var unknownSegment=(byte[])webm.Clone();int segment=Find(unknownSegment,new byte[]{0x18,0x53,0x80,0x67});for(int n=1;n<8;n++)unknownSegment[segment+4+n]=0xff;
   check(Mori.WebMediaFormat.HasCompleteVideo(unknownSegment),"streaming WebM supports an unknown-length Segment");
   var unknownCluster=(byte[])webm.Clone();int cluster=Find(unknownCluster,new byte[]{0x1f,0x43,0xb6,0x75});unknownCluster[cluster+4]=0xff;
   check(Mori.WebMediaFormat.HasCompleteVideo(unknownCluster),"streaming WebM supports an unknown-length Cluster followed by segment-level Cues");
   var extended=mp4.Concat(new byte[]{0,0,0,1,102,114,101,101,0,0,0,0,0,0,0,16}).ToArray();
   check(Mori.WebMediaFormat.HasCompleteVideo(extended),"complete 64-bit MP4 box lengths are supported");
   var toEnd=mp4.Concat(new byte[]{0,0,0,0,102,114,101,101}).ToArray();check(Mori.WebMediaFormat.HasCompleteVideo(toEnd),"MP4 size-zero trailing boxes extend to the enclosing boundary");
   var fragmented=FragmentedMp4(mp4,2,1);check(Mori.WebMediaFormat.HasCompleteVideo(fragmented),"fragmented MP4 accepts a bounded video-track trun with a nonempty media payload");
   check(!Mori.WebMediaFormat.HasCompleteVideo(FragmentedMp4(mp4,1,1)),"an audio-track fragment cannot stand in for a video fragment");
   check(!Mori.WebMediaFormat.HasCompleteVideo(FragmentedMp4(mp4,2,0)),"zero-sample MP4 fragments are not saved as video");
   check(!Mori.WebMediaFormat.HasCompleteVideo(null)&&!Mori.WebMediaFormat.HasCompleteVideo(new byte[16]),"null and non-media data are not a video");
  }
  // Synthetic container-layout variants of the real fixture, not claims that
  // their reused chunk offsets form a decoder/conformance test.
  static byte[] FragmentedMp4(byte[] original,byte track,byte samples){
   var bytes=CountZero(original);var fragment=new byte[]{
    0,0,0,48,109,111,111,102,0,0,0,40,116,114,97,102,
    0,0,0,16,116,102,104,100,0,0,0,0,0,0,0,track,
    0,0,0,16,116,114,117,110,0,0,0,0,0,0,0,samples};
   return bytes.Concat(fragment).ToArray();
  }
#if WEB_MEDIA_FORMAT_TESTS
  public static int Main(){int failures=0;Run((ok,message)=>{Console.WriteLine((ok?"PASS ":"FAIL ")+message);if(!ok)failures++;});return failures==0?0:1;}
#endif
 }
}
