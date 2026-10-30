using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mori {
 // A virtual strip: no per-document collection of controls or eager decodes.
 internal sealed class PdfThumbnailPane:Border,IDisposable {
  const double RowHeight=176;
  readonly PdfContinuousView view;
  readonly ScrollViewer scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
  readonly Canvas canvas=new Canvas();
  readonly Dictionary<int,Button> tiles=new Dictionary<int,Button>();
  bool open,closed,refreshing;int selected=-1;
  internal int VisibleCount{get{return tiles.Count;}}
  internal ScrollViewer Scroller{get{return scroll;}}
  internal PdfThumbnailPane(PdfContinuousView view){
   this.view=view;Name="PdfThumbnailPane";Width=164;BorderThickness=new Thickness(0,0,1,0);SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Border");
   var body=new DockPanel();var header=new TextBlock{Text="页面",Margin=new Thickness(16,12,0,10),FontSize=12};header.SetResourceReference(TextBlock.ForegroundProperty,"Muted");DockPanel.SetDock(header,Dock.Top);body.Children.Add(header);scroll.Content=canvas;body.Children.Add(scroll);Child=body;
   scroll.ScrollChanged+=delegate(object sender,ScrollChangedEventArgs e){if(open&&selected>=0&&e.ViewportHeightChange!=0)ScrollTo(selected);Refresh();};view.ThumbnailChanged+=UpdateImages;view.PageChanged+=OnPage;
  }
  void OnPage(int page,int total){UpdatePage(page);}
  internal void SetOpen(bool value){if(closed)return;open=value;Visibility=value?Visibility.Visible:Visibility.Collapsed;if(value){UpdateLayout();ScrollTo(view.CurrentPage);Refresh();}else Clear();}
  internal void UpdatePage(int page){if(selected==page){if(open)Refresh();return;}selected=page;if(open){ScrollTo(page);Refresh();}}
  void ScrollTo(int page){double top=page*RowHeight;if(top<scroll.VerticalOffset)scroll.ScrollToVerticalOffset(top);else if(top+RowHeight>scroll.VerticalOffset+scroll.ViewportHeight)scroll.ScrollToVerticalOffset(Math.Max(0,top+RowHeight-scroll.ViewportHeight));}
  void Refresh(){
   if(closed||!open||refreshing)return;refreshing=true;
   try{
    canvas.Height=view.PageCount*RowHeight;canvas.Width=142;
    int first=Math.Max(0,(int)(scroll.VerticalOffset/RowHeight)-1);int last=Math.Min(view.PageCount-1,(int)((scroll.VerticalOffset+Math.Max(1,scroll.ViewportHeight))/RowHeight)+1);
    int[] wanted=last<first?new int[0]:Enumerable.Range(first,Math.Min(12,last-first+1)).ToArray();
    foreach(int page in tiles.Keys.ToArray())if(!wanted.Contains(page)){canvas.Children.Remove(tiles[page]);tiles.Remove(page);}
    foreach(int page in wanted){Button button;if(!tiles.TryGetValue(page,out button)){
      int target=page;var image=new Image{Width=112,Height=140,Stretch=Stretch.Uniform};var caption=new TextBlock{Text=(page+1).ToString(),FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,5,0,0)};caption.SetResourceReference(TextBlock.ForegroundProperty,"Text");
      var panel=new StackPanel();panel.Children.Add(image);panel.Children.Add(caption);button=new Button{Content=panel,Width=128,Height=166,Padding=new Thickness(4),ToolTip="第 "+(page+1)+" 页"};AutomationProperties.SetName(button,"第 "+(page+1)+" 页");button.Click+=delegate{view.GoTo(target);view.Scroller.Focus();};tiles[page]=button;canvas.Children.Add(button);Canvas.SetLeft(button,7);Canvas.SetTop(button,page*RowHeight);
     }
     button.SetResourceReference(Control.BackgroundProperty,page==view.CurrentPage?"AccentSoft":"Panel");
    }
    view.SetThumbnailPages(wanted);UpdateImages();
   }finally{refreshing=false;}
  }
  void UpdateImages(){if(closed)return;foreach(var pair in tiles){var image=(Image)((StackPanel)pair.Value.Content).Children[0];image.Source=view.Thumbnail(pair.Key);}}
  void Clear(){foreach(var button in tiles.Values)((Image)((StackPanel)button.Content).Children[0]).Source=null;tiles.Clear();canvas.Children.Clear();view.SetThumbnailPages(new int[0]);}
  public void Dispose(){if(closed)return;Clear();closed=true;view.ThumbnailChanged-=UpdateImages;view.PageChanged-=OnPage;}
 }
}
