using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// No real preferences, component startup, desktop, window handles, or models.
internal static class CaptureFeatureSettingsTests {
    static FieldInfo[] Flags {get{return typeof(CaptureFeatures).GetFields(BindingFlags.Instance|BindingFlags.Public).Where(field=>field.FieldType==typeof(bool)).ToArray();}}
    static bool All(CaptureFeatures value,bool expected){return Flags.All(field=>(bool)field.GetValue(value)==expected);}
    static IEnumerable<DependencyObject> Tree(DependencyObject node){yield return node;foreach(object value in LogicalTreeHelper.GetChildren(node)){var child=value as DependencyObject;if(child!=null)foreach(var entry in Tree(child))yield return entry;}}
    internal static void Run(Action<bool,string> check){
        check(Flags.Length==13,"feature defaults cover twelve screenshot options and optional long-capture prewarm");
        var defaults=new CaptureFeatures();var basic=CaptureFeatures.Basic();
        check(All(defaults,true)&&All(basic,false),"all-on upgrade defaults and basic screenshot preset are explicit");
        check(defaults.HasAnnotations&&!basic.HasAnnotations,"annotation property reflects available marking tools");
        check(defaults.AllowsTool("select")&&basic.AllowsTool("select"),"crop selection is never disabled by optional feature settings");
        foreach(var tool in new[]{"rect","ellipse","arrow","line","pen","text","mosaic","ocr","translate","scroll","pin","library"})check(defaults.AllowsTool(tool)&&!basic.AllowsTool(tool),"feature policy gates real tool "+tool);
        check(!defaults.AllowsTool(null)&&!defaults.AllowsTool("unknown")&&!basic.AllowsTool("unknown"),"unknown tools cannot accidentally opt in");
        var snapshot=defaults.Copy();defaults.Pen=false;check(snapshot.Pen&&!defaults.Pen,"feature copy isolates active capture from later settings changes");
        var lines=CaptureFeatures.Basic();lines.Lines=true;check(lines.AllowsTool("arrow")&&lines.AllowsTool("line")&&lines.HasAnnotations&&!lines.AllowsTool("rect"),"arrow and line share one option without enabling other annotations");
        var translate=CaptureFeatures.Basic();translate.Translation=true;check(translate.AllowsTool("translate")&&!translate.AllowsTool("ocr"),"screenshot translation and extract-text entries may be configured independently");
        Persistence(check);Panel(check);
    }
    static void Persistence(Action<bool,string> check){
        string previous=Preferences.TestFilePath;
        if(string.IsNullOrEmpty(previous)||!Path.IsPathRooted(previous))throw new InvalidOperationException("Feature persistence tests require the background runner sandbox.");
        string folder=Path.Combine(Path.GetDirectoryName(previous),"feature-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);Preferences.TestFilePath=Path.Combine(folder,"settings.json");
        var serializer=new JavaScriptSerializer();
        try{
            check(All(Preferences.Load().CaptureFeatures,true),"absent settings file preserves all existing screenshot features");
            foreach(string input in new[]{"{}","{\"Stroke\":7}","{\"CaptureFeatures\":null}","null"}){
                File.WriteAllText(Preferences.TestFilePath,input);check(All(Preferences.Load().CaptureFeatures,true),"legacy or null feature settings normalize to existing all-on behavior: "+input);
            }
            File.WriteAllText(Preferences.TestFilePath,"{\"CaptureFeatures\":{\"Ocr\":false}}");var partial=Preferences.Load();
            check(!partial.CaptureFeatures.Ocr&&Flags.Where(field=>field.Name!="Ocr").All(field=>(bool)field.GetValue(partial.CaptureFeatures)),"partial feature settings retain defaults for fields absent from the file");
            File.WriteAllText(Preferences.TestFilePath,"{\"Stroke\":9,\"TextSize\":42,\"Color\":\"#123456\",\"OcrLanguage\":\"jpn\",\"FutureSetting\":{\"Value\":37},\"CaptureFeatures\":{\"FutureTool\":false,\"Scrolling\":true}}");
            var preferences=Preferences.Load();preferences.CaptureFeatures=CaptureFeatures.Basic();preferences.CaptureFeatures.Text=true;preferences.CaptureFeatures.Translation=true;preferences.SaveChecked();
            var loaded=Preferences.Load();
            check(loaded.CaptureFeatures.Text&&loaded.CaptureFeatures.Translation&&!loaded.CaptureFeatures.Scrolling&&!loaded.CaptureFeatures.ScrollPrewarm&&!loaded.CaptureFeatures.Ocr,"feature choices survive a disk round trip independently");
            check(loaded.Stroke==9&&loaded.TextSize==42&&loaded.Color=="#123456"&&loaded.OcrLanguage=="jpn","saving features preserves unrelated established preferences");
            var payload=(Dictionary<string,object>)serializer.DeserializeObject(File.ReadAllText(Preferences.TestFilePath));
            check((int)((Dictionary<string,object>)payload["FutureSetting"])["Value"]==37&&!(bool)((Dictionary<string,object>)payload["CaptureFeatures"])["FutureTool"],"saving known feature settings preserves unknown root and nested future values");
            check(!((Dictionary<string,object>)payload["CaptureFeatures"]).ContainsKey("HasAnnotations"),"derived annotation availability is not written as a user preference");
            preferences.CaptureFeatures=CaptureFeatures.Basic();preferences.SaveChecked();check(All(Preferences.Load().CaptureFeatures,false),"all-disabled basic preset survives restart rather than reverting to defaults");
        }finally{Preferences.TestFilePath=previous;}
    }
    static void Panel(Action<bool,string> check){
        var preferences=new Preferences {Stroke=8,TextSize=32,OcrLanguage="kor",TranslationProvider="offline"};
        var original=preferences.CaptureFeatures;var shortcuts=preferences.Shortcuts;int saves=0;bool fail=false;
        var panel=new CaptureFeaturesPanel(preferences,()=>{saves++;if(fail)throw new IOException("synthetic write failure");});
        var controls=Tree(panel).OfType<CheckBox>().ToArray();
        check(controls.Length==13&&controls.Select(control=>control.Name).Distinct().Count()==13,"settings expose each implemented feature and prewarm once with stable UI identifiers");
        check(controls.All(control=>AutomationProperties.GetName(control).Length>0&&AutomationProperties.GetHelpText(control).Length>0&&control.MinHeight>=28),"checkboxes have accessible labels, help text, and practical hit areas");
        var basic=Tree(panel).OfType<Button>().Single(button=>button.Name=="CaptureFeaturesBasic");var all=Tree(panel).OfType<Button>().Single(button=>button.Name=="CaptureFeaturesAll");var save=Tree(panel).OfType<Button>().Single(button=>button.Name=="SaveCaptureFeatures");
        basic.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(All(panel.Draft,false)&&All(preferences.CaptureFeatures,true)&&saves==0,"basic preset changes only the unsaved draft");
        all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(All(panel.Draft,true)&&saves==0,"all-on preset also waits for explicit save");
        controls.Single(control=>control.Name=="CaptureFeature_Scrolling").IsChecked=false;
        var prewarm=controls.Single(control=>control.Name=="CaptureFeature_ScrollPrewarm");
        check(!prewarm.IsEnabled&&prewarm.IsChecked==true,"turning off long capture disables prewarm while retaining its preference");
        controls.Single(control=>control.Name=="CaptureFeature_Scrolling").IsChecked=true;
        check(prewarm.IsEnabled&&prewarm.IsChecked==true,"re-enabling long capture restores the retained prewarm choice");
        prewarm.IsChecked=false;check(!panel.Draft.ScrollPrewarm&&panel.Draft.Scrolling,"prewarm may be disabled without removing long capture");
        controls.Single(control=>control.Name=="CaptureFeature_Scrolling").IsChecked=false;
        check(!panel.Draft.Scrolling&&preferences.CaptureFeatures.Scrolling&&saves==0,"individual checkboxes do not change active settings before save");
        var exposed=panel.Draft;exposed.Ocr=false;check(panel.Draft.Ocr,"draft getter does not leak a mutable reference to panel state");
        fail=true;check(!panel.SaveDraft()&&ReferenceEquals(preferences.CaptureFeatures,original)&&!panel.Draft.Scrolling,"failed save restores the exact previous settings and retains the retry draft");
        var status=Tree(panel).OfType<TextBlock>().Single(text=>text.Name=="CaptureFeaturesStatus");check(status.Text.Contains("保存失败")&&status.Text.Contains("原设置")&&status.Text.Contains("重试"),"save failure clearly explains that prior preferences remain active and retry is available");
        fail=false;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(saves==2&&!preferences.CaptureFeatures.Scrolling&&!ReferenceEquals(preferences.CaptureFeatures,original),"explicit save commits a distinct feature snapshot after a failed attempt");
        check(status.Text.Contains("下一次截图")&&status.Text.Contains("无需重启"),"save confirmation names the actual next-capture activation boundary");
        check(preferences.Stroke==8&&preferences.TextSize==32&&preferences.OcrLanguage=="kor"&&preferences.TranslationProvider=="offline"&&ReferenceEquals(shortcuts,preferences.Shortcuts),"feature save does not replace unrelated appearance, language, provider, or shortcuts");
        panel.SetDraft(CaptureFeatures.Basic());check(preferences.CaptureFeatures.Text,"editing another unsaved draft leaves the committed feature snapshot intact");
        string help=string.Join(" ",Tree(panel).OfType<TextBlock>().Select(text=>text.Text));
        check(help.Contains("按需加载")&&help.Contains("实际占用")&&help.Contains("仍会按需识别文字")&&help.Contains("不会卸载")&&help.Contains("独立翻译")&&help.Contains("已有贴图")&&help.Contains("Esc"),"settings explain shared OCR dependency, resource limits, retained installations, unaffected workflows, and essential exit");
        check(help.Contains("临时多占内存")&&help.Contains("结束截图即释放")&&help.Contains("不会读取屏幕内容"),"prewarm explains its bounded resource and no-pixel-read behavior without promising a measured saving");
        foreach(double width in new[]{300.0,468.0,640.0}){panel.Measure(new Size(width,double.PositiveInfinity));panel.Arrange(new Rect(0,0,width,panel.DesiredSize.Height));check(panel.DesiredSize.Width<=width+.1,"feature settings wrap without horizontal overflow at width "+width);}
        var nullPreferences=new Preferences {CaptureFeatures=null};var nullPanel=new CaptureFeaturesPanel(nullPreferences,()=>{});check(All(nullPanel.Draft,true)&&nullPreferences.CaptureFeatures==null,"viewing null feature settings shows compatible defaults without mutating preferences");
        // Render the real settings control offscreen, never a desktop capture.
        var frame=Ui.Theme(new Border {Child=nullPanel,Padding=new Thickness(24)},Border.BackgroundProperty,"Surface");
        frame.Measure(new Size(520,double.PositiveInfinity));int height=(int)Math.Ceiling(frame.DesiredSize.Height);frame.Arrange(new Rect(0,0,520,height));frame.UpdateLayout();
        var preview=new RenderTargetBitmap(520,height,96,96,PixelFormats.Pbgra32);preview.Render(frame);preview.Freeze();
        string previewPath=Path.Combine(Path.GetDirectoryName(Preferences.TestFilePath),"capture-feature-settings.png");Directory.CreateDirectory(Path.GetDirectoryName(previewPath));
        using(var stream=File.Create(previewPath)){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(preview));encoder.Save(stream);}
        check(preview.PixelHeight>300&&File.Exists(previewPath),"real settings layout is rendered offscreen for visual review without showing a window");
    }
}
