using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Windows;
using System.Windows.Controls;

namespace MoriDesk {
 internal static class PreviewBundleTests {
  internal static void Run(Action<bool,string> check){
   string root=AppDomain.CurrentDomain.BaseDirectory;
   string[] components={"Magick.NET.Core.dll","Magick.NET-Q8-x64.dll","Magick.Native-Q8-x64.dll","UglyToad.PdfPig.dll","UglyToad.PdfPig.Core.dll","UglyToad.PdfPig.DocumentLayoutAnalysis.dll","UglyToad.PdfPig.Fonts.dll","UglyToad.PdfPig.Package.dll","UglyToad.PdfPig.Tokenization.dll","UglyToad.PdfPig.Tokens.dll","Microsoft.Bcl.HashCode.dll"};
   long bytes=0;foreach(string name in components){var file=new FileInfo(Path.Combine(root,name));check(file.Exists,"portable preview dependency is included: "+name);bytes+=file.Length;}
   check(bytes<34L*1024*1024,"lightweight image/PDF component files remain below 34 MiB, without duplicate framework/architecture DLLs");
   foreach(string name in new[]{"image-codecs/LICENSE.txt","image-codecs/Notice.txt","image-codecs/test-fixtures-COPYING.txt","image-codecs/test-fixtures-NOTICE.txt","pdf-text/LICENSE.txt","pdf-text/NOTICES.txt","preview-support/LICENSE.txt","PREVIEW-NOTICES.md"})check(File.Exists(Path.Combine(root,name)),"bundled preview license included: "+name);
   foreach(string name in new[]{"libvlc.dll","libvlccore.dll","python313.dll","Ghostscript.dll","onnxruntime.dll"})check(!File.Exists(Path.Combine(root,name)),"base preview does not bundle unwanted video/OCR/offline engine: "+name);
   check(!Directory.Exists(Path.Combine(root,"pdfjs"))&&!Directory.Exists(Path.Combine(root,"offline-translation")),"base package does not include a second PDF reader or translation models");
   var config=new XmlDocument{XmlResolver=null};config.Load(Path.Combine(root,"Mori.exe.config"));var ns=new XmlNamespaceManager(config.NameTable);ns.AddNamespace("a","urn:schemas-microsoft-com:asm.v1");
   foreach(string name in new[]{"System.Memory","System.Buffers","System.Numerics.Vectors","System.Runtime.CompilerServices.Unsafe"}){
    var redirect=config.SelectSingleNode("/configuration/runtime/a:assemblyBinding/a:dependentAssembly[a:assemblyIdentity/@name='"+name+"']/a:bindingRedirect",ns);
    check(redirect!=null&&redirect.Attributes["newVersion"].Value==AssemblyName.GetAssemblyName(Path.Combine(root,name+".dll")).Version.ToString(),"existing SVG/capture and new PDF dependencies share the deployed binding: "+name);
   }
   foreach(string language in new[]{"zh","en"}){
    var window=new QingJie.WriteTranslationWindow("Selected PDF text",language,false);
    try{
     var input=(TextBox)LogicalTreeHelper.FindLogicalNode(window,"TranslationInput");var target=(Button)LogicalTreeHelper.FindLogicalNode(window,"TranslationTarget");
     check(input.Text=="Selected PDF text"&&Convert.ToString(target.Content).Contains(language=="en"?"英文":"中文"),"PDF translation seeds selected text and explicit target without an automatic request: "+language);
    }finally{window.Close();}
   }
  }
 }
}
