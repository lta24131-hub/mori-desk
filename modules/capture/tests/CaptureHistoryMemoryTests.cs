using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

internal static class CaptureHistoryMemoryTests {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Field(object target,string name){return target.GetType().GetField(name,Hidden).GetValue(target);}
    static object Call(CaptureWindow target,string name,params object[] args){return typeof(CaptureWindow).GetMethod(name,Hidden).Invoke(target,args);}
    sealed class Fixture:IDisposable {
        internal CaptureWindow Window;internal ScreenshotSurface Surface;internal Annotation Mark;
        internal Fixture(){
            var image=BitmapSource.Create(800,560,96,96,PixelFormats.Bgra32,null,new byte[800*560*4],3200);image.Freeze();
            Window=new CaptureWindow(new Snapshot{Image=image,Bounds=new System.Drawing.Rectangle(0,0,800,560)},null,new CaptureFeatures{SmartSelection=false});Surface=(ScreenshotSurface)Field(Window,"surface");
            var root=(Grid)Window.Content;root.Measure(new Size(800,560));root.Arrange(new Rect(0,0,800,560));Surface.Selection=new Rect(20,20,760,480);
            Mark=new Annotation{Kind="pen",Width=4,Start=new Point(100,100),End=new Point(300,100)};
            for(int i=0;i<10000;i++)Mark.Points.Add(new Point(100+i*.02,100+i%2));
            Surface.Marks.Add(Mark);Call(Window,"RecordAnnotationAdded",Mark);Call(Window,"SelectShape",Mark);
        }
        internal void Wheel(int delta){Window.HandleCaptureWheel(delta,ModifierKeys.Control,new Point(400,300));}
        public void Dispose(){Window.Close();AppState.Captures.Remove(Window);}
    }
    internal static void Run(Action<bool,string> check){
        var original=AppState.Settings;string previous=Preferences.TestFilePath;string temp=Path.Combine(Path.GetTempPath(),"capture-history-"+Guid.NewGuid().ToString("N"));Preferences.TestFilePath=Path.Combine(temp,"settings.json");AppState.Settings=new Preferences();
        try{
            using(var f=new Fixture()){
                f.Wheel(120);var history=Field(f.Window,"wheelHistory");var after=(Annotation)Field(history,"After");var points=after.Points;
                f.Wheel(120);check(ReferenceEquals(after,Field(history,"After"))&&ReferenceEquals(points,((Annotation)Field(history,"After")).Points),"one wheel burst reuses its private 10000-point snapshot");
                check(!ReferenceEquals(points,f.Mark.Points),"history retains an independent point buffer");
                f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(f.Mark.Width==4&&f.Mark.Points.Count==10000,"undo restores complete stroke after merged wheel edits");
                f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);check(f.Mark.Width==6&&f.Mark.Points.Count==10000,"redo restores final width and original points");
                Call(f.Window,"SelectShape",f.Mark);f.Wheel(120);Call(f.Window,"ApplyAnnotationColor","#2563EB");f.Wheel(120);
                f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(f.Mark.Width==7&&f.Mark.Color=="#2563EB","wheel edits do not coalesce across an intervening color edit");
                f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(f.Mark.Color!="#2563EB"&&f.Mark.Width==7,"intervening edit keeps its independent undo");
            }
            using(var f=new Fixture()){
                f.Wheel(120);f.Wheel(-120);check(f.Mark.Width==4,"wheel round trip returns to original size");
                f.Window.HandleCaptureKey(Key.Z,ModifierKeys.Control);check(f.Surface.Marks.Count==0,"round trip leaves no empty undo: next Undo removes the stroke");
                f.Window.HandleCaptureKey(Key.Y,ModifierKeys.Control);check(f.Surface.Marks.Count==1&&f.Mark.Width==4,"redo after round trip restores the original stroke");
            }
            using(var f=new Fixture()){
                f.Wheel(120);Call(f.Window,"ClearAnnotationHistory");
                check(Field(f.Window,"wheelHistory")==null&&Field(f.Window,"wheelTarget")==null,"clearing history releases wheel references to stroke snapshots");
            }
            using(var f=new Fixture()){
                f.Wheel(120);f.Window.Close();
                check(Field(f.Window,"wheelHistory")==null&&Field(f.Window,"wheelTarget")==null,"closed capture releases wheel snapshot and target");
            }
        }finally{AppState.Settings=original;Preferences.TestFilePath=previous;if(File.Exists(Path.Combine(temp,"settings.json")))File.Delete(Path.Combine(temp,"settings.json"));if(Directory.Exists(temp))Directory.Delete(temp);}
    }
}
