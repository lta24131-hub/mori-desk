using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace Mori {
 public static class SelectionTests {
  public static void Run(Action<bool,string> check){
   var gallery=new ListBox();
   gallery.Template=new ControlTemplate(typeof(ListBox)){VisualTree=new FrameworkElementFactory(typeof(Grid),"surface")};
   gallery.ApplyTemplate();
   var surface=(Grid)gallery.Template.FindName("surface",gallery);
   check(MainWindow.IsGalleryBlank(gallery,gallery),"gallery padding clears selection");
   check(MainWindow.IsGalleryBlank(surface,gallery),"gallery empty surface clears selection");
   var gap=new Border();surface.Children.Add(gap);
   check(MainWindow.IsGalleryBlank(gap,gallery),"gallery gaps clear selection");
   var label=new TextBlock();var run=new Run("name");label.Inlines.Add(run);
   var tile=new Border{Tag=new Asset(),Child=label};surface.Children.Add(tile);
   check(!MainWindow.IsGalleryBlank(tile,gallery),"tile body keeps selection");
   check(!MainWindow.IsGalleryBlank(label,gallery),"tile label keeps selection");
   check(!MainWindow.IsGalleryBlank(run,gallery),"inline tile text resolves safely");
   foreach(var control in new Control[]{new ScrollBar(),new Thumb(),new Button(),new RepeatButton(),new TextBox(),new ComboBox(),new PasswordBox(),new MenuItem()}){
    surface.Children.Add(control);
    check(!MainWindow.IsGalleryBlank(control,gallery),control.GetType().Name+" never clears gallery selection");
   }
   var buttonLabel=new TextBlock{Text="action"};var button=new Button{Content=buttonLabel};surface.Children.Add(button);
   check(!MainWindow.IsGalleryBlank(buttonLabel,gallery),"nested button content keeps selection");
   check(!MainWindow.IsGalleryBlank(new Border(),gallery),"outside gallery click is ignored");
   check(!MainWindow.IsGalleryBlank(null,gallery),"missing hit target is ignored");
  }
 }
}
