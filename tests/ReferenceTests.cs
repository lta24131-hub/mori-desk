using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace MoriDesk {
 public static class ReferenceTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static DragEventArgs Drop(IDataObject data,UIElement target){var args=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),Flags,null,new object[]{data,DragDropKeyStates.None,DragDropEffects.Copy|DragDropEffects.Link|DragDropEffects.Move,target,new Point(10,10)},System.Globalization.CultureInfo.InvariantCulture);args.RoutedEvent=UIElement.PreviewDropEvent;return args;}
  public static void Run(string root,Action<bool,string> check){
   string source=Path.Combine(root,"desktop-folder");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"inside.mp4"),"synthetic video");
   string photo=Path.Combine(root,"reference-photo.png");var pixels=new byte[640*480*4];for(int y=0;y<480;y++)for(int x=0;x<640;x++){int pos=(y*640+x)*4;pixels[pos]=(byte)(130+y/5);pixels[pos+1]=(byte)(60+x/5);pixels[pos+2]=(byte)(35+y/8);pixels[pos+3]=255;}var bmp=BitmapSource.Create(640,480,96,96,PixelFormats.Bgra32,null,pixels,640*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using(var stream=File.Create(photo))encoder.Save(stream);
   check(ReferenceThumbnail.CanPreview(new FileReference{Path=photo}),"raster reference has thumbnail");check(!ReferenceThumbnail.CanPreview(new FileReference{Path=source,Directory=true}),"folder thumbnail never scans children");check(!ReferenceThumbnail.CanPreview(new FileReference{Path=Path.Combine(root,"large.step")}),"3D cards do not parse heavy CAD");var thumbnail=ReferenceThumbnail.Read(photo);check(thumbnail.PixelWidth<=256&&thumbnail.PixelHeight<=256&&thumbnail.IsFrozen,"reference image decode bounded and cross-thread safe");using(var stream=File.Open(photo,FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(stream.Length>0,"thumbnail releases original file handle");
   using(var lib=new Mori.Library(Path.Combine(root,"ref-card-library"))){
    var store=new ReferenceStore(lib.Db);string category=store.AddFolder("设计参考");
    var main=new Mori.MainWindow(lib);main.PreviewMode=true;
    var data=new DataObject(DataFormats.FileDrop,new[]{source});check(Mori.MainWindow.IsLocalFileDrop(data),"external folder defaults to local references");data.SetData("Mori.AssetIds",new[]{"id"});check(!Mori.MainWindow.IsLocalFileDrop(data),"internal library drag keeps classification semantics");
    var entryDrop=Drop(new DataObject(DataFormats.FileDrop,new[]{source,photo}),main.Window);main.Window.RaiseEvent(entryDrop);check(entryDrop.Handled,"desktop drop intercepted before the library importer");check(lib.All().Count==0,"drop from library does not import any assets");check(store.All().Count==2&&store.All().Count(r=>r.Directory)==1,"folder drop creates exactly one folder card");check(Directory.GetFiles(Path.Combine(lib.Root,"原图"),"*",SearchOption.AllDirectories).Length==0,"drop copies no original or folder contents");
    var page=(ReferencePage)typeof(Mori.MainWindow).GetField("referencePage",Flags).GetValue(main);var files=(ListView)typeof(ReferencePage).GetField("files",Flags).GetValue(page);var folders=(ListBox)typeof(ReferencePage).GetField("folders",Flags).GetValue(page);
    check(Grid.GetColumn(page)==2&&Grid.GetColumnSpan(page)==3,"local page keeps main navigation visible");check(((Button)main.Window.FindName("All")).Visibility==Visibility.Visible,"main navigation remains available");check(((Button)main.Window.FindName("Import")).Visibility==Visibility.Collapsed,"confusing import button hidden on local page");check(files.View==null&&files.ItemTemplate!=null,"local file list uses visual cards rather than columns");
    page.CollectPaths(new[]{photo},category,true);check(store.All().Single(r=>r.Path==photo).Folder==category,"explicit drop to category moves an existing reference");check(store.All().Count==2,"category drop never duplicates reference");typeof(ReferencePage).GetMethod("Undo",Flags).Invoke(page,null);check(store.All().Single(r=>r.Path==photo).Folder=="","category move and add is a single undo step");page.Refresh();
    check(folders.Items.Cast<ReferenceFolder>().Single(f=>f.Id=="*").Count==2,"category cards display count");
    main.SetSection("videos");check(page.Visibility==Visibility.Collapsed,"video navigation exits local page");check(((Button)main.Window.FindName("Import")).Visibility==Visibility.Visible,"library toolbar restored on navigation");
    main.CollectLocalPaths(new[]{source});check(store.All().Count==2,"repeated folder drag does not duplicate");check(lib.All().Count==0,"repeated drag never falls through to import");
    check(store.All().Single(r=>r.Directory).Folder=="","external drop from library does not use stale category");
    var body=(FrameworkElement)main.Window.Content;main.Window.Content=null;body.Resources.MergedDictionaries.Add(main.Window.Resources);body.Width=1380;body.Height=860;body.Measure(new Size(1380,860));body.Arrange(new Rect(0,0,1380,860));body.UpdateLayout();
    var target=(ListBoxItem)folders.ItemContainerGenerator.ContainerFromIndex(2);check(target!=null,"folder cards available as drop targets");var itemId=store.All().Single(r=>r.Directory).Id;
    var moveDrop=Drop(new DataObject("MoriDesk.ReferenceIds",new[]{itemId}),target);target.RaiseEvent(moveDrop);check(moveDrop.Handled&&store.All().Single(r=>r.Id==itemId).Folder==category,"routed card drop moves classification and is consumed");
    typeof(ReferencePage).GetMethod("Undo",Flags).Invoke(page,null);check(store.All().Single(r=>r.Id==itemId).Folder=="","drag classification is undoable");
    folders.SelectedIndex=0;body.UpdateLayout();foreach(string theme in new[]{"light","dark"}){Mori.Theme.Set(theme);body.UpdateLayout();var snapshot=new RenderTargetBitmap(1380,860,96,96,PixelFormats.Pbgra32);snapshot.Render(body);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(snapshot));using(var output=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"reference-ui-"+theme+".png")))png.Save(output);}
    main.Window.Close();
   }
   // A large collection must realise only visible cards, not thousands of images.
   var list=new ListView{ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(ReferenceTilePanel)))};
   list.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListView'><ScrollViewer CanContentScroll='True' HorizontalScrollBarVisibility='Disabled'><ItemsPresenter/></ScrollViewer></ControlTemplate>");
   ScrollViewer.SetCanContentScroll(list,true);ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
   list.ItemsSource=Enumerable.Range(0,10000).Select(i=>"File "+i).ToList();list.Measure(new Size(900,500));list.Arrange(new Rect(0,0,900,500));list.UpdateLayout();
   var panel=Mori.MainWindow.VisualChild<ReferenceTilePanel>(list);check(panel!=null,"virtual card panel created");check(VisualTreeHelper.GetChildrenCount(panel)<40,"ten thousand cards only realise visible rows");panel.SetVerticalOffset(20000);list.UpdateLayout();check(panel.VerticalOffset>0&&VisualTreeHelper.GetChildrenCount(panel)<40,"scroll recycles bounded visible cards");
   list.ItemsSource=new[]{"Only one"};list.UpdateLayout();check(panel.VerticalOffset==0&&VisualTreeHelper.GetChildrenCount(panel)==1,"filter shrink clamps virtual scrolling");
  }
 }
}
