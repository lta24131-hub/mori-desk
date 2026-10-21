using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mori {
 public sealed class GalleryTile {
  public Asset Asset {get;set;}
  public double Width {get;set;}
  public double ImageHeight {get;set;}
  public double X {get;set;}
  public double Y {get;set;}
  public bool ShowName {get;set;}
  public bool ShowDetails {get;set;}
  // Keep source identity separate from the format badge and multi-select mark.
  // Extremely narrow/short tiles retain the same explanation in their tooltip.
  public bool ShowSourceBadge {get{return Asset!=null&&Asset.IsReference&&Width>=56&&ImageHeight>=52;}}
  public double LabelHeight {get{return (ShowName?21:0)+(ShowDetails?19:0);}}
 }
 public sealed class AssetRow {
  public List<GalleryTile> Items {get;set;}
  public double Height {get;set;}
  public string Heading {get;set;}
  public double HeadingHeight {get{return string.IsNullOrEmpty(Heading)?0:34;}}
  public double BodyHeight {get{return Height-HeadingHeight;}}
 }
 // Only row metadata is built here. WPF still virtualizes/recycles the rows;
 // changing size never decodes originals or keeps a full-library image cache.
 public static class GalleryLayout {
  public const int DefaultSize=210,MinSize=120,MaxSize=360;
  const double TileOverhead=16; // 12px gap + 4px selection border
  public static int Clamp(int size){return Math.Max(MinSize,Math.Min(MaxSize,size));}
  public static List<AssetRow> BuildShortcuts(IList<Asset> assets,double available,int size){
   var rows=new List<AssetRow>();double scale=Math.Max(.8,Math.Min(1.3,(double)Clamp(size)/DefaultSize));
   foreach(bool folders in new[]{true,false}){
    var group=assets.Where(a=>a.IsDirectory==folders).ToList();double tileWidth=Math.Min(Math.Max(24,available-16),(folders?180:100)*scale),imageHeight=(folders?100:66)*scale;
    int columns=Math.Max(1,(int)(available/(tileWidth+16)));
    for(int i=0;i<group.Count;i+=columns){var row=new AssetRow{Items=new List<GalleryTile>(),Heading=i==0?(folders?"文件夹":"文件"):null};row.Height=imageHeight+39+row.HeadingHeight;
     for(int j=i;j<Math.Min(i+columns,group.Count);j++)row.Items.Add(new GalleryTile{Asset=group[j],Width=tileWidth,ImageHeight=imageHeight,X=(j-i)*(tileWidth+16),ShowName=true,ShowDetails=false});rows.Add(row);
    }
   }return rows;
  }
  public static double Ratio(Asset a){return a.Width>0&&a.Height>0?Math.Max(.35,Math.Min(3,(double)a.Width/a.Height)):1;}
  public static List<AssetRow> Build(IList<Asset> assets,double available,int size){return Build(assets,available,size,false,true,true);}
  public static List<AssetRow> Build(IList<Asset> assets,double available,int size,bool waterfall,bool showName,bool showDetails){
   var rows=new List<AssetRow>();if(assets.Count==0)return rows;
   // Website icons are small identifiers, not image previews. Keep the same
   // selection/virtualization model with compact cards and no empty image box.
   if(assets.All(a=>a.IsBookmark)){
    int columns=Math.Max(1,(int)(available/210));double tileWidth=Math.Max(24,available/columns-12);
    for(int i=0;i<assets.Count;i+=columns){var row=new AssetRow{Items=new List<GalleryTile>(),Height=62};for(int j=i;j<Math.Min(i+columns,assets.Count);j++)row.Items.Add(new GalleryTile{Asset=assets[j],Width=tileWidth,ImageHeight=0,X=(j-i)*(tileWidth+12),ShowName=true,ShowDetails=true});rows.Add(row);}return rows;
   }
   if(waterfall)return Waterfall(assets,available,size,showName,showDetails);
   double width=Math.Max(40,available),target=Clamp(size);int start=0;
   while(start<assets.Count){
    int end=start;double ratios=0;
    while(end<assets.Count){
     double next=ratios+Ratio(assets[end]);int count=end-start+1;
     if(next*target+count*TileOverhead>=width){
      double with=(width-count*TileOverhead)/next;
      double without=end>start?(width-(count-1)*TileOverhead)/ratios:double.PositiveInfinity;
      if(end>start&&Math.Abs(without-target)<Math.Abs(with-target))break;
      ratios=next;end++;break;
     }
     ratios=next;end++;
    }
    int n=end-start;double h=Math.Max(1,(width-n*TileOverhead)/ratios);
    // A sparse last row does not magnify its last image to fill the window.
    if(end==assets.Count)h=Math.Min(target,h);
    var row=new AssetRow{Items=new List<GalleryTile>(),Height=h+14+(showName?21:0)+(showDetails?19:0)};double x=0;
    for(int i=start;i<end;i++){double tileWidth=Ratio(assets[i])*h+4;row.Items.Add(new GalleryTile{Asset=assets[i],Width=tileWidth,ImageHeight=h,X=x,ShowName=showName,ShowDetails=showDetails});x+=tileWidth+12;}
    rows.Add(row);start=end;
   }
   return rows;
  }
  // Continuous shortest-column masonry, sliced into clipped, recycled rows.
  // A tile crossing a slice is represented on both sides; no full-library
  // visual tree, image decode or bitmap allocation happens during layout.
  const double SliceHeight=600;
  static List<AssetRow> Waterfall(IList<Asset> assets,double available,int size,bool names,bool details){
   double width=Math.Max(40,available);int columns=Math.Max(1,(int)(width/(Clamp(size)+12)));
   double pitch=width/columns,tileWidth=pitch-12;var bottoms=new double[columns];var rows=new List<AssetRow>();
   foreach(var asset in assets){
    int column=0;for(int c=1;c<columns;c++)if(bottoms[c]<bottoms[column])column=c;
    double top=bottoms[column],height=(tileWidth-4)/Ratio(asset),bottom=top+height+14+(names?21:0)+(details?19:0);
    int first=(int)(top/SliceHeight),last=(int)Math.Floor((bottom-.00001)/SliceHeight);
    while(rows.Count<=last)rows.Add(new AssetRow{Items=new List<GalleryTile>(),Height=SliceHeight});
    for(int row=first;row<=last;row++)rows[row].Items.Add(new GalleryTile{Asset=asset,Width=tileWidth,ImageHeight=height,X=column*pitch,Y=top-row*SliceHeight,ShowName=names,ShowDetails=details});
    bottoms[column]=bottom;
   }
   if(rows.Count>0)rows[rows.Count-1].Height=bottoms.Max()-(rows.Count-1)*SliceHeight;
   return rows;
  }
  public static bool HandleWheel(Slider slider,MouseWheelEventArgs e,ModifierKeys modifiers){
   if((modifiers&ModifierKeys.Control)==0||e.Delta==0)return false;
   // Fractional wheel/trackpad deltas accumulate through the slider value.
   slider.Value=Math.Max(MinSize,Math.Min(MaxSize,slider.Value+e.Delta/120.0*20));
   e.Handled=true;return true;
  }
 }
}
