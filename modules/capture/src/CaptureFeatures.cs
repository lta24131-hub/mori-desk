using System.Web.Script.Serialization;

namespace QingJie {
    // Defaults preserve the existing screenshot experience when upgrading from
    // settings files that predate feature selection. Copy once per capture.
    public sealed class CaptureFeatures {
        public bool SmartSelection=true;
        public bool Rectangle=true;
        public bool Ellipse=true;
        public bool Lines=true;
        public bool Pen=true;
        public bool Text=true;
        public bool Mosaic=true;
        public bool Ocr=true;
        public bool Translation=true;
        public bool Scrolling=true;
        public bool ScrollPrewarm=true;
        public bool Pin=true;
        public bool Collect=true;

        public CaptureFeatures Copy(){return (CaptureFeatures)MemberwiseClone();}
        public static CaptureFeatures Basic(){return new CaptureFeatures {
            SmartSelection=false,Rectangle=false,Ellipse=false,
            Lines=false,Pen=false,Text=false,Mosaic=false,Ocr=false,
            Translation=false,Scrolling=false,ScrollPrewarm=false,Pin=false,Collect=false
        };}
        [ScriptIgnore]
        public bool HasAnnotations {get{return Rectangle||Ellipse||Lines||Pen||Text||Mosaic;}}
        public bool AllowsTool(string tool){
            switch(tool){
                case "select":return true;
                case "rect":return Rectangle;
                case "ellipse":return Ellipse;
                case "arrow":case "line":return Lines;
                case "pen":return Pen;
                case "text":return Text;
                case "mosaic":return Mosaic;
                case "ocr":return Ocr;
                case "translate":return Translation;
                case "scroll":return Scrolling;
                case "pin":return Pin;
                case "library":return Collect;
                default:return false;
            }
        }
    }
}
