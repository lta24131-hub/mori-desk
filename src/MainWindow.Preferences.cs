using System;
namespace Mori {
 public partial class MainWindow {
  internal static bool ShouldCloseToTray(bool hasTray,bool explicitExit,Settings settings){return hasTray&&!explicitExit&&settings.CloseToTray;}
  internal static bool ShouldCloseToTray(bool hasTray,bool explicitExit,Func<Settings> read,bool fallback){
   if(!hasTray||explicitExit)return false;
   try{return read().CloseToTray;}catch{return fallback;}
  }
 }
}
