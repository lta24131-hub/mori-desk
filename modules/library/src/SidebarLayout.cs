using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mori {
 public sealed class SidebarLayout {
  public const int DefaultLeft=208,DefaultRight=252,MinLeft=160,MinRight=240,MaxLeft=480,MaxRight=520,MinCenter=480,Handles=12;
  readonly Grid grid;
  readonly ColumnDefinition left,right;
  bool applying;
  int? temporaryRightWidth;
  public int Left {get;private set;}
  public int Right {get;private set;}
  public bool LeftVisible {get;private set;}
  public bool RightVisible {get;private set;}
  public event Action Changed;
  public static int ClampLeft(int value){return Math.Max(MinLeft,Math.Min(MaxLeft,value));}
  public static int ClampRight(int value){return Math.Max(MinRight,Math.Min(MaxRight,value));}
  public static double[] Fit(double width,int left,int right){
   double l=ClampLeft(left),r=ClampRight(right),budget=Math.Max(MinLeft+MinRight,width-MinCenter-Handles);
   if(l+r>budget){double excess=l+r-budget,flex=l-MinLeft+r-MinRight;if(flex>0){l-=excess*(l-MinLeft)/flex;r-=excess*(r-MinRight)/flex;}}
   return new[]{l,r};
  }
  public SidebarLayout(Grid grid,GridSplitter leftHandle,GridSplitter rightHandle,int leftWidth,int rightWidth){
   this.grid=grid;left=grid.ColumnDefinitions[0];right=grid.ColumnDefinitions[4];Left=ClampLeft(leftWidth);Right=ClampRight(rightWidth);LeftVisible=RightVisible=true;
   left.Width=new GridLength(Left);right.Width=new GridLength(Right);
   grid.SizeChanged+=delegate(object s,SizeChangedEventArgs e){if(e.WidthChanged)Apply();};
   Wire(leftHandle,true);Wire(rightHandle,false);Apply();
  }
  void Wire(GridSplitter splitter,bool isLeft){
   splitter.DragCompleted+=delegate(object s,System.Windows.Controls.Primitives.DragCompletedEventArgs e){if(!e.Canceled)Commit(isLeft);};
   splitter.KeyUp+=delegate(object s,KeyEventArgs e){if(e.Key==Key.Left||e.Key==Key.Right)Commit(isLeft);};
   splitter.MouseDoubleClick+=delegate(object s,MouseButtonEventArgs e){SetPreferred(isLeft,isLeft?DefaultLeft:DefaultRight);e.Handled=true;};
  }
  void Commit(bool isLeft){if(applying)return;if(LeftVisible)Left=ClampLeft((int)Math.Round(left.Width.IsAbsolute?left.Width.Value:left.ActualWidth));if(RightVisible&&(!temporaryRightWidth.HasValue||!isLeft)){Right=ClampRight((int)Math.Round(right.Width.IsAbsolute?right.Width.Value:right.ActualWidth));if(temporaryRightWidth.HasValue)temporaryRightWidth=Right;}Apply();if(Changed!=null)Changed();}
  internal void SetVisible(bool isLeft,bool visible){if(isLeft)LeftVisible=visible;else RightVisible=visible;Apply();}
  // A task panel may need breathing room without overwriting the saved details
  // width. Explicit splitter movement still becomes the user's new preference.
  internal void SetTemporaryRightWidth(int? width){temporaryRightWidth=width.HasValue?(int?)ClampRight(width.Value):null;Apply();}
  // Window shrinking only fits the current display; it never loses the user's preferred widths.
  internal void SetPreferred(bool isLeft,int width){if(isLeft)Left=ClampLeft(width);else Right=ClampRight(width);Apply();if(Changed!=null)Changed();}
  internal void Apply(){
   if(applying)return;applying=true;
   try{
   int displayRight=temporaryRightWidth??Right;
    var sizes=LeftVisible&&RightVisible&&grid.ActualWidth>0?Fit(grid.ActualWidth,Left,displayRight):new double[]{Left,displayRight};
    left.MinWidth=LeftVisible?MinLeft:0;right.MinWidth=RightVisible?MinRight:0;
    left.Width=new GridLength(LeftVisible?sizes[0]:0);right.Width=new GridLength(RightVisible?sizes[1]:0);
    grid.ColumnDefinitions[1].Width=new GridLength(LeftVisible?6:0);grid.ColumnDefinitions[3].Width=new GridLength(RightVisible?6:0);
    foreach(UIElement child in grid.Children){int col=Grid.GetColumn(child);if(col==0||col==1)child.Visibility=LeftVisible?Visibility.Visible:Visibility.Collapsed;else if(col==3||col==4)child.Visibility=RightVisible?Visibility.Visible:Visibility.Collapsed;}
   }finally{applying=false;}
  }
 }
}
