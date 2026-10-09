using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    public static class PinTransforms {
        public static int Normalize(int turns){return (turns%4+4)%4;}
        // Pixel-exact quarter turns / mirrors: no resampling, cumulative blur or
        // source overwrite. Rotation is applied first, then screen-axis mirrors.
        public static BitmapSource Apply(BitmapSource image,int turns,bool horizontal,bool vertical){
            turns=Normalize(turns);if(turns==0&&!horizontal&&!vertical)return image;
            int sw=image.PixelWidth,sh=image.PixelHeight,dw=turns%2==0?sw:sh,dh=turns%2==0?sh:sw;
            var source=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var input=new byte[checked(sw*sh*4)];var output=new byte[input.Length];source.CopyPixels(input,sw*4,0);
            for(int y=0;y<sh;y++)for(int x=0;x<sw;x++){
                int dx=x,dy=y;switch(turns){case 1:dx=sh-1-y;dy=x;break;case 2:dx=sw-1-x;dy=sh-1-y;break;case 3:dx=y;dy=sw-1-x;break;}
                if(horizontal)dx=dw-1-dx;if(vertical)dy=dh-1-dy;
                Buffer.BlockCopy(input,(y*sw+x)*4,output,(dy*dw+dx)*4,4);
            }
            var result=BitmapSource.Create(dw,dh,96,96,PixelFormats.Bgra32,null,output,dw*4);result.Freeze();return result;
        }
        public static void Rotate(PinRecord pin,int direction){pin.QuarterTurns=Normalize(pin.QuarterTurns+direction);bool mirror=pin.FlipHorizontal;pin.FlipHorizontal=pin.FlipVertical;pin.FlipVertical=mirror;}
    }
}
