namespace Mori {
 public partial class MainWindow {
  bool metadataLoaded,metadataReloadRequired;
  // Mutations, explicit refresh and external changes keep the existing entry
  // point. Mark invalidation before the draft guard, so a postponed import is
  // still loaded when the user resumes with a search-only refresh.
  void Refresh(bool inspector){metadataReloadRequired=true;RefreshCore(inspector);}
  internal void RefreshView(){RefreshCore(false);}
  void LoadMetadataForRefresh(){
   if(metadataLoaded&&!metadataReloadRequired)return;
   var loaded=library.All();all=loaded;metadataLoaded=true;metadataReloadRequired=false;
  }
 }
}
