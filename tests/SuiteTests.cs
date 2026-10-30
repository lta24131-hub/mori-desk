using System;
using System.IO;
using System.Linq;
using System.Text;
using Mori;
namespace MoriDesk {
 public static class SuiteTests {
  static int checks;
  static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
  static void Reject(Action action,string message){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,message);}
  public static int Run(){string root=Path.Combine(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).FullName,"fixtures",Guid.NewGuid().ToString("N").Substring(0,12));Directory.CreateDirectory(root);string log=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"suite-tests.txt");try{
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
   IterationTests.Run(root,Check);BrowserLocalBridgeTests.Run(root,Check);BrowserExtensionManagerTests.Run(root,Check);ScreenshotCollectionTests.Run(root,Check);LaunchTests.Run(root,Check);ReferenceTests.Run(root,Check);LibraryConsistencyTests.Run(root,Check);ReferenceThumbnailFreshnessTests.Run(root,Check);MetadataRefreshCacheTests.Run(root,Check);InspectorDraftTests.Run(root,Check);BoardTests.Run(root,Check);ClassicTests.Run(root,Check);LocalCollectionTests.Run(root,Check);OverviewVisibilityTests.Run(root,Check);DesktopDropTests.Run(root,Check);DeskSettingsTests.Run(root,Check);
   LibraryRestoreTests.Run(root,Check);
   InitialWindowPlacementTests.Run(Check);
   InspectorConcurrentMetadataTests.Run(Path.Combine(root,"inspector-concurrent"),Check);
   ImageWorkRegressionTests.Run(Path.Combine(root,"image-work-regressions"),Check);
   BrandingTests.Run(Path.Combine(root,"branding"),Check);
   AppUpdateServiceTests.Run(Path.Combine(root,"app-update-service"),Check);
   AppUpdatePanelTests.Run(Path.Combine(root,"app-update-panel"),Check);
   AppUpdateCoordinatorTests.Run(Path.Combine(root,"app-update-coordinator"),Check);
   InstalledVersionMaintenanceTests.Run(Check);
   InstalledTaskbarIdentityTests.Run(Path.Combine(root,"installed-taskbar"),Check);
   ReferenceEagleMetadataTests.Run(Path.Combine(root,"reference-eagle"),Check);
   ExportNamingTests.Run(Path.Combine(root,"export-naming"),Check);
   ImageBrowserLifecycleTests.Run(Path.Combine(root,"image-browser-lifecycle"),Check);
   ImageViewerExperienceTests.Run(Path.Combine(root,"image-viewer-experience"),Check);
   ImageViewerDetailTests.Run(Path.Combine(root,"image-viewer-details"),Check);
   GalleryNavigationTests.Run(Path.Combine(root,"gallery-navigation"),Check);
   HomepageUsabilityTests.Run(root,Check);
   HomepageRedesignTests.Run(root,Check);
   OpenWithRegistrationTests.Run(Path.Combine(root,"open-with"),Check);
   ThemePaletteTests.Run(Path.Combine(root,"theme-palette"),Check);
   EmbeddedThemeTests.Run(Check);
   CaptureThemeTests.Run(Path.Combine(root,"capture-theme"),Check);
   BrowseSettingsTests.Run(root,Check);ThumbnailBuildGateTests.Run(root,Check);VideoControlsTests.Run(Check);MissingReferenceTests.Run(root,Check);ScreenColorTests.Run(Check);MediaBadgeTests.Run(Path.Combine(root,"media-badges"),Check);
   DocumentContentTests.Run(Path.Combine(root,"document-content"),Check);PsdCompositeTests.Run(Path.Combine(root,"psd-composite"),Check);PsdEmbeddedThumbnailTests.Run(Path.Combine(root,"psd-embedded"),Check);PdfPreviewTests.Run(Path.Combine(root,"pdf-preview"),Check);FormatPreviewTests.Run(Path.Combine(root,"format-preview"),Check);PdfContinuousTests.Run(Path.Combine(root,"pdf-continuous"),Check);
   PreviewBundleTests.Run(Check);ImageCodecTests.Run(Path.Combine(root,"image-codec"),Check);PdfTextTests.Run(Path.Combine(root,"pdf-text"),Check);PdfIsolationTests.Run(Path.Combine(root,"pdf-isolation"),Check);
   WallpaperTests.Run(Path.Combine(root,"wallpaper"),Check);
   SimpleBrowserTests.Run(Check);BrowserWorkspaceTests.Run(Path.Combine(root,"browser-workspace"),Check);
   FreeCanvasTests.Run(Path.Combine(root,"free-canvas"),Check);
   CanvasProjectMemoryTests.Run(Path.Combine(root,"canvas-memory"),Check);
   CanvasToolbarTests.Run(Path.Combine(root,"canvas-toolbar"),Check);
   CanvasProjectsPageTests.Run(Path.Combine(root,"canvas-projects"),Check);
   CanvasDestinationTests.Run(Path.Combine(root,"canvas-destinations"),Check);
   CanvasProjectGalleryTests.Run(Path.Combine(root,"canvas-gallery"),Check);
   CanvasFallbackSafetyTests.Run(Path.Combine(root,"canvas-fallback-safety"),Check);
   CanvasProjectRefreshTests.Run(Path.Combine(root,"project-refresh"),Check);
   GeneralPerformanceTests.Run(Path.Combine(root,"general-performance"),Check);
   ThumbnailThroughputTests.Scheduling(Path.Combine(root,"thumbnail-scheduling"),Check);ThumbnailThroughputTests.Raster(Check);
   WorkerStartupTests.Run(Path.Combine(root,"worker-startup"),Check);
   CanvasTextLayoutTests.Run(Path.Combine(root,"canvas-text-layout"),Check);
   CanvasLineSelectionTests.Run(Path.Combine(root,"canvas-line-selection"),Check);
   CanvasStyleEditingTests.Run(Path.Combine(root,"canvas-style-editing"),Check);
   MatureCanvasStoreTests.Run(Path.Combine(root,"mature-canvas-store"),Check);
   MatureCanvasHostTests.Run(Path.Combine(root,"mature-canvas-host"),Check);
   MatureCanvasInteractionTests.Run(Path.Combine(root,"mature-canvas-interaction"),Check);
   AppearanceSettingsTests.Run(Path.Combine(root,"appearance-settings"),Check);
   CustomizationTests.Run(Path.Combine(root,"customization"),Check);
   PreviewPreferencesTests.Run(Path.Combine(root,"preview-preferences"),Check);
   DesktopImagePinTests.Run(Path.Combine(root,"desktop-image-pins"),Check);
   PreviewClipboardTests.Run(Path.Combine(root,"preview-clipboard"),Check);
   PinLifecycleTests.Run(Path.Combine(root,"pin-lifecycle"),Check);
   PinRuntimeTests.Run(Path.Combine(root,"pin-runtime"),Check);
   PinHistoryIndexTests.Run(Path.Combine(root,"pin-history-index"),Check);
   TextWindowPinOutputTests.Run(Path.Combine(root,"text-window-pin-output"),Check);
   MiddleDragPanTests.Run(Check);
   File.WriteAllText(log,"PASS "+checks+" checks\r\nFixtures: "+root+"\r\n"+DateTime.Now.ToString("O"),Encoding.UTF8);return 0;
  }catch(Exception ex){File.WriteAllText(log,"FAIL after "+checks+" checks\r\n"+ex+"\r\nFixtures: "+root,Encoding.UTF8);return 1;}}
 }
}
