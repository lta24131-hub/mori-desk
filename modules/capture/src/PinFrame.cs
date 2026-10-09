using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace QingJie {
    // Decoration is a sibling of the image, never an effect on image pixels.
    public sealed class PinFrame : Grid {
        public const double Inset=12;
        readonly Border glow;
        public PinFrame(UIElement content){
            var shadow=new Border {Margin=new Thickness(Inset),Background=Brushes.White,IsHitTestVisible=false,
                Effect=new DropShadowEffect {Color=Colors.Black,BlurRadius=12,ShadowDepth=2,Direction=270,Opacity=.24,RenderingBias=RenderingBias.Performance}};
            Children.Add(shadow);
            var host=new Border {Margin=new Thickness(Inset),Child=content};Children.Add(host);
            glow=new Border {Margin=new Thickness(Inset-.5),BorderThickness=new Thickness(1),BorderBrush=Ui.Brush("#A8D5C3"),IsHitTestVisible=false,Opacity=0,
                Effect=new DropShadowEffect {Color=Color.FromRgb(126,199,168),BlurRadius=6,ShadowDepth=0,Opacity=.48,RenderingBias=RenderingBias.Performance}};
            Children.Add(glow);
        }
        public void SetHovered(bool hovered){glow.Opacity=hovered?.85:0;}
    }
}
