#if OCR_FALLBACK_TESTS
// Compile with the real OcrService and OcrResultCache. Every platform, pixel and
// recognition adapter below is inert: never load WinRT, models or app settings.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using QingJie;
using Windows.Globalization;
using Windows.Media.Ocr;

internal static class OcrFallbackTests {
    static int checks,cases;
    static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks++;Console.WriteLine("PASS "+name);}
    static void Run(string name,Action body){OcrEngine.Reset();EnglishOcr.Reset();OcrLanguagePacks.Reset();OcrPixels.Created=OcrPixels.WindowsCalls=0;Console.WriteLine("CASE "+name);body();cases++;}
    static BitmapSource Image(){return new BitmapSource(2,2);}
    static OcrPage Read(string languages=null,CancellationToken cancel=default(CancellationToken)){return OcrService.ReadLocal(Image(),languages,cancel).GetAwaiter().GetResult();}
    static T Failure<T>(Action action) where T:Exception {try{action();}catch(T error){return error;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
    static void Tests(){
        Check(typeof(OcrEngine).Assembly==typeof(OcrFallbackTests).Assembly&&typeof(EnglishOcr).Assembly==typeof(OcrFallbackTests).Assembly,"platform and model adapters are compiled inert stubs");
        Check(!typeof(OcrFallbackTests).Assembly.GetReferencedAssemblies().Any(a=>a.Name=="PresentationCore"||a.Name=="PresentationFramework"||a.Name=="Tesseract"||a.Name=="Mori"||a.Name.StartsWith("Windows.",StringComparison.Ordinal)),"no application, WinRT, UI or native OCR assembly referenced");
        Run("No Windows OCR languages retains bundled English",()=>{
            var page=Read();Check(page.Text=="English result"&&EnglishOcr.Calls==1,"missing Windows engine still executes bundled English once");
            Check(OcrPixels.Created==1&&OcrPixels.WindowsCalls==0&&OcrEngine.RecognizeCalls==0,"fallback prepares once without Windows bitmap or recognition call");
            Check(page.Notice!=null&&page.Notice.Contains("内置英文")&&page.Notice.Contains("中文暂不可用"),"fallback explicitly describes English capability and unavailable Chinese");
            Check(page.Text=="English result"&&!page.Text.Contains(page.Notice)&&page.Words.Single().TextStart==0,"capability notice never pollutes transcript, words or copy offsets");
        });
        Run("Enabled and installed optional capability",()=>{
            OcrLanguagePacks.InstalledLanguages.Add("jpn");var page=Read("jpn");
            Check(EnglishOcr.Languages=="jpn"&&page.Notice.Contains("已启用的附加语言")&&page.Notice.Contains("中文暂不可用"),"installed optional languages are described without promising Chinese");
        });
        Run("Missing optional model is not advertised",()=>{var page=Read("jpn");Check(!page.Notice.Contains("附加语言"),"uninstalled selection does not claim available language capability");});
        Run("Normal Windows OCR path unchanged",()=>{
            OcrEngine.Languages.Add(new Language("en-US"));OcrEngine.Profile=new OcrEngine();EnglishOcr.PreserveOriginal=true;
            var page=Read();Check(page.Text=="Windows result"&&page.Notice==null,"normal recognition preserves transcript with no new notice");
            Check(OcrEngine.RecognizeCalls==1&&OcrPixels.WindowsCalls==1&&EnglishOcr.Calls==1,"normal path retains one Windows and one English refinement call");
            Check(page.Words.Single().Box==new Rect(1,2,3,4),"Windows coordinates retain inverse 3x mapping");
        });
        Run("Prefer installed Chinese recognizer",()=>{
            OcrEngine.Languages.Add(new Language("en-US"));OcrEngine.Languages.Add(new Language("zh-Hans-CN"));OcrEngine.Chinese=new OcrEngine();OcrEngine.Profile=new OcrEngine();Read();
            Check(OcrEngine.Selected=="zh-Hans-CN"&&OcrEngine.ProfileCalls==0,"Chinese recognizer preference does not add another profile engine");
        });
        Run("Neither engine available is actionable",()=>{
            EnglishOcr.Error=new InvalidOperationException("synthetic engine initialization failure");var error=Failure<InvalidOperationException>(()=>Read());
            Check(error.Message.Contains("Windows")&&error.Message.Contains("内置文字识别也未完成")&&error.Message.Contains("安装")&&error.InnerException==EnglishOcr.Error,"no engines returns repair guidance and preserves diagnostic cause");
        });
        Run("Existing Windows refinement failure stays observable",()=>{
            OcrEngine.Profile=new OcrEngine();EnglishOcr.Error=new InvalidOperationException("synthetic refinement failure");
            Check(Failure<InvalidOperationException>(()=>Read())==EnglishOcr.Error,"Windows-present failures are not mislabeled missing-language errors");
        });
        Run("Pre-cancel avoids all recognition work",()=>{using(var cancel=new CancellationTokenSource()){cancel.Cancel();Failure<OperationCanceledException>(()=>Read(null,cancel.Token));Check(OcrEngine.LanguageProbes==0&&OcrPixels.Created==0&&EnglishOcr.Calls==0,"pre-cancel never enumerates languages or prepares pixels");}});
        Run("Cancellation after Windows avoids refinement",()=>{using(var cancel=new CancellationTokenSource()){OcrEngine.Profile=new OcrEngine();OcrEngine.OnRecognize=cancel.Cancel;Failure<OperationCanceledException>(()=>Read(null,cancel.Token));Check(EnglishOcr.Calls==0,"cancelled Windows result cannot start English refinement");}});
        Run("Fallback cancellation is never engine failure",()=>{EnglishOcr.Error=new OperationCanceledException();Failure<OperationCanceledException>(()=>Read());Check(EnglishOcr.Calls==1,"fallback cancellation propagates unchanged instead of repair guidance");});
        Run("Cancellation after refinement discards stale result",()=>{using(var cancel=new CancellationTokenSource()){EnglishOcr.OnImprove=cancel.Cancel;Failure<OperationCanceledException>(()=>Read(null,cancel.Token));Check(cancel.IsCancellationRequested,"completed OCR cannot escape a cancelled request");}});
        Run("Empty fallback still reports capability",()=>{EnglishOcr.Empty=true;var page=Read();Check(page.Text==""&&page.Words.Count==0&&page.Notice.Contains("中文暂不可用"),"unreadable image is not a false engine failure and retains capability notice");});
        Run("Null image is rejected",()=>{Failure<ArgumentNullException>(()=>OcrService.ReadLocal(null).GetAwaiter().GetResult());Check(OcrEngine.LanguageProbes==0&&OcrPixels.Created==0,"null image performs no work");});
        Run("Notice survives bounded cache without aliasing",()=>{
            var page=Read();var cache=new OcrResultCache(()=>0);cache.Put("fallback",page);var copy=cache.Get("fallback");
            Check(copy.Notice==page.Notice&&copy.Text=="English result","cached fallback retains separate notice and exact transcript");
            copy.Notice="mutated";copy.Words[0].Text="mutated";Check(cache.Get("fallback").Notice==page.Notice&&cache.Get("fallback").Words[0].Text=="English result","cached notice and words are copied, not exposed by mutable reference");
            page.Notice=null;cache.Put("normal",page);Check(cache.Get("normal").Notice==null,"default-null notice remains compatible with ordinary results");
        });
        Run("Status composition is compact and non-destructive",()=>{
            Check(OcrService.WithNotice("Done",null)=="Done"&&OcrService.WithNotice(null,null)=="","normal success and clear status retain their old appearance");
            Check(OcrService.WithNotice(null,"Capability")=="Capability"&&OcrService.WithNotice("Capability","Capability")=="Capability","clearing progress retains capability notice without duplicates");
            Check(OcrService.WithNotice("Retry","Capability")=="Retry"+Environment.NewLine+"Capability","error and progress can coexist with capability in existing status card");
        });
    }
    public static int Main(){try{Tests();Console.WriteLine("PASS "+checks+" assertions in "+cases+" deterministic cases");Console.WriteLine("SAFETY: inert platform/pixels/engines only; no UI, models, settings, library, app, native calls or network.");return 0;}catch(Exception error){Console.Error.WriteLine("FAIL after "+checks+" assertions: "+error);return 1;}}
}

namespace QingJie {
    internal sealed class OcrPixels {
        internal static int Created,WindowsCalls;
        internal OcrPixels(BitmapSource source){Created++;}
        public Windows.Graphics.Imaging.SoftwareBitmap WindowsBitmap(){WindowsCalls++;return new Windows.Graphics.Imaging.SoftwareBitmap();}
    }
    public static class OcrWorker { public static Task<OcrPage> Read(BitmapSource source,CancellationToken cancel){throw new InvalidOperationException("Worker must not run in inert tests");} }
    public static class Geometry {public static bool NeedsWordSpace(char a,char b){return true;}}
    public static class OcrLanguagePacks {
        internal static readonly HashSet<string> InstalledLanguages=new HashSet<string>();
        public static string EffectiveSelectionKey{get{return "";}}
        public static string[] ParseSelection(string selection){return (selection??"").Split('+').Where(x=>x=="jpn"||x=="kor").ToArray();}
        public static bool Installed(string language){return InstalledLanguages.Contains(language);}
        internal static void Reset(){InstalledLanguages.Clear();}
    }
    public static class EnglishOcr {
        internal static int Calls;internal static bool PreserveOriginal,Empty;internal static Exception Error;internal static string Languages;internal static Action OnImprove;
        internal static void Reset(){Calls=0;PreserveOriginal=Empty=false;Error=null;Languages=null;OnImprove=null;}
        internal static Task<OcrPage> Improve(OcrPixels pixels,double ratio,OcrPage original,string languages){Calls++;Languages=languages;if(OnImprove!=null)OnImprove();if(Error!=null)throw Error;var page=PreserveOriginal?original:new OcrPage();if(!PreserveOriginal&&!Empty)page.Words.Add(new WordBox{Text="English result",Box=new Rect(0,0,10,10)});OcrService.IndexText(page);return Task.FromResult(page);}
    }
}
namespace System.Windows.Media {
    public static class PixelFormats {public static readonly object Bgra32=new object(),Gray8=new object();}
    public sealed class ScaleTransform {internal readonly double X,Y;public ScaleTransform(double x,double y){X=x;Y=y;}}
}
namespace System.Windows.Media.Imaging {
    public class BitmapSource {
        public int PixelWidth{get;protected set;}public int PixelHeight{get;protected set;}
        public BitmapSource(int width,int height){PixelWidth=width;PixelHeight=height;}
        public void Freeze(){}
        public virtual void CopyPixels(Int32Rect area,byte[] pixels,int stride,int offset){for(int i=0;i<area.Height*stride;i++)pixels[offset+i]=255;}
        public static BitmapSource Create(int width,int height,double x,double y,object format,object palette,byte[] pixels,int stride){return new BitmapSource(width,height);}
    }
    public sealed class FormatConvertedBitmap:BitmapSource {public FormatConvertedBitmap(BitmapSource source,object format,object palette,double alpha):base(source.PixelWidth,source.PixelHeight){}}
    public sealed class TransformedBitmap:BitmapSource {public TransformedBitmap(BitmapSource source,System.Windows.Media.ScaleTransform scale):base((int)(source.PixelWidth*scale.X),(int)(source.PixelHeight*scale.Y)){}}
}
namespace Windows.Globalization {public sealed class Language {public string LanguageTag;public Language(string tag){LanguageTag=tag;}}}
namespace Windows.Foundation {
    public enum AsyncStatus {Completed,Canceled,Error}
    public delegate void AsyncOperationCompletedHandler<T>(IAsyncOperation<T> operation,AsyncStatus status);
    public interface IAsyncOperation<T> {AsyncOperationCompletedHandler<T> Completed{get;set;}T GetResults();}
    internal sealed class CompletedOperation<T>:IAsyncOperation<T> {
        readonly T result;AsyncOperationCompletedHandler<T> completed;
        internal CompletedOperation(T value){result=value;}
        public AsyncOperationCompletedHandler<T> Completed{get{return completed;}set{completed=value;completed(this,AsyncStatus.Completed);}}
        public T GetResults(){return result;}
    }
}
namespace Windows.Graphics.Imaging {public sealed class SoftwareBitmap:IDisposable {public void Dispose(){}}}
namespace Windows.Media.Ocr {
    public sealed class OcrWord {public string Text="Windows result";public Rect BoundingRect=new Rect(3,6,9,12);}
    public sealed class OcrLine {public readonly List<OcrWord> Words=new List<OcrWord>{new OcrWord()};}
    public sealed class OcrResult {public string Text="Windows result";public readonly List<OcrLine> Lines=new List<OcrLine>{new OcrLine()};}
    public sealed class OcrEngine {
        internal static readonly List<Windows.Globalization.Language> Languages=new List<Windows.Globalization.Language>();
        internal static OcrEngine Profile,Chinese;internal static int LanguageProbes,ProfileCalls,RecognizeCalls;internal static string Selected;internal static Action OnRecognize;
        public static IEnumerable<Windows.Globalization.Language> AvailableRecognizerLanguages{get{LanguageProbes++;return Languages;}}
        public static int MaxImageDimension{get{return 10000;}}
        public static OcrEngine TryCreateFromLanguage(Windows.Globalization.Language language){Selected=language.LanguageTag;return Chinese;}
        public static OcrEngine TryCreateFromUserProfileLanguages(){ProfileCalls++;return Profile;}
        public Windows.Foundation.IAsyncOperation<OcrResult> RecognizeAsync(Windows.Graphics.Imaging.SoftwareBitmap bitmap){RecognizeCalls++;if(OnRecognize!=null)OnRecognize();return new Windows.Foundation.CompletedOperation<OcrResult>(new OcrResult());}
        internal static void Reset(){Languages.Clear();Profile=Chinese=null;LanguageProbes=ProfileCalls=RecognizeCalls=0;Selected=null;OnRecognize=null;}
    }
}
#endif
