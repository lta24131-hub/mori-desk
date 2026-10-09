using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Automation;

namespace QingJie {
    public static class Ui {
        static ImageSource appIcon;
        public static ImageSource AppIcon {get{if(appIcon==null){using(var stream=typeof(Ui).Assembly.GetManifestResourceStream("QingJie.Icon.png")){var icon=System.Windows.Media.Imaging.BitmapFrame.Create(stream,System.Windows.Media.Imaging.BitmapCreateOptions.None,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);icon.Freeze();appIcon=icon;}}return appIcon;}}
        public static SolidColorBrush Brush(string color) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); b.Freeze(); return b; }
        public static readonly Brush Green = Brush("#07A56B");
        public static readonly Brush Ink = Brush("#303639");
        public static Button TranslationTarget(string initial,Action<string> changed){
            string target=initial=="en"?"en":"zh";
            // Keep target-language text in the menu, not in the icon toolbar.
            var button=Tool("translate","翻译目标语言",()=>{});
            button.Width=18;button.Padding=new Thickness(0);button.VerticalAlignment=VerticalAlignment.Center;
            button.Content=new Viewbox {Width=12,Height=12,Child=new ToolIcon("chevron")};
            var menu=new ContextMenu {PlacementTarget=button,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom,FontSize=13};
            var chinese=new MenuItem {Header="译成中文",IsCheckable=true};var english=new MenuItem {Header="译成英文",IsCheckable=true};menu.Items.Add(chinese);menu.Items.Add(english);button.ContextMenu=menu;
            Action refresh=()=>{string current=target=="en"?"英文":"中文";chinese.IsChecked=target=="zh";english.IsChecked=target=="en";button.ToolTip="当前译成"+current+" · 选择目标语言";AutomationProperties.SetName(button,"翻译目标："+current+"，展开语言选项");};
            Action<string> select=value=>{bool different=target!=value;target=value;refresh();if(different)changed(target);};
            chinese.Click+=(s,e)=>select("zh");english.Click+=(s,e)=>select("en");menu.Opened+=(s,e)=>refresh();
            refresh();button.Click+=(s,e)=>{refresh();menu.IsOpen=true;};return button;
        }
        public static Button Tool(string icon,string name,Action click,double size=36) {
            var b=new Button { Content=new ToolIcon(icon), Width=36, Height=36, Padding=new Thickness(7), Margin=new Thickness(1,0,1,0), Cursor=System.Windows.Input.Cursors.Hand, ToolTip=name, Background=Brushes.Transparent, BorderThickness=new Thickness(0), Foreground=Ink, Focusable=false };
            if(size!=36){b.Width=size;b.Height=size;b.Padding=new Thickness(4);b.Margin=new Thickness(0);b.Content=new Viewbox {Width=16,Height=16,Child=new ToolIcon(icon)};}
            var border=new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty,new CornerRadius(5)); border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
            var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);
            b.Template=new ControlTemplate(typeof(Button)){VisualTree=border};
            b.MouseEnter+=(s,e)=> { if(b.Tag==null) b.Background=Brush("#EEF1F2"); };
            b.MouseLeave+=(s,e)=> { if(b.Tag==null) b.Background=Brushes.Transparent; };
            b.Click+=(s,e)=>click(); AutomationProperties.SetName(b,name); return b;
        }
        public static Button TextButton(string text,Action click,bool primary=false) {
            var b=new Button { Content=text,Padding=new Thickness(13,7,13,7),Margin=new Thickness(4),Background=primary?Green:Brushes.White,Foreground=primary?Brushes.White:Ink,BorderBrush=Brush("#D9DFDF"),BorderThickness=new Thickness(1),Cursor=System.Windows.Input.Cursors.Hand,FontSize=13 };
            b.Click+=(s,e)=>click(); return b;
        }
        public static Border Card(UIElement child) { return new Border {Child=child,Background=Brushes.White,BorderBrush=Brush("#DDE3E1"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),Padding=new Thickness(5)}; }
        public static void Separator(Panel p) { p.Children.Add(new Border {Width=1,Height=22,Background=Brush("#E4E7E7"),Margin=new Thickness(6,0,6,0),VerticalAlignment=VerticalAlignment.Center}); }
    }
    // Original vector glyphs. No resources are copied from WeChat or Snipaste.
    public sealed class ToolIcon : FrameworkElement {
        readonly string kind;
        public ToolIcon(string kind) {this.kind=kind;Width=20;Height=20;IsHitTestVisible=false;}
        protected override void OnRender(DrawingContext d) {
            var brush=(kind=="done"||kind=="ocr")?Ui.Green:Ui.Ink; if(kind=="close")brush=Ui.Brush("#DD5863");
            var p=new Pen(brush,1.5);p.StartLineCap=PenLineCap.Round;p.EndLineCap=PenLineCap.Round;p.LineJoin=PenLineJoin.Round;
            Action<double,double,double,double> line=(x,y,x2,y2)=>d.DrawLine(p,new Point(x,y),new Point(x2,y2));
            switch(kind) {
                case "rect":d.DrawRoundedRectangle(null,p,new Rect(3,4,14,12),1,1);break;
                case "ellipse":d.DrawEllipse(null,p,new Point(10,10),7,7);break;
                case "arrow":line(3,17,16,4);line(8,4,16,4);line(16,4,16,12);break;
                case "line":line(3,17,17,3);break;
                case "chevron":line(5,8,10,13);line(10,13,15,8);break;
                case "pen":line(4,15,14,5);line(6,17,16,7);line(4,15,3,18);line(3,18,6,17);line(14,5,16,7);break;
                case "text":line(3,4,17,4);line(10,4,10,17);line(7,17,13,17);break;
                case "mosaic":for(int y=0;y<4;y++)for(int x=0;x<4;x++)d.DrawRectangle((x+y)%2==0?Ui.Ink:Ui.Brush("#ADB5B5"),null,new Rect(3+x*3.5,3+y*3.5,3.5,3.5));break;
                case "undo":line(8,4,3,8);line(3,8,8,12);line(3,8,13,8);var g=System.Windows.Media.Geometry.Parse("M 13,8 C 19,8 19,17 10,17");d.DrawGeometry(null,p,g);break;
                case "ocr":d.DrawRoundedRectangle(null,p,new Rect(2,2,16,16),2,2);line(6,6,14,6);line(10,6,10,14);line(7,14,13,14);break;
                case "translate":line(2,5,12,5);line(7,2,7,5);line(4,6,10,13);line(10,5,3,14);line(10,18,14,8);line(14,8,18,18);line(12,14,16,14);break;
                case "pin":line(7,3,13,3);line(8,3,7,9);line(12,3,13,9);line(7,9,4,12);line(13,9,16,12);line(4,12,16,12);line(10,12,10,18);break;
                case "save":line(10,2,10,13);line(6,9,10,13);line(10,13,14,9);line(3,12,3,18);line(3,18,17,18);line(17,18,17,12);break;
                case "close":line(4,4,16,16);line(4,16,16,4);break;
                case "done":line(3,10,8,15);line(8,15,17,5);break;
                case "copy":d.DrawRectangle(null,p,new Rect(6,6,11,11));line(3,14,3,3);line(3,3,14,3);break;
                case "rotate":d.DrawGeometry(null,p,System.Windows.Media.Geometry.Parse("M 4,7 C 6,1 16,2 17,9 M 14,7 L 17,10 L 19,6 M 6,9 L 12,11 L 10,17 L 4,15 Z"));break;
                case "select":line(3,8,3,3);line(3,3,8,3);line(12,3,17,3);line(17,3,17,8);line(17,12,17,17);line(17,17,12,17);line(8,17,3,17);line(3,17,3,12);break;
            }
        }
    }
}
