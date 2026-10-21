using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mori {
 public static class StorageSettingsTests {
  public static List<string> Run(string output){
   Directory.CreateDirectory(output);var log=new List<string>();
   Action<bool,string> check=delegate(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);log.Add("PASS: "+name);};
   Action<Action,string> rejects=delegate(Action action,string name){bool failed=false;try{action();}catch{failed=true;}check(failed,name);};
   string source=Path.Combine(output,"current-library");Directory.CreateDirectory(source);string sourceFile=Path.Combine(source,"original.txt");File.WriteAllText(sourceFile,"original library stays here");
   string settingsPath=Path.Combine(output,"settings.json");new Settings{LibraryPath=source,Theme="light"}.SaveTo(settingsPath);byte[] settingsBefore=File.ReadAllBytes(settingsPath);
   check(SettingsWindow.CreateStorageRequest(source,source,false)==null,"unchanged storage location returns no migration request");
   check(SettingsWindow.CreateStorageRequest(source,source.ToUpperInvariant()+Path.DirectorySeparatorChar,false)==null,"equivalent case and trailing separator do not trigger migration");
   string destination=Path.Combine(output,"new-library");var request=SettingsWindow.CreateStorageRequest(source,destination,false);
   check(request!=null&&request.Migrate&&request.Path==Path.GetFullPath(destination),"a new storage location requests whole-library migration");
   check(!Directory.Exists(destination),"choosing a new storage location does not create the destination");
   string empty=Path.Combine(output,"empty-folder");Directory.CreateDirectory(empty);request=SettingsWindow.CreateStorageRequest(source,empty,false);
   check(request!=null&&request.Migrate&&!Directory.EnumerateFileSystemEntries(empty).Any(),"choosing an empty destination leaves it empty until migration");
   string occupied=Path.Combine(output,"occupied");Directory.CreateDirectory(occupied);string occupiedFile=Path.Combine(occupied,"keep.txt");File.WriteAllText(occupiedFile,"do not overwrite");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,occupied,false);},"settings reject a nonempty destination without merging");
   check(File.ReadAllText(occupiedFile)=="do not overwrite","rejected destination content stays unchanged");
   string existingLibrary=Path.Combine(output,"another-library");Directory.CreateDirectory(Path.Combine(existingLibrary,".mori"));File.WriteAllText(Path.Combine(existingLibrary,".mori","library.db"),"existing database");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,existingLibrary,false);},"storage relocation rejects an existing library as a merge destination");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,Path.Combine(source,"inside"),false);},"settings reject a migration into the source library");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,output,false);},"settings reject a destination containing the source library");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,sourceFile,false);},"settings reject a file used as a destination directory");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,Path.GetPathRoot(source),false);},"settings reject a disk root as a migration destination");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,"relative-new-library",false);},"settings require an absolute destination path");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,Path.Combine(output,"missing-parent","new-library"),false);},"settings reject a destination whose parent does not exist");
   rejects(delegate{SettingsWindow.CreateStorageRequest(source,"",false);},"settings reject an empty storage path");
   check(SettingsWindow.CreateStorageRequest(source,destination,true)==null&&SettingsWindow.CreateStorageRequest(source,"",true)==null,"preview mode cannot submit a storage change");
   check(settingsBefore.SequenceEqual(File.ReadAllBytes(settingsPath))&&Settings.LoadFrom(settingsPath).LibraryPath==source,"storage requests never persist a new library path before verified migration");
   check(File.ReadAllText(sourceFile)=="original library stays here"&&!Directory.Exists(destination),"validating storage changes never moves or deletes original files");
   ConversionOutputPreferences(output,check,rejects);
   return log;
  }
  static void ConversionOutputPreferences(string output,Action<bool,string> check,Action<Action,string> rejects){
   string area=Path.Combine(output,"conversion-preferences");Directory.CreateDirectory(area);
   string legacy=Path.Combine(area,"legacy.json"),library=Path.Combine(area,"library");
   File.WriteAllText(legacy,new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"LibraryPath",library},{"Theme","light"}}));
   string originalDefault=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Nook 转换");
   check(new Settings().FileConversionOutputPath==originalDefault&&Settings.LoadFrom(legacy).FileConversionOutputPath==originalDefault,"old and new settings retain the existing Documents conversion default until explicitly changed");
   string target=Path.Combine(area,"new-results","nested");
   check(Settings.ValidateConversionOutputPath(target+Path.DirectorySeparatorChar)==Path.GetFullPath(target)&&!Directory.Exists(Path.Combine(area,"new-results")),"conversion destination validation normalizes without creating any directory");
   foreach(string invalid in new[]{"", "relative-results", @"C:results", @"\\server\share\results", @"\\?\C:\results", @"\\.\C:\results", Path.GetPathRoot(area), Path.Combine(area,"NUL"), "https://example.test/results"})
    rejects(delegate{Settings.ValidateConversionOutputPath(invalid);},"conversion preferences reject nonordinary destination: "+invalid);
   string file=Path.Combine(area,"keep.txt");File.WriteAllText(file,"previous output");
   rejects(delegate{Settings.ValidateConversionOutputPath(file);},"conversion preferences reject an existing file as a folder");
   rejects(delegate{Settings.ValidateConversionOutputPath(Path.Combine(file,"child"));},"conversion preferences reject a file-valued ancestor");
   var changed=Settings.LoadFrom(legacy);var stale=Settings.LoadFrom(legacy);
   changed.FileConversionOutputPath=target;changed.SaveTo(legacy);
   check(Settings.LoadFrom(legacy).FileConversionOutputPath==target&&!Directory.Exists(target),"conversion output preference persists without moving files or eagerly creating destination");
   stale.Theme="dark";stale.SaveTo(legacy);
   check(Settings.LoadFrom(legacy).FileConversionOutputPath==target&&stale.FileConversionOutputPath==target&&stale.Theme=="dark","a stale unrelated preferences editor preserves the newer output path and refreshes ApplySaved");
   var outputEditor=Settings.LoadFrom(legacy);var libraryEditor=Settings.LoadFrom(legacy);
   string newerLibrary=Path.Combine(area,"different-library");libraryEditor.LibraryPath=newerLibrary;libraryEditor.SaveTo(legacy);
   string secondTarget=Path.Combine(area,"second-results");outputEditor.FileConversionOutputPath=secondTarget;outputEditor.SaveTo(legacy);
   var loaded=Settings.LoadFrom(legacy);
   check(loaded.LibraryPath==newerLibrary&&outputEditor.LibraryPath==newerLibrary&&loaded.FileConversionOutputPath==secondTarget,"changing conversion output does not overwrite a concurrent library-path edit");
   check(File.ReadAllText(file)=="previous output"&&!Directory.Exists(secondTarget)&&!Directory.Exists(newerLibrary),"output preference changes leave existing files, library and future destinations untouched");
  }
 }
}
