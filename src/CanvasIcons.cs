using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MoriDesk {
 // Lucide's original 24px outlines, rendered as WPF vectors. No icon font,
 // bitmap cache, or drawing-engine dependency. See CanvasIcons-NOTICE.txt.
 internal static class CanvasIcons {
  static readonly Dictionary<string,Geometry> icons=new Dictionary<string,Geometry>();
  static CanvasIcons(){
   Add("select","M4.037 4.688a.495.495 0 0 1 .651-.651l16 6.5a.5.5 0 0 1-.063.947l-6.124 1.58a2 2 0 0 0-1.438 1.435l-1.579 6.126a.5.5 0 0 1-.947.063z");
   Add("pan","M18 11V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2 M14 10V4a2 2 0 0 0-2-2a2 2 0 0 0-2 2v2 M10 10.5V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2v8 M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.86-5.99-2.34l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15");
   Add("stroke","M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z M15 5l4 4");
   Add("line","M5 12h14");Add("arrow","M13 5H19V11 M19 5L5 19");
   var square=new RectangleGeometry(new Rect(3,3,18,18),2,2);square.Freeze();icons.Add("rectangle",square);
   var circle=new EllipseGeometry(new Point(12,12),10,10);circle.Freeze();icons.Add("ellipse",circle);
   Add("text","M12 4v16 M4 7V5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v2 M9 20h6");
   Add("undo","M9 14 4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11");
   Add("fit","M8 3H5a2 2 0 0 0-2 2v3 M21 8V5a2 2 0 0 0-2-2h-3 M3 16v3a2 2 0 0 0 2 2h3 M16 21h3a2 2 0 0 0 2-2v-3");
   Add("pin","M12 17v5 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z");
   Add("plus","M5 12h14 M12 5v14");
   Add("image","M4 3h16a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z M21 15l-5-5L3 21 M6.5 7h.01");
   Add("more","M5 12h.01 M12 12h.01 M19 12h.01");
   Add("eye","M2 12c4-9 16-9 20 0-4 9-16 9-20 0z M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0");
   Add("minimize","M5 12h14");Add("maximize","M5 5h14v14H5z");
   Add("restore","M8 3h13v13 M3 8h13v13H3z");Add("close","M6 6l12 12 M6 18L18 6");
  }
  static void Add(string name,string data){var geometry=Geometry.Parse(data);geometry.Freeze();icons.Add(name,geometry);}
  internal static FrameworkElement Create(string name,Control owner,double size){
   Geometry geometry;if(!icons.TryGetValue(name,out geometry))throw new ArgumentException("Unknown canvas icon: "+name);
   var path=new System.Windows.Shapes.Path{Data=geometry,StrokeThickness=1.8,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
   path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new Binding("Foreground"){Source=owner});
   var grid=new Grid{Width=24,Height=24};grid.Children.Add(path);return new Viewbox{Width=size,Height=size,Child=grid,IsHitTestVisible=false};
  }
 }
}
