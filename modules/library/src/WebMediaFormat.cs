using System;
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
 }
}
