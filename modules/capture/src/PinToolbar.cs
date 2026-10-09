using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace QingJie {
    // Separate popup keeps controls completely outside the image and shadow.
    public sealed class PinToolbar {
        readonly Popup popup;
        readonly FrameworkElement target;
        readonly DispatcherTimer hide=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(220)};
        readonly Action<bool> highlight;
        public readonly StackPanel Tools=new StackPanel {Orientation=Orientation.Horizontal};
        public bool MenuOpen;
        bool disposed;
        public PinToolbar(FrameworkElement target,Action<bool> highlight){
            this.target=target;this.highlight=highlight;
            var card=Ui.Card(Tools);card.Padding=new Thickness(3);card.CornerRadius=new CornerRadius(5);
            popup=new Popup {Child=card,PlacementTarget=target,Placement=PlacementMode.Custom,AllowsTransparency=true,StaysOpen=true,Focusable=false};
            popup.CustomPopupPlacementCallback=(size,anchor,offset)=>new[]{
                new CustomPopupPlacement(new Point(Math.Max(0,anchor.Width-size.Width),-size.Height-5),PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(Math.Max(0,anchor.Width-size.Width),anchor.Height+5),PopupPrimaryAxis.Horizontal)};
            target.MouseEnter+=(s,e)=>Show();target.MouseLeave+=(s,e)=>ScheduleHide();
            card.MouseEnter+=(s,e)=>{hide.Stop();highlight(true);};card.MouseLeave+=(s,e)=>ScheduleHide();
            hide.Tick+=(s,e)=>{hide.Stop();if(!target.IsMouseOver&&!card.IsMouseOver&&!MenuOpen){popup.IsOpen=false;highlight(false);}};
        }
        public void Show(){if(disposed)return;hide.Stop();popup.IsOpen=true;highlight(true);}
        public void ScheduleHide(){if(disposed)return;hide.Stop();hide.Start();}
        public void Hide(){hide.Stop();popup.IsOpen=false;highlight(false);}
        public void Reposition(){if(popup.IsOpen){popup.HorizontalOffset=.01;popup.HorizontalOffset=0;}}
        public void Dispose(){disposed=true;hide.Stop();popup.IsOpen=false;}
        public static Button Button(string icon,string title,Action action){return Ui.Tool(icon,title,action,26);}
    }
}
