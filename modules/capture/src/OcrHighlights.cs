using System.Windows;
using System.Windows.Media;

namespace QingJie {
    // One drawing surface instead of allocating one WPF control per selected word.
    internal sealed class OcrHighlights : FrameworkElement {
        static readonly Brush highlight=CreateBrush();
        OcrPage page;
        int start,end;
        static Brush CreateBrush(){var brush=Ui.Brush("#6680CFA8");brush.Freeze();return brush;}
        public void SetSelection(OcrPage value,int offset,int length){page=value;start=offset;end=offset+length;InvalidateVisual();}
        protected override void OnRender(DrawingContext context){
            context.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
            if(page==null||end<=start)return;
            foreach(var word in page.Words)if(word.TextStart<end&&word.TextStart+word.Text.Length>start)context.DrawRectangle(highlight,null,word.Box);
        }
    }
}
