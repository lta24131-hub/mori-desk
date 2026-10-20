using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// All providers are synthetic; windows stay unshown and have no native handle.
internal static class CaptureFeatureBoundaryTests {
    static IEnumerable<DependencyObject> Tree(DependencyObject node){yield return node;foreach(object value in LogicalTreeHelper.GetChildren(node)){var child=value as DependencyObject;if(child!=null)foreach(var entry in Tree(child))yield return entry;}}
    static object Field(TextWindow view,string name){return typeof(TextWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(view);}
    static void Call(TextWindow view,string name){typeof(TextWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(view,null);}
    static bool Subscribed(TextWindow view){var handler=typeof(AppState).GetField("TranslationProviderChanged",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null) as Delegate;return handler!=null&&handler.GetInvocationList().Any(item=>ReferenceEquals(item.Target,view));}
    internal static void Run(Action<bool,string> check){
        int reads=0;var targets=new CaptureTargets(new List<CaptureTarget>());
        Func<CaptureTargets> provider=()=>{reads++;return targets;};
        check(AppState.ReadCaptureTargets(CaptureFeatures.Basic(),false,provider)==null&&reads==0,"disabled smart selection does not call the native-geometry provider");
        check(AppState.ReadCaptureTargets(new CaptureFeatures(),true,provider)==null&&reads==0,"synthetic capture never inspects the real desktop even with smart selection enabled");
        check(ReferenceEquals(AppState.ReadCaptureTargets(new CaptureFeatures(),false,provider),targets)&&reads==1,"enabled smart selection reads its geometry exactly once");
        check(AppState.ReadCaptureTargets(new CaptureFeatures(),false,()=>{throw new InvalidOperationException("synthetic enumeration failure");})==null,"failed optional geometry does not block manual screenshot selection");
        check(AppState.ReadCaptureTargets(null,false,provider)==null&&AppState.ReadCaptureTargets(new CaptureFeatures(),false,null)==null&&reads==1,"missing optional provider or policy cannot launch geometry detection");

        var pixels=new byte[16*16*4];var image=BitmapSource.Create(16,16,96,96,PixelFormats.Bgra32,null,pixels,16*4);image.Freeze();
        int recognitions=0;Func<BitmapSource,System.Threading.CancellationToken,Task<OcrPage>> recognize=(bitmap,token)=>{recognitions++;return Task.FromResult(new OcrPage());};
        var reader=new TextWindow(image,"zh",recognize,allowTranslation:false,allowPin:false);
        try{
            var buttons=Tree(reader).OfType<Button>().ToArray();
            check(!buttons.Any(button=>Convert.ToString(button.ToolTip).Contains("翻译")||Convert.ToString(button.Content)=="翻译所选"),"OCR-only reader omits translation actions and language menu");
            check(!buttons.Any(button=>Convert.ToString(button.ToolTip)=="贴到屏幕"),"capture reader omits pin action when screenshot pinning is disabled");
            check(buttons.Any(button=>Convert.ToString(button.ToolTip)=="复制图片")&&buttons.Any(button=>Convert.ToString(button.ToolTip)=="保存图片"),"OCR-only reader retains essential copy and save actions");
            check(!Subscribed(reader),"OCR-only reader does not subscribe to translation provider changes");
            Call(reader,"ToggleImageTranslation");
            check(recognitions==0&&Field(reader,"imageRequest")==null&&Field(reader,"ocrTask")==null,"disabled translation rejects a direct action before OCR or a request starts");
            // Empty selected text ensures even a failed policy guard cannot
            // issue a network translation in this background-only regression.
            Call(reader,"TranslateSelected");
            check(Field(reader,"selectionRequest")==null,"disabled selected-text route leaves no translation request");
            reader.EnsureOcr().GetAwaiter().GetResult();
            check(recognitions==1,"disabling translation leaves explicitly requested OCR functional");
        }finally{reader.Close();}
        var standalone=new TextWindow(image,"zh",recognize);
        try{
            check(Tree(standalone).OfType<Button>().Any(button=>Convert.ToString(button.ToolTip).StartsWith("整图翻译")),"standalone image reader keeps existing translation controls by default");
            check(Subscribed(standalone),"standalone image reader retains translation provider refresh");
        }finally{standalone.Close();}
        check(!Subscribed(standalone),"closing the image reader releases its translation subscription");
    }
}
