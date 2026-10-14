using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Mori {
 internal enum PreviewBackground { Automatic,LightChecker,DarkChecker,White,Dark }
 internal static class PreviewBackgrounds {
  internal const int CellSize=12;
  internal static readonly Color LightA=Color.FromArgb(240,242,244),LightB=Color.FromArgb(226,230,234);
  internal static readonly Color DarkA=Color.FromArgb(46,51,58),DarkB=Color.FromArgb(59,65,73),SolidDark=Color.FromArgb(29,31,34);
  internal static string Label(PreviewBackground mode){switch(mode){case PreviewBackground.LightChecker:return "浅色底纹";case PreviewBackground.DarkChecker:return "深色底纹";case PreviewBackground.White:return "纯白";case PreviewBackground.Dark:return "深灰";default:return "自动（透明底纹）";}}
  // Inspect decoder metadata, not millions of pixels. An opaque image stored
  // in an alpha-capable format simply paints over its entire backdrop.
  internal static bool MayHaveAlpha(Image image){return Image.IsAlphaPixelFormat(image.PixelFormat)||(image.Flags&((int)ImageFlags.HasAlpha|(int)ImageFlags.HasTranslucent))!=0;}
  internal static TextureBrush Checker(bool dark){int cell=CellSize;using(var tile=new Bitmap(cell*2,cell*2)){
   using(var graphics=Graphics.FromImage(tile))using(var accent=new SolidBrush(dark?DarkB:LightB)){graphics.Clear(dark?DarkA:LightA);graphics.FillRectangle(accent,cell,0,cell,cell);graphics.FillRectangle(accent,0,cell,cell,cell);}
   return new TextureBrush(tile,WrapMode.Tile);
  }}
 }
}
