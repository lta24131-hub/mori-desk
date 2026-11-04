using System;
using System.IO;
using System.Linq;
using System.Text;
using Mori;
namespace MoriDesk {
 public static class SuiteTests {
  static int checks;
  static StreamWriter progress;
  static void Stage(string name){if(progress!=null)progress.WriteLine(DateTime.UtcNow.ToString("O")+" START "+name+" after "+checks+" checks");}
  static void Check(bool ok,string message){checks++;if(progress!=null)progress.WriteLine((ok?"PASS ":"FAIL ")+checks+" "+message);if(!ok)throw new Exception(message);}
  static void Reject(Action action,string message){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,message);}
  public static int Run(){checks=0;string root=Path.Combine(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).FullName,"fixtures",Guid.NewGuid().ToString("N").Substring(0,12));Directory.CreateDirectory(root);string log=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"suite-tests.txt");progress=new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"suite-progress.txt"),false,Encoding.UTF8){AutoFlush=true};try{
   string source=Path.Combine(root,"原始文件");Directory.CreateDirectory(source);string a=Path.Combine(source,"装配.step"),b=Path.Combine(source,"图纸'B.stl");File.WriteAllText(a,"REFERENCE FILE CONTENT; NEVER COPY",Encoding.UTF8);File.WriteAllText(b,"solid test\nendsolid test");var time=File.GetLastWriteTimeUtc(a);byte[] bytes=File.ReadAllBytes(a);string library=Path.Combine(root,"library");
   using(var lib=new Library(library)){
    var refs=new ReferenceStore(lib.Db);Check(refs.All().Count==0,"empty store");string f=refs.AddFolder("项目 A"),g=refs.AddFolder("另一个项目");Check(refs.Folders().Count==2,"create folders");Check(refs.Add(new[]{a,b},f)==2,"batch add");Check(refs.Add(new[]{a.ToUpperInvariant()},g)==0,"case-insensitive duplicate");Check(refs.All().All(r=>r.Folder==f),"duplicate doesn't move silently");Check(lib.All().Count==0,"refs aren't library assets");Check(Directory.GetFiles(Path.Combine(library,"原图"),"*",SearchOption.AllDirectories).Length==0,"no copies created");var first=refs.All().First(r=>r.Path==a);Check(first.Exists&&!first.Directory&&first.Kind=="STEP","file metadata");
    refs.Move(refs.All().Select(r=>r.Id),g);Check(refs.All().All(r=>r.Folder==g),"batch classify");Check(refs.Undo()&&refs.All().All(r=>r.Folder==f),"undo classify");refs.RenameFolder(f,"新项目");Check(refs.Folders().Any(r=>r.Name=="新项目"),"rename folder");refs.DeleteFolder(f);Check(refs.All().Count==2&&refs.All().All(r=>r.Folder==""),"delete folder unfiles references");Check(refs.Undo()&&refs.All().All(r=>r.Folder==f),"undo folder deletion");
    refs.Remove(refs.All().Select(r=>r.Id));Check(refs.All().Count==0,"batch remove");Check(File.Exists(a)&&File.Exists(b),"remove preserves originals");Check(refs.Undo()&&refs.All().Count==2,"undo remove");Check(refs.Add(new[]{source},"")==1,"folder reference");Check(refs.All().Count==3,"folder not recursively indexed");Check(refs.All().Single(r=>r.Directory).Exists,"folder exists");
    string moved=Path.Combine(source,"重新定位.step");File.Move(a,moved);Check(!first.Exists,"missing reference");refs.Relink(first.Id,moved);Check(refs.All().Single(r=>r.Id==first.Id).Exists,"relink");Check(refs.Undo()&&!refs.All().Single(r=>r.Id==first.Id).Exists,"undo relink");refs.Relink(first.Id,moved);Reject(()=>refs.Relink(first.Id,b),"relink collision rejected");Check(refs.All().Single(r=>r.Id==first.Id).Path==moved,"relink rollback");
    Reject(()=>refs.Add(new[]{b,Path.Combine(root,"missing.step")},""),"invalid batch rejected");Check(refs.All().Count==3,"invalid batch no partial result");Reject(()=>refs.Move(new[]{first.Id},"missing-category"),"invalid category rejected");Reject(()=>refs.AddFolder(" "),"blank folder rejected");Reject(()=>refs.AddFolder("新项目"),"duplicate folder rejected");Reject(()=>ReferenceStore.Normalize("relative.step"),"relative path rejected");Check(ReferenceStore.Normalize(source+"\\")==source,"normalize separator");Check(new ReferenceStore(lib.Db).All().Count==3,"persistence");Check(File.ReadAllBytes(moved).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(moved)==time,"original bytes and timestamp unchanged");
    string backup=Path.Combine(root,"test.mori.zip");lib.Backup(backup);Check(File.Exists(backup),"backup covers reference DB");
   }
   Check(Settings.ConfigDir.EndsWith("MoriDesk"),"independent settings");Check(new Settings().LibraryPath.StartsWith(Settings.ConfigDir),"independent library");Check(QingJie.OcrLanguagePacks.Folder.StartsWith(Settings.ConfigDir),"independent languages");Check(QingJie.OfflinePacks.Folder.StartsWith(Settings.ConfigDir),"independent offline packs");Check(StartupRegistration.ValueName=="MoriDesk","independent startup");foreach(string ext in FileRouter.Models)Check(FileRouter.Kind("test"+ext)=="model","route "+ext);foreach(string ext in FileRouter.Pictures)Check(FileRouter.Kind("test"+ext)=="image","route "+ext);Check(FileRouter.Kind("document.pdf")=="preview","built-in PDF route");
   var testApp=System.Windows.Application.Current??new System.Windows.Application();testApp.ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown;
   Stage("IterationTests.Run");IterationTests.Run(root,Check);Stage("BrowserLocalBridgeTests.Run");BrowserLocalBridgeTests.Run(root,Check);Stage("BrowserExtensionManagerTests.Run");BrowserExtensionManagerTests.Run(root,Check);Stage("ScreenshotCollectionTests.Run");ScreenshotCollectionTests.Run(root,Check);Stage("LaunchTests.Run");LaunchTests.Run(root,Check);Stage("ReferenceTests.Run");ReferenceTests.Run(root,Check);Stage("LibraryConsistencyTests.Run");LibraryConsistencyTests.Run(root,Check);Stage("ReferenceThumbnailFreshnessTests.Run");ReferenceThumbnailFreshnessTests.Run(root,Check);Stage("MetadataRefreshCacheTests.Run");MetadataRefreshCacheTests.Run(root,Check);Stage("InspectorDraftTests.Run");InspectorDraftTests.Run(root,Check);Stage("BoardTests.Run");BoardTests.Run(root,Check);Stage("ClassicTests.Run");ClassicTests.Run(root,Check);Stage("LocalCollectionTests.Run");LocalCollectionTests.Run(root,Check);Stage("OverviewVisibilityTests.Run");OverviewVisibilityTests.Run(root,Check);Stage("DesktopDropTests.Run");DesktopDropTests.Run(root,Check);Stage("DeskSettingsTests.Run");DeskSettingsTests.Run(root,Check);
   Stage("LibraryRestoreTests.Run");LibraryRestoreTests.Run(root,Check);
   Stage("InitialWindowPlacementTests.Run");InitialWindowPlacementTests.Run(Check);
   Stage("InspectorConcurrentMetadataTests.Run");InspectorConcurrentMetadataTests.Run(Path.Combine(root,"inspector-concurrent"),Check);
   Stage("ImageWorkRegressionTests.Run");ImageWorkRegressionTests.Run(Path.Combine(root,"image-work-regressions"),Check);
   Stage("BrandingTests.Run");BrandingTests.Run(Path.Combine(root,"branding"),Check);
   Stage("AppUpdateServiceTests.Run");AppUpdateServiceTests.Run(Path.Combine(root,"app-update-service"),Check);
   Stage("AppUpdatePanelTests.Run");AppUpdatePanelTests.Run(Path.Combine(root,"app-update-panel"),Check);
   Stage("AppUpdateCoordinatorTests.Run");AppUpdateCoordinatorTests.Run(Path.Combine(root,"app-update-coordinator"),Check);
   Stage("InstalledVersionMaintenanceTests.Run");InstalledVersionMaintenanceTests.Run(Check);
   Stage("UpdateArtifactMaintenanceTests.Run");UpdateArtifactMaintenanceTests.Run(Path.Combine(root,"update-artifacts"),Check);
   Stage("InstalledTaskbarIdentityTests.Run");InstalledTaskbarIdentityTests.Run(Path.Combine(root,"installed-taskbar"),Check);
   Stage("ReferenceEagleMetadataTests.Run");ReferenceEagleMetadataTests.Run(Path.Combine(root,"reference-eagle"),Check);
   Stage("ExportNamingTests.Run");ExportNamingTests.Run(Path.Combine(root,"export-naming"),Check);
   Stage("ImageBrowserLifecycleTests.Run");ImageBrowserLifecycleTests.Run(Path.Combine(root,"image-browser-lifecycle"),Check);
   Stage("ImageViewerExperienceTests.Run");ImageViewerExperienceTests.Run(Path.Combine(root,"image-viewer-experience"),Check);
   Stage("ImageViewerDetailTests.Run");ImageViewerDetailTests.Run(Path.Combine(root,"image-viewer-details"),Check);
   Stage("GalleryNavigationTests.Run");GalleryNavigationTests.Run(Path.Combine(root,"gallery-navigation"),Check);
   Stage("HomepageUsabilityTests.Run");HomepageUsabilityTests.Run(root,Check);
   Stage("GallerySelectionStabilityTests.Run");GallerySelectionStabilityTests.Run(root,Check);
   Stage("SelectionLookupTests.Run");SelectionLookupTests.Run(root,Check);
   Stage("SvgWorkerProtocolTests.Run");SvgWorkerProtocolTests.Run(Check);
   Stage("HomepageRedesignTests.Run");HomepageRedesignTests.Run(root,Check);
   Stage("OpenWithRegistrationTests.Run");OpenWithRegistrationTests.Run(Path.Combine(root,"open-with"),Check);
   Stage("ThemePaletteTests.Run");ThemePaletteTests.Run(Path.Combine(root,"theme-palette"),Check);
   Stage("EmbeddedThemeTests.Run");EmbeddedThemeTests.Run(Check);
   Stage("CaptureThemeTests.Run");CaptureThemeTests.Run(Path.Combine(root,"capture-theme"),Check);
   Stage("BrowseSettingsTests.Run");BrowseSettingsTests.Run(root,Check);Stage("ThumbnailBuildGateTests.Run");ThumbnailBuildGateTests.Run(root,Check);Stage("VideoControlsTests.Run");VideoControlsTests.Run(Check);Stage("MissingReferenceTests.Run");MissingReferenceTests.Run(root,Check);Stage("ScreenColorTests.Run");ScreenColorTests.Run(Check);Stage("MediaBadgeTests.Run");MediaBadgeTests.Run(Path.Combine(root,"media-badges"),Check);
   Stage("DocumentContentTests.Run");DocumentContentTests.Run(Path.Combine(root,"document-content"),Check);Stage("PsdCompositeTests.Run");PsdCompositeTests.Run(Path.Combine(root,"psd-composite"),Check);Stage("PsdEmbeddedThumbnailTests.Run");PsdEmbeddedThumbnailTests.Run(Path.Combine(root,"psd-embedded"),Check);Stage("PdfPreviewTests.Run");PdfPreviewTests.Run(Path.Combine(root,"pdf-preview"),Check);Stage("FormatPreviewTests.Run");FormatPreviewTests.Run(Path.Combine(root,"format-preview"),Check);Stage("PdfContinuousTests.Run");PdfContinuousTests.Run(Path.Combine(root,"pdf-continuous"),Check);
   Stage("PreviewBundleTests.Run");PreviewBundleTests.Run(Check);Stage("ImageCodecTests.Run");ImageCodecTests.Run(Path.Combine(root,"image-codec"),Check);Stage("PdfTextTests.Run");PdfTextTests.Run(Path.Combine(root,"pdf-text"),Check);Stage("PdfIsolationTests.Run");PdfIsolationTests.Run(Path.Combine(root,"pdf-isolation"),Check);
   Stage("WallpaperTests.Run");WallpaperTests.Run(Path.Combine(root,"wallpaper"),Check);
   Stage("SimpleBrowserTests.Run");SimpleBrowserTests.Run(Check);Stage("BrowserWorkspaceTests.Run");BrowserWorkspaceTests.Run(Path.Combine(root,"browser-workspace"),Check);
   Stage("FreeCanvasTests.Run");FreeCanvasTests.Run(Path.Combine(root,"free-canvas"),Check);
   Stage("CanvasProjectMemoryTests.Run");CanvasProjectMemoryTests.Run(Path.Combine(root,"canvas-memory"),Check);
   Stage("CanvasToolbarTests.Run");CanvasToolbarTests.Run(Path.Combine(root,"canvas-toolbar"),Check);
   Stage("CanvasProjectsPageTests.Run");CanvasProjectsPageTests.Run(Path.Combine(root,"canvas-projects"),Check);
   Stage("CanvasDestinationTests.Run");CanvasDestinationTests.Run(Path.Combine(root,"canvas-destinations"),Check);
   Stage("CanvasProjectGalleryTests.Run");CanvasProjectGalleryTests.Run(Path.Combine(root,"canvas-gallery"),Check);
   Stage("CanvasFallbackSafetyTests.Run");CanvasFallbackSafetyTests.Run(Path.Combine(root,"canvas-fallback-safety"),Check);
   Stage("CanvasProjectRefreshTests.Run");CanvasProjectRefreshTests.Run(Path.Combine(root,"project-refresh"),Check);
   Stage("GeneralPerformanceTests.Run");GeneralPerformanceTests.Run(Path.Combine(root,"general-performance"),Check);
   Stage("ThumbnailThroughputTests.Scheduling");ThumbnailThroughputTests.Scheduling(Path.Combine(root,"thumbnail-scheduling"),Check);Stage("ThumbnailThroughputTests.Raster");ThumbnailThroughputTests.Raster(Check);
   Stage("WorkerStartupTests.Run");WorkerStartupTests.Run(Path.Combine(root,"worker-startup"),Check);
   Stage("PreviewWorkerSchedulingTests.Run");PreviewWorkerSchedulingTests.Run(Path.Combine(root,"worker-scheduling"),Check);
   Stage("CanvasTextLayoutTests.Run");CanvasTextLayoutTests.Run(Path.Combine(root,"canvas-text-layout"),Check);
   Stage("CanvasLineSelectionTests.Run");CanvasLineSelectionTests.Run(Path.Combine(root,"canvas-line-selection"),Check);
   Stage("CanvasStyleEditingTests.Run");CanvasStyleEditingTests.Run(Path.Combine(root,"canvas-style-editing"),Check);
   Stage("MatureCanvasStoreTests.Run");MatureCanvasStoreTests.Run(Path.Combine(root,"mature-canvas-store"),Check);
   Stage("MatureCanvasImageMetadataTests.Run");MatureCanvasImageMetadataTests.Run(Path.Combine(root,"mature-canvas-image-metadata"),Check);
   Stage("MatureCanvasCloseFailureTests.Run");MatureCanvasCloseFailureTests.Run(Path.Combine(root,"mature-canvas-close-failure"),Check);
   Stage("MatureCanvasHostTests.Run");MatureCanvasHostTests.Run(Path.Combine(root,"mature-canvas-host"),Check);
   Stage("MatureCanvasInteractionTests.Run");MatureCanvasInteractionTests.Run(Path.Combine(root,"mature-canvas-interaction"),Check);
   Stage("AppearanceSettingsTests.Run");AppearanceSettingsTests.Run(Path.Combine(root,"appearance-settings"),Check);
   Stage("CustomizationTests.Run");CustomizationTests.Run(Path.Combine(root,"customization"),Check);
   Stage("PreviewPreferencesTests.Run");PreviewPreferencesTests.Run(Path.Combine(root,"preview-preferences"),Check);
   Stage("DesktopImagePinTests.Run");DesktopImagePinTests.Run(Path.Combine(root,"desktop-image-pins"),Check);
   Stage("PreviewClipboardTests.Run");PreviewClipboardTests.Run(Path.Combine(root,"preview-clipboard"),Check);
   Stage("PinLifecycleTests.Run");PinLifecycleTests.Run(Path.Combine(root,"pin-lifecycle"),Check);
   Stage("PinRuntimeTests.Run");PinRuntimeTests.Run(Path.Combine(root,"pin-runtime"),Check);
   Stage("PinHistoryIndexTests.Run");PinHistoryIndexTests.Run(Path.Combine(root,"pin-history-index"),Check);
   Stage("TextWindowPinOutputTests.Run");TextWindowPinOutputTests.Run(Path.Combine(root,"text-window-pin-output"),Check);
   Stage("MiddleDragPanTests.Run");MiddleDragPanTests.Run(Check);
   File.WriteAllText(log,"PASS "+checks+" checks\r\nFixtures: "+root+"\r\n"+DateTime.Now.ToString("O"),Encoding.UTF8);return 0;
  }catch(Exception ex){var diagnostic=new System.Text.StringBuilder();for(var cause=ex;cause!=null;cause=cause.InnerException)foreach(System.Collections.DictionaryEntry entry in cause.Data)if(Convert.ToString(entry.Key).StartsWith("Nook.Worker.",StringComparison.Ordinal))diagnostic.Append(entry.Key).Append('=').Append(entry.Value).AppendLine();File.WriteAllText(log,"FAIL after "+checks+" checks\r\n"+ex+"\r\n"+diagnostic+"Fixtures: "+root,Encoding.UTF8);return 1;}finally{progress.Dispose();progress=null;}}
 }
}
