using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Mori {
 public static class GalleryLayoutTests {
  static List<T> Descendants<T>(DependencyObject root)where T:DependencyObject{var results=new List<T>();for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);var typed=child as T;if(typed!=null)results.Add(typed);results.AddRange(Descendants<T>(child));}return results;}
  static Asset A(int n,int w,int h){return new Asset{Id=n.ToString(),Name="Gallery test "+n,Ext=".png",Width=w,Height=h};}
  static MouseWheelEventArgs Wheel(int delta){return new MouseWheelEventArgs(Mouse.PrimaryDevice,0,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent};}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   var assets=new List<Asset>{A(0,492,900),A(1,1027,787),A(2,658,435),A(3,1440,1801),A(4,748,512),A(5,564,564),A(6,470,470),A(7,483,391),A(8,564,650),A(9,730,592),A(10,1080,807),A(11,750,1000)};
   assets[2].Selected=true;
   check(GalleryLayout.Build(new List<Asset>(),900,210).Count==0,"gallery empty layout has no rows");
   foreach(double width in new[]{40.0,180,540,900,1920})foreach(int size in new[]{120,210,360}){
    var rows=GalleryLayout.Build(assets,width,size);var tiles=rows.SelectMany(r=>r.Items).ToList();
    check(tiles.Select(t=>t.Asset).SequenceEqual(assets),"gallery preserves identity/order at "+width+" / "+size);
    check(rows.All(r=>r.Items.Sum(t=>t.Width+12)<=width+.01&&r.Items.All(t=>t.Width>0&&t.ImageHeight>0)),"gallery rows fit width at "+width+" / "+size);
    check(tiles.All(t=>Math.Abs((t.Width-4)/t.ImageHeight-GalleryLayout.Ratio(t.Asset))<.00001),"gallery aspect ratios retained at "+width+" / "+size);
   }
   check(assets[2].Selected&&assets.Count==12,"relayout leaves selected asset and original data unchanged");
   var last=GalleryLayout.Build(new[]{A(0,640,480)},1200,210)[0];check(last.Items[0].ImageHeight==210&&last.Items[0].Width<300,"sparse last row stays at requested size");
   check(GalleryLayout.Ratio(A(0,0,0))==1&&GalleryLayout.Ratio(A(1,100000,1))==3&&GalleryLayout.Ratio(A(2,1,100000))==.35,"unknown/extreme dimensions use bounded uncropped tile frames");
   var large=Enumerable.Range(0,10000).Select(i=>A(i,640+i%100,480)).ToList();var timer=System.Diagnostics.Stopwatch.StartNew();var largeRows=GalleryLayout.Build(large,1000,210);timer.Stop();
   check(largeRows.Sum(r=>r.Items.Count)==10000&&timer.ElapsedMilliseconds<1500,"10000-item metadata layout completes without image decoding");
   File.WriteAllText(Path.Combine(output,"layout-timing.txt"),"10000 assets layout: "+timer.ElapsedMilliseconds+" ms");
   var slider=new Slider{Minimum=120,Maximum=360,Value=210};var e=Wheel(120);
   check(!GalleryLayout.HandleWheel(slider,e,ModifierKeys.None)&&!e.Handled&&slider.Value==210,"normal wheel is left to list scrolling");
   e=Wheel(120);check(GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control)&&e.Handled&&slider.Value==230,"Ctrl wheel up enlarges thumbnail size");
   e=Wheel(-120);GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control);check(slider.Value==210&&e.Handled,"Ctrl wheel down shrinks thumbnail size");
   e=Wheel(30);GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control);check(slider.Value==215,"high-resolution wheel uses proportional increments");
   e=Wheel(120000);GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control);check(slider.Value==360&&e.Handled,"zoom upper limit consumes wheel without list scrolling");
   e=Wheel(-120000);GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control);check(slider.Value==120&&e.Handled,"zoom lower limit consumes wheel without list scrolling");
   e=Wheel(0);check(!GalleryLayout.HandleWheel(slider,e,ModifierKeys.Control)&&!e.Handled,"zero wheel delta does not force a layout");
   string path=Path.Combine(output,"settings.json");new Settings{LibraryPath=output,Theme="light",ThumbnailSize=290}.SaveTo(path);var settings=Settings.LoadFrom(path);
   check(settings.ThumbnailSize==290&&settings.Theme=="light"&&settings.LibraryPath==output,"size preference survives restart with library/theme unchanged");
   settings.ThumbnailSize=9999;check(settings.ThumbnailSize==360,"oversize preference bounded");settings.ThumbnailSize=-10;check(settings.ThumbnailSize==120,"undersize preference bounded");
   var json=new System.Web.Script.Serialization.JavaScriptSerializer();
   File.WriteAllText(path,json.Serialize(new{LibraryPath=output,ThumbnailSize="broken",Theme="light"}));check(Settings.LoadFrom(path).ThumbnailSize==210&&Settings.LoadFrom(path).Theme=="light","malformed size falls back without losing theme");
   File.WriteAllText(path,json.Serialize(new{LibraryPath=output,Theme="light"}));check(Settings.LoadFrom(path).ThumbnailSize==210,"legacy preferences get larger default thumbnails");
   // Exercise real templates, never send input to the desktop.
   var app=Application.Current??new Application();var old=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Window window=null;
   try{
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))window=(Window)XamlReader.Load(stream);
    var content=(FrameworkElement)window.Content;var gallery=(ListBox)window.FindName("Gallery");((StackPanel)window.FindName("Empty")).Visibility=Visibility.Collapsed;
    foreach(string theme in new[]{"dark","light"}){
     Theme.Set(theme);gallery.ItemsSource=GalleryLayout.Build(assets,885,210);content.Measure(new Size(1380,900));content.Arrange(new Rect(0,0,1380,900));content.UpdateLayout();
     var tiles=Descendants<Border>(gallery).Where(b=>b.Tag is Asset).ToList();check(tiles.Count>0&&tiles.All(b=>b.Padding==new Thickness(0)),theme+" gallery uses image-first unpadded tiles");
     var selected=tiles.Single(b=>((Asset)b.Tag).Selected);check(selected.BorderBrush==Theme.Brush("Accent"),theme+" selection outline binds to underlying asset");
     check(tiles.All(b=>!MainWindow.IsGalleryBlank(b,gallery)),theme+" variable-width tiles remain selectable");
     check(Descendants<ThumbnailImage>(gallery).All(i=>i.DataContext is Asset&&i.Stretch==Stretch.Uniform),theme+" images receive assets without cropping");
     var sizeSlider=(Slider)window.FindName("ThumbnailSize");sizeSlider.ApplyTemplate();check(sizeSlider.Template.FindName("PART_Track",sizeSlider)!=null,theme+" size slider has working native track");
    }
    gallery.ItemsSource=GalleryLayout.Build(large,885,210);content.UpdateLayout();check(Descendants<Border>(gallery).Count(b=>b.Tag is Asset)<100,"10000-asset gallery materializes only visible/adjacent rows");
    check(VirtualizingPanel.GetIsVirtualizing(gallery)&&VirtualizingPanel.GetScrollUnit(gallery)==ScrollUnit.Pixel,"gallery retains virtualization and pixel scrolling");
    check(!window.IsVisible,"layout tests do not interrupt the desktop");
   }finally{if(window!=null)window.Close();Theme.Set("dark");app.ShutdownMode=old;}
  }
 }
}
