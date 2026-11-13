using System;
using System.IO;
using System.Linq;

namespace MoriDesk {
 // Shared, dependency-free capability catalog for the library, direct-open
 // router, and installer. Classification-only formats must not silently claim
 // an Explorer preview association merely because conversion can produce them.
 internal static class FileFormats {
  internal const string Pictures=".jpg .jpeg .png .gif .bmp .tif .tiff .webp .avif .ico .svg .psd .psb .ai .heic .heif .dng .cr2 .cr3 .nef .arw .orf .rw2 .raf";
  internal const string OtherPictures=".jpe .jfif .dib .hif .jxl .jp2 .j2k .jpf .jpx .tga .icb .vda .vst .ppm .pgm .pbm .pnm .icns .apng .mng .qoi .dds .pcx .cur .xpm .xbm .wbmp .pam .pfm .sgi .rgb .rgba .bw .ras .sun .miff .svgz .eps .epsf .epsi .ps .emf .wmf .odg";
  internal const string Videos=".mp4 .mov .m4v .webm .avi .mkv .wmv .mpg .mpeg .m2ts .mts .ts .3gp .ogv .flv .f4v .3g2 .vob .dv .mxf";
  internal const string Audio=".mp3 .wav .m4a .aac .flac .ogg .opus .wma .aiff .aif .m4b .ac3 .amr .au .caf .weba .mka .mp2 .voc .wv .tta .w64 .eac3";
  internal const string PlainDocuments=".txt .md .markdown .log .json .xml .yaml .yml .ini .csv .tsv";
  internal const string PackageDocuments=".docx .xlsx .pptx .odt .ods .odp";
  internal const string ContentDocuments=PlainDocuments+" "+PackageDocuments;
  internal const string Documents=ContentDocuments+" .pdf";
  internal const string Models=".stp .step .stl .obj .glb .3mf";
  internal static string[] PreviewExtensions(){
   // Do not Distinct here: a duplicate or overlapping capability is a catalog
   // defect and the regression suite should expose it rather than conceal it.
   return (Pictures+" "+Models+" "+Videos+" "+Audio+" "+Documents).Split(' ').OrderBy(x=>x,StringComparer.Ordinal).ToArray();
  }
  internal static string MediaMimeType(string pathOrExtension){
   if(string.IsNullOrEmpty(pathOrExtension))return "application/octet-stream";
   string extension=Path.GetExtension(pathOrExtension).ToLowerInvariant();
   // Container metadata, not a claim that the installed decoder supports each
   // codec. Standard/common types follow mime-db v1.54.0; WavPack/VOC/TTA
   // aliases follow the FFmpeg n8.0 muxers. Never mislabel unknown data as MP4.
   switch(extension){
    case ".mp4":case ".m4v":return "video/mp4";
    case ".mov":return "video/quicktime";
    case ".webm":return "video/webm";
    case ".avi":return "video/x-msvideo";
    case ".mkv":return "video/x-matroska";
    case ".wmv":return "video/x-ms-wmv";
    case ".mpg":case ".mpeg":return "video/mpeg";
    case ".m2ts":case ".mts":case ".ts":return "video/mp2t";
    case ".3gp":return "video/3gpp";
    case ".ogv":return "video/ogg";
    case ".flv":return "video/x-flv";
    case ".f4v":return "video/x-f4v";
    case ".3g2":return "video/3gpp2";
    case ".vob":return "video/x-ms-vob";
    case ".dv":return "video/dv";
    case ".mxf":return "application/mxf";
    case ".mp3":case ".mp2":return "audio/mpeg";
    case ".wav":return "audio/wav";
    case ".m4a":case ".m4b":return "audio/mp4";
    case ".aac":return "audio/aac";
    case ".flac":return "audio/flac";
    case ".ogg":case ".opus":return "audio/ogg";
    case ".wma":return "audio/x-ms-wma";
    case ".aiff":case ".aif":return "audio/x-aiff";
    case ".ac3":return "audio/ac3";
    case ".amr":return "audio/amr";
    case ".au":return "audio/basic";
    case ".caf":return "audio/x-caf";
    case ".weba":return "audio/webm";
    case ".mka":return "audio/x-matroska";
    case ".voc":return "audio/x-voc";
    case ".wv":return "audio/x-wavpack";
    case ".tta":return "audio/x-tta";
    case ".w64":return "audio/x-w64";
    case ".eac3":return "audio/eac3";
    default:return "application/octet-stream";
   }
  }
 }
}
