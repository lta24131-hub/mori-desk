using MoriDesk;

namespace Mori {
 public partial class MainWindow {
  InstalledTaskbarIdentity taskbarIdentity;
  void InitializeTaskbarIdentity(){taskbarIdentity=new InstalledTaskbarIdentity(Window,()=>PreviewMode);}
  // A transient shell/property-store failure may recover when services start;
  // update candidates normally already bind to the launcher before rendering.
  void RefreshTaskbarIdentity(){if(taskbarIdentity!=null)taskbarIdentity.Refresh();}
 }
}
