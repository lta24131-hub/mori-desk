using System;
using System.Collections.Generic;
using System.Text;

namespace Mori {
 // Detect containers from bytes, never from a website-controlled URL or MIME.
 internal static class WebMediaFormat {
  static long VInt(byte[] b,ref int at,int limit,bool id){
   if(at>=limit||b[at]==0)return -1;int count=1,mask=128;while((b[at]&mask)==0){count++;mask>>=1;}
   if(count>(id?4:8)||at+count>limit)return -1;long value=id?b[at]:(b[at]&(mask-1));at++;for(int i=1;i<count;i++)value=(value<<8)|b[at++];return value;
  }
  internal static string DetectVideo(byte[] b){
   if(b==null||b.Length<16)return null;
   if(b[0]==0x1a&&b[1]==0x45&&b[2]==0xdf&&b[3]==0xa3){
    int at=4;long size=VInt(b,ref at,b.Length,false);if(size<0||size>4096||size>b.Length-at)return null;int end=at+(int)size;
    while(at<end){long id=VInt(b,ref at,end,true),length=VInt(b,ref at,end,false);if(id<0||length<0||length>end-at)return null;
     if(id==0x4282)return length==4&&Encoding.ASCII.GetString(b,at,4)=="webm"?".webm":null;at+=(int)length;
    }return null;
   }
   if(Encoding.ASCII.GetString(b,4,4)=="ftyp"){
    long size=((long)b[0]<<24)|((long)b[1]<<16)|((long)b[2]<<8)|b[3];if(size<16||size>4096||size>b.Length||size%4!=0)return null;
    string brand=Encoding.ASCII.GetString(b,8,4);
    // Do not mistake AVIF / HEIF's shared ISO container for an MP4 video.
    if(brand=="avif"||brand=="avis"||brand=="heic"||brand=="heix"||brand=="mif1"||brand=="msf1")return null;
    if(brand=="qt  ")return ".mov";
    if(brand=="isom"||brand=="iso2"||brand=="iso5"||brand=="iso6"||brand=="mp41"||brand=="mp42"||brand=="avc1"||brand=="M4V ")return ".mp4";
   }return null;
  }
  // Recognition above intentionally remains separate from validation: callers
  // can report an opposite-kind response even when only its header arrived.
  // This checks container structure, not codec support or decoded frame quality.
  internal static bool HasCompleteVideo(byte[] bytes){
   string extension=DetectVideo(bytes);if(extension==null)return false;
   var reader=new ContainerReader(bytes);return extension==".webm"?reader.WebM():reader.Mp4();
  }
  sealed class ContainerReader {
   // Explicit, nonrecursive hierarchy: at most six MP4 or four WebM container
   // levels. Unknown metadata stays opaque; no unbounded recursive descent.
   readonly byte[] bytes;int operations=1000000;
   struct Part {internal ulong Id;internal int Data,End;internal bool Unknown;}
   sealed class Track {internal uint Id;internal bool Video,Description;internal long Samples,SampleBytes;}
   readonly List<Track> tracks=new List<Track>();
   readonly HashSet<uint> fragmentTracks=new HashSet<uint>();
   readonly HashSet<ulong> webVideoTracks=new HashSet<ulong>(),webBlockTracks=new HashSet<ulong>(),webTracks=new HashSet<ulong>();
   internal ContainerReader(byte[] data){bytes=data;}
   bool Step(){return --operations>=0;}
   uint U32(int at){return ((uint)bytes[at]<<24)|((uint)bytes[at+1]<<16)|((uint)bytes[at+2]<<8)|bytes[at+3];}
   ulong UInt(int at,int count){ulong value=0;for(int n=0;n<count;n++)value=(value<<8)|bytes[at+n];return value;}
   bool Box(ref int at,int end,out Part box){
    box=new Part();if(!Step()||end-at<8)return false;int start=at,header=8;ulong size=U32(at);box.Id=U32(at+4);
    if(size==1){if(end-at<16)return false;size=UInt(at+8,8);header=16;}
    else if(size==0)size=(ulong)(end-at);
    if(box.Id==0x75756964)header+=16; // uuid adds a 16-byte user type.
    if(size<(ulong)header||size>(ulong)(end-start))return false;
    box.Data=start+header;box.End=start+(int)size;at=box.End;return true;
   }
   internal bool Mp4(){
    int at=0;long mediaBytes=0;bool movie=false;
    while(at<bytes.Length){Part box;if(!Box(ref at,bytes.Length,out box))return false;
     if(box.Id==0x6d646174)mediaBytes+=box.End-box.Data; // mdat
     else if(box.Id==0x6d6f6f76){if(movie||!Movie(box))return false;movie=true;}
     else if(box.Id==0x6d6f6f66&&!Fragment(box))return false; // moof
    }
    if(!movie||mediaBytes<=0)return false;
    foreach(var track in tracks)if(track.Video&&track.Description&&
     ((track.Samples>0&&track.SampleBytes>0&&track.SampleBytes<=mediaBytes)||fragmentTracks.Contains(track.Id)))return true;
    return false;
   }
   bool Movie(Part parent){
    int at=parent.Data;while(at<parent.End){Part box;if(!Box(ref at,parent.End,out box))return false;
     if(box.Id==0x7472616b){if(tracks.Count>=256)return false;var track=new Track();if(!Mp4Track(box,track))return false;tracks.Add(track);}
    }return true;
   }
   bool Mp4Track(Part parent,Track track){
    bool header=false,media=false;int at=parent.Data;
    while(at<parent.End){Part box;if(!Box(ref at,parent.End,out box))return false;
     if(box.Id==0x746b6864){ // tkhd: version changes the track-ID offset.
      if(header||box.End-box.Data<4||bytes[box.Data]>1)return false;int offset=bytes[box.Data]==1?20:12;
      if(box.End-box.Data<offset+4)return false;track.Id=U32(box.Data+offset);header=true;
     }else if(box.Id==0x6d646961){if(media||!Media(box,track))return false;media=true;}
    }return header&&media&&track.Id!=0;
   }
   bool Media(Part parent,Track track){
    bool handler=false,information=false;int at=parent.Data;
    while(at<parent.End){Part box;if(!Box(ref at,parent.End,out box))return false;
     if(box.Id==0x68646c72){if(handler||box.End-box.Data<24)return false;track.Video=U32(box.Data+8)==0x76696465;handler=true;}
     else if(box.Id==0x6d696e66){if(information||!MediaInformation(box,track))return false;information=true;}
    }return handler&&information;
   }
   bool MediaInformation(Part parent,Track track){
    bool table=false;int at=parent.Data;while(at<parent.End){Part box;if(!Box(ref at,parent.End,out box))return false;
     if(box.Id==0x7374626c){if(table||!SampleTable(box,track))return false;table=true;}
    }return table;
   }
   bool SampleTable(Part parent,Track track){
    bool description=false,sizes=false;int at=parent.Data;
    while(at<parent.End){Part box;if(!Box(ref at,parent.End,out box))return false;int length=box.End-box.Data;
     if(box.Id==0x73747364){ // stsd entries have their own bounded box headers.
      if(description||length<8)return false;uint count=U32(box.Data+4);if(count==0||count>256)return false;int entryAt=box.Data+8;
      for(uint n=0;n<count;n++){Part entry;if(!Box(ref entryAt,box.End,out entry))return false;
       // VisualSampleEntry is 78 bytes before codec-specific child boxes. The
       // handler may occur after minf; defer video-only requirements to Mp4().
       if(entry.End-entry.Data>=78)track.Description=true;
      }if(entryAt!=box.End)return false;description=true;
     }else if(box.Id==0x7374737a||box.Id==0x73747a32){ // stsz / stz2
      if(sizes||length<12)return false;uint count=U32(box.Data+8);if(count>1000000)return false;long total=0;
      if(box.Id==0x7374737a){uint fixedSize=U32(box.Data+4);
       if(fixedSize==0){if(count>(uint)((length-12)/4)||12L+count*4L!=length)return false;
        for(uint n=0;n<count;n++){if(!Step())return false;total+=U32(box.Data+12+(int)n*4);}
       }else{if(length!=12)return false;total=(long)fixedSize*count;}
      }else{int field=bytes[box.Data+7];if(field!=4&&field!=8&&field!=16)return false;
       if(12L+((long)count*field+7)/8!=length)return false;
       for(uint n=0;n<count;n++){if(!Step())return false;int position=box.Data+12+(int)((long)n*field/8);
        total+=field==4?((n%2==0?bytes[position]>>4:bytes[position]&15)):field==8?bytes[position]:(long)UInt(position,2);
       }
      }track.Samples=count;track.SampleBytes=total;sizes=true;
     }
    }return description;
   }
   bool Fragment(Part parent){
    int at=parent.Data;while(at<parent.End){Part box;if(!Box(ref at,parent.End,out box))return false;
     if(box.Id==0x74726166&&!TrackFragment(box))return false;
    }return true;
   }
   bool TrackFragment(Part parent){
    int at=parent.Data;uint track=0;bool samples=false,header=false;
    while(at<parent.End){Part box;if(!Box(ref at,parent.End,out box))return false;int length=box.End-box.Data;
     if(box.Id==0x74666864){if(header||length<8)return false;uint flags=U32(box.Data)&0xffffff;
      int expected=8+((flags&1)!=0?8:0)+((flags&2)!=0?4:0)+((flags&8)!=0?4:0)+((flags&16)!=0?4:0)+((flags&32)!=0?4:0);
      if(length!=expected)return false;track=U32(box.Data+4);header=true;
     }else if(box.Id==0x7472756e){if(length<8||bytes[box.Data]>1)return false;uint flags=U32(box.Data)&0xffffff,count=U32(box.Data+4);
      int headerSize=8+((flags&1)!=0?4:0)+((flags&4)!=0?4:0),sampleSize=((flags&256)!=0?4:0)+((flags&512)!=0?4:0)+((flags&1024)!=0?4:0)+((flags&2048)!=0?4:0);
      if(count>1000000||headerSize+(long)count*sampleSize!=length)return false;if(count>0)samples=true;
     }
    }if(!header||track==0)return false;if(samples)fragmentTracks.Add(track);return true;
   }
   bool Variable(ref int at,int end,bool id,out ulong value,out bool unknown,out int count){
    value=0;unknown=false;count=1;if(at>=end||bytes[at]==0)return false;int mask=128;
    while((bytes[at]&mask)==0){count++;mask>>=1;}
    if(count>(id?4:8)||count>end-at)return false;value=(ulong)(id?bytes[at]:bytes[at]&(mask-1));at++;
    for(int n=1;n<count;n++)value=(value<<8)|bytes[at++];unknown=!id&&value==((1UL<<(count*7))-1);return true;
   }
   bool Element(ref int at,int end,out Part element){
    element=new Part();if(!Step())return false;ulong id,size;bool unused,unknown;int count;
    if(!Variable(ref at,end,true,out id,out unused,out count)||!Variable(ref at,end,false,out size,out unknown,out count))return false;
    if(!unknown&&size>(ulong)(end-at))return false;element.Id=id;element.Data=at;element.Unknown=unknown;element.End=unknown?end:at+(int)size;at=element.End;return true;
   }
   bool FiniteChildren(Part parent){int at=parent.Data;while(at<parent.End){Part child;if(!Element(ref at,parent.End,out child)||child.Unknown)return false;}return true;}
   internal bool WebM(){
    int at=0;Part header;if(!Element(ref at,bytes.Length,out header)||header.Id!=0x1a45dfa3||header.Unknown||!FiniteChildren(header))return false;
    bool segment=false;while(at<bytes.Length){Part part;if(!Element(ref at,bytes.Length,out part))return false;
     if(part.Id==0x18538067){if(segment||!Segment(part))return false;segment=true;}
     else if(part.Unknown||part.Id!=0xec&&part.Id!=0xbf)return false;
    }
    if(!segment)return false;foreach(ulong track in webBlockTracks)if(webVideoTracks.Contains(track))return true;return false;
   }
   bool Segment(Part parent){
    int at=parent.Data;while(at<parent.End){int start=at;Part part;if(!Element(ref at,parent.End,out part))return false;
     if(part.Id==0x1654ae6b){if(part.Unknown||!WebTracks(part))return false;}
     else if(part.Id==0x1f43b675){int next;if(!Cluster(part,out next))return false;at=next;}
     else if(part.Unknown)return false;
     if(at<=start)return false;
    }return true;
   }
   bool WebTracks(Part parent){
    int at=parent.Data;while(at<parent.End){Part part;if(!Element(ref at,parent.End,out part)||part.Unknown)return false;
     if(part.Id==0xae&&!WebTrack(part))return false;
    }return true;
   }
   bool WebTrack(Part parent){
    ulong number=0,type=0;bool codec=false,video=false;int at=parent.Data;
    while(at<parent.End){Part part;if(!Element(ref at,parent.End,out part)||part.Unknown)return false;int length=part.End-part.Data;
     if(part.Id==0xd7||part.Id==0x83){if(length<1||length>8)return false;ulong value=UInt(part.Data,length);
      if(part.Id==0xd7){if(number!=0||value==0)return false;number=value;}else{if(type!=0||value==0)return false;type=value;}
     }else if(part.Id==0x86){if(codec||length<1||length>256)return false;for(int n=part.Data;n<part.End;n++)if(bytes[n]<33||bytes[n]>126)return false;codec=true;}
     else if(part.Id==0xe0){if(video||!WebVideo(part))return false;video=true;}
    }
    if(number==0||type==0||!codec||webTracks.Count>=256||!webTracks.Add(number))return false;
    if(type==1&&video)webVideoTracks.Add(number);return true;
   }
   bool WebVideo(Part parent){
    bool width=false,height=false;int at=parent.Data;while(at<parent.End){Part part;if(!Element(ref at,parent.End,out part)||part.Unknown)return false;
     if(part.Id==0xb0||part.Id==0xba){int length=part.End-part.Data;if(length<1||length>8||UInt(part.Data,length)==0)return false;
      if(part.Id==0xb0){if(width)return false;width=true;}else{if(height)return false;height=true;}
     }
    }return width&&height;
   }
   static bool SegmentLevel(ulong id){return id==0x114d9b74||id==0x1549a966||id==0x1654ae6b||id==0x1f43b675||id==0x1c53bb6b||id==0x1941a469||id==0x1043a770||id==0x1254c367;}
   bool Cluster(Part parent,out int next){
    int at=parent.Data;next=parent.End;
    while(at<parent.End){int start=at;Part part;if(!Element(ref at,parent.End,out part))return false;
     if(SegmentLevel(part.Id)){if(!parent.Unknown)return false;next=start;return true;}
     if(part.Unknown)return false;
     if(part.Id==0xa3){if(!Block(part))return false;}
     else if(part.Id==0xa0){int childAt=part.Data;while(childAt<part.End){Part child;if(!Element(ref childAt,part.End,out child)||child.Unknown)return false;if(child.Id==0xa1&&!Block(child))return false;}}
    }return true;
   }
   bool Block(Part part){
    int at=part.Data,count;ulong track;bool unknown;
    if(!Variable(ref at,part.End,false,out track,out unknown,out count)||unknown||track==0||part.End-at<4)return false;
    int lacing=(bytes[at+2]>>1)&3;at+=3;if(lacing!=0){int frames=bytes[at++]+1;if(frames<2)return false;long used=0,previous=0;
     if(lacing==2){if((part.End-at)%frames!=0||part.End-at<frames)return false;}
     else for(int n=0;n<frames-1;n++){long size;
      if(lacing==1){size=0;int value;do{if(!Step()||at>=part.End)return false;value=bytes[at++];size+=value;}while(value==255);}
      else{ulong value;if(!Variable(ref at,part.End,false,out value,out unknown,out count)||unknown)return false;
       size=n==0?(long)value:previous+(long)value-((1L<<(count*7-1))-1);previous=size;}
      if(size<=0)return false;used+=size;if(used>part.End-at)return false;
     }
     if(lacing!=2&&used>=part.End-at)return false;
    }else if(at>=part.End)return false;
    webBlockTracks.Add(track);return true;
   }
  }
 }
}
