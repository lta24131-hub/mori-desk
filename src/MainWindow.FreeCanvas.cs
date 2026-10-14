using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  FreeCanvasWindow freeCanvas;
  internal bool FreeCanvasPresentationSuppressedForTests;
  internal Task FreeCanvasImportForTests=Task.FromResult(false);
  void OpenFreeCanvas(string[] paths){
   if(busy||!PrepareLibraryChange())return;
   try{
    if(freeCanvas==null){
     var canvas=new FreeCanvasWindow(Entry.DataRoot);freeCanvas=canvas;canvas.Closed+=delegate{if(freeCanvas==canvas)freeCanvas=null;};
     // No Owner: the reference board remains visible when the library is in its tray.
     if(!FreeCanvasPresentationSuppressedForTests)canvas.Show();
    }
    if(paths!=null&&paths.Length>0){
     if(freeCanvas.CanImportImages)FreeCanvasImportForTests=ImportCanvasSelection(freeCanvas,paths);
     else Status("画布仍在保存上一批图片，请稍后再添加。");
    }
    if(!FreeCanvasPresentationSuppressedForTests){if(freeCanvas.WindowState==WindowState.Minimized)freeCanvas.WindowState=WindowState.Normal;freeCanvas.Show();freeCanvas.Activate();}
   }catch(Exception error){Status("无法打开自由画布："+error.Message);}
  }
  async Task ImportCanvasSelection(FreeCanvasWindow canvas,string[] paths){
   try{int before=canvas.ItemCount;await canvas.ImportImagesAsync(paths,null);if(canvas.CanImportImages&&canvas.ItemCount>before)canvas.FitAddedImages();}
   catch(Exception error){Status("图片未能全部加入画布："+error.Message);}
  }
  void AddSelectionToFreeCanvas(){
   var items=Selected();if(busy||items.Count==0||view=="trash"||!items.All(a=>SectionContains(a,"pictures")))return;
   var paths=items.Select(ResolveReferenceForOpen).Where(a=>a!=null).Select(library.FullPath).ToArray();if(paths.Length>0)OpenFreeCanvas(paths);
  }
  void CloseFreeCanvasForExit(object sender,System.ComponentModel.CancelEventArgs e){
   if(e.Cancel||freeCanvas==null)return;
   freeCanvas.Close();if(freeCanvas!=null&&freeCanvas.IsVisible)e.Cancel=true;
  }
 }
}
