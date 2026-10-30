using System;
using System.Linq;
namespace Mori {
 public partial class MainWindow {
  void ShowImageViewer(Asset current){
   // Follow the current gallery's order and filter, not the storage folder's
   // hashed filenames. Viewing never adds, moves or changes a library item.
   var list=visible.Where(a=>!a.IsDirectory&&!a.IsBookmark&&MediaTypes.Pictures.Contains(a.Ext)&&!FormatPreviewInfo.UsesPagedPreview(library.FullPath(a))).ToList();
   int currentIndex=list.FindIndex(a=>a.Id==current.Id);if(currentIndex<0)list.Insert(0,current);else list[currentIndex]=current;
   var viewer=new MoriDesk.ImageBrowser(library.FullPath(current)){Owner=Window};
   viewer.SetImageSequence(list.Select(a=>library.FullPath(a)).ToArray(),list.Select(a=>a.Name).ToArray());viewer.Show();
  }
 }
}
