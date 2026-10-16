using System;
using System.IO;
using System.Text;
using System.Windows;

namespace MoriDesk {
 internal static class MatureCanvasFixture {
  internal static int Run(string root){
   int checks=0;string log=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"mature-canvas-tests.txt");
   try{
    root=Path.GetFullPath(root);new MatureCanvasStore(root);
    if(Directory.Exists(Path.Combine(root,"store"))||Directory.Exists(Path.Combine(root,"host")))throw new IOException("Use a fresh fixture directory.");
    Directory.CreateDirectory(root);var app=Application.Current??new Application();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
    Action<bool,string> check=(ok,message)=>{checks++;if(!ok)throw new Exception(message);};
    MatureCanvasStoreTests.Run(Path.Combine(root,"store"),check);
    MatureCanvasHostTests.Run(Path.Combine(root,"host"),check);
    MatureCanvasInteractionTests.Run(Path.Combine(root,"interaction"),check);
    CustomizationTests.Run(Path.Combine(root,"customization"),check);
    CanvasDestinationTests.Run(Path.Combine(root,"destinations"),check);
    CanvasProjectGalleryTests.Run(Path.Combine(root,"gallery"),check);
    CanvasFallbackSafetyTests.Run(Path.Combine(root,"fallback-safety"),check);
    CanvasProjectRefreshTests.Run(Path.Combine(root,"project-refresh"),check);
    File.WriteAllText(log,"PASS "+checks+" checks\r\nFixtures: "+root,Encoding.UTF8);return 0;
   }catch(Exception error){File.WriteAllText(log,"FAIL after "+checks+" checks\r\n"+error+"\r\nFixtures: "+root,Encoding.UTF8);return 1;}
  }
 }
}
