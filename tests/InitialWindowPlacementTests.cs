using System;
using System.Windows;
namespace MoriDesk {
 internal static class InitialWindowPlacementTests {
  internal static void Run(Action<bool,string> check) {
   foreach(var work in new[]{new Rect(0,0,1024,728),new Rect(0,0,1536,824),new Rect(-1920,-200,1920,1040),new Rect(1920,0,960,650)}){
    foreach(var preferred in new[]{new Size(1380,900),new Size(1220,820),new Size(1180,820),new Size(1100,790),new Size(1200,850),new Size(1060,790),new Size(1100,800)}){
     var bounds=InitialWindowPlacement.Fit(work,preferred.Width,preferred.Height);
     check(work.Contains(bounds),"initial library/canvas/viewer window remains inside the complete monitor work area: "+work+" preferred "+preferred);
     check(bounds.Width<=preferred.Width&&bounds.Height<=preferred.Height&&bounds.Width>0&&bounds.Height>0,"screen fitting never enlarges or invalidates the preferred dimensions");
     check(Math.Abs((bounds.Left+bounds.Width/2)-(work.Left+work.Width/2))<.01&&Math.Abs((bounds.Top+bounds.Height/2)-(work.Top+work.Height/2))<.01,"initial window centers on the selected monitor, including negative origins");
    }
   }
   var normal=InitialWindowPlacement.Fit(new Rect(0,0,1920,1040),1380,900);
   check(normal.Width==1380&&normal.Height==900,"ordinary desktop retains the original preferred window dimensions");
  }
 }
}
