using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    // Geometry-only fallback for visible rectangular cards/panels in applications
    // whose controls have no HWND (WPF, Chromium, Electron). Never OCRs text or
    // queries another application's accessibility provider. Only rectangles survive
    // analysis; temporary downsampled pixels are released when analysis finishes.
    internal sealed class CaptureVisualRegions {
        struct Region {internal Rect Bounds;internal bool Aggregate;}
        readonly List<Region> regions=new List<Region>();
        double toleranceX=4,toleranceY=4;
        const int MaximumRegions=256,MaximumLines=1800;
        struct EdgeRun {internal int Left,Right,Y;}
        internal int Count {get{return regions.Count;}}
        internal IEnumerable<Rect> At(Point point,Rect visible){
            foreach(var region in regions){var rect=region.Bounds;if(CaptureTargets.Contains(rect,point)&&visible.Contains(rect))yield return rect;}
        }
        internal static CaptureVisualRegions Read(BitmapSource image,System.Drawing.Rectangle screen){
            return Read(image,screen,CancellationToken.None);
        }
        internal static CaptureVisualRegions Read(BitmapSource image,System.Drawing.Rectangle screen,CancellationToken cancel){
            var result=new CaptureVisualRegions();
            cancel.ThrowIfCancellationRequested();
            if(image==null||image.PixelWidth<24||image.PixelHeight<16||screen.Width<=0||screen.Height<=0)return result;
            var watch=Stopwatch.StartNew();
            try{
                // At most about 1.3 million pixels even on a 4K/8K desktop. Sampling
                // cost is per capture, not per cursor move, and keeps no bitmap copy.
                double scale=Math.Min(1,Math.Min(1440.0/image.PixelWidth,900.0/image.PixelHeight));
                BitmapSource source=image;
                if(scale<1)source=new TransformedBitmap(source,new ScaleTransform(scale,scale));
                if(source.Format!=PixelFormats.Bgra32)source=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
                int width=source.PixelWidth,height=source.PixelHeight,stride=width*4;
                result.toleranceX=Math.Max(4,2.0*screen.Width/width+1);
                result.toleranceY=Math.Max(4,2.0*screen.Height/height+1);
                var pixels=new byte[stride*height];source.CopyPixels(pixels,stride,0);
                int minWidth=Math.Max(14,(int)Math.Ceiling(24*scale)),minHeight=Math.Max(10,(int)Math.Ceiling(18*scale));
                var lines=new List<EdgeRun>();
                var buckets=new Dictionary<int,List<EdgeRun>>();
                for(int y=1;y<height&&lines.Count<MaximumLines&&result.regions.Count<MaximumRegions;y++){
                    cancel.ThrowIfCancellationRequested();if(watch.ElapsedMilliseconds>55)break;
                    for(int x=1;x<width-1;){
                        if(!Edge(pixels,y*stride+x*4,(y-1)*stride+x*4)){x++;continue;}
                        int left=x,last=x,gaps=0;x++;
                        for(;x<width-1;x++){
                            if(Edge(pixels,y*stride+x*4,(y-1)*stride+x*4)){last=x;gaps=0;}
                            else if(++gaps>2)break;
                        }
                        // A border has a quiet strip on at least one side. Dense photo/
                        // video texture creates long gradients too, but not a flat edge.
                        if(last-left+1<minWidth||(!QuietRow(pixels,stride,y,left,last)&&!QuietRow(pixels,stride,y-1,left,last)))continue;
                        if(watch.ElapsedMilliseconds>55)return result;
                        var run=new EdgeRun {Left=left,Right=last,Y=y};lines.Add(run);
                        int key=left/4;Rect nearest=Rect.Empty,outer=Rect.Empty;
                        for(int k=key-1;k<=key+1;k++){
                            List<EdgeRun> previous;if(!buckets.TryGetValue(k,out previous))continue;
                            // Keep an atomic row and its outer container, not every
                            // combination of table/list separator lines (O(n^2)).
                            for(int i=previous.Count-1,attempts=0;i>=0&&attempts<40;i--){
                                if(!Matches(run,previous[i],minHeight))continue;
                                if((attempts&7)==0&&watch.ElapsedMilliseconds>55)return result;
                                var candidate=Rectangle(pixels,width,stride,run,previous[i],minHeight,screen,height);attempts++;
                                if(candidate.IsEmpty)continue;
                                if(nearest.IsEmpty||candidate.Height<nearest.Height)nearest=candidate;break;
                            }
                            for(int i=0,attempts=0;i<previous.Count&&attempts<40;i++){
                                if(!Matches(run,previous[i],minHeight))continue;
                                if((attempts&7)==0&&watch.ElapsedMilliseconds>55)return result;
                                var candidate=Rectangle(pixels,width,stride,run,previous[i],minHeight,screen,height);attempts++;
                                if(candidate.IsEmpty)continue;
                                if(outer.IsEmpty||candidate.Height>outer.Height)outer=candidate;break;
                            }
                        }
                        if(!nearest.IsEmpty)result.Add(nearest,false);
                        if(!outer.IsEmpty&&outer!=nearest)result.Add(outer,true);
                        List<EdgeRun> group;if(!buckets.TryGetValue(key,out group)){group=new List<EdgeRun>();buckets[key]=group;}
                        group.Add(run);
                        if(group.Count>80)group.RemoveAt(1); // Preserve the first outer boundary plus recent rows.
                        if(lines.Count>=MaximumLines||result.regions.Count>=MaximumRegions)break;
                    }
                }
            }catch(OperationCanceledException){throw;}
            catch{ /* The native window chain and manual drag always remain usable. */ }
            return result;
        }
        static bool Edge(byte[] pixels,int a,int b){
            return Math.Abs(pixels[a]-pixels[b])>=6||Math.Abs(pixels[a+1]-pixels[b+1])>=6||Math.Abs(pixels[a+2]-pixels[b+2])>=6;
        }
        static bool Matches(EdgeRun bottom,EdgeRun top,int minHeight){return Math.Abs(top.Left-bottom.Left)<=2&&Math.Abs(top.Right-bottom.Right)<=2&&bottom.Y-top.Y>=minHeight;}
        static Rect Rectangle(byte[] pixels,int width,int stride,EdgeRun bottom,EdgeRun top,int minHeight,System.Drawing.Rectangle screen,int height){
            if(!Matches(bottom,top,minHeight))return Rect.Empty;
            // Rounded cards have a shorter straight top edge. Find their true
            // vertical sides beyond the corners, including short rounded buttons.
            int left=FindSide(pixels,width,stride,Math.Min(bottom.Left,top.Left),top.Y,bottom.Y,-1);
            if(left<0)return Rect.Empty;
            int right=FindSide(pixels,width,stride,Math.Max(bottom.Right,top.Right)+1,top.Y,bottom.Y,1);
            if(right<0)return Rect.Empty;
            int corners=Math.Max(Math.Min(bottom.Left,top.Left)-left,right-Math.Max(bottom.Right,top.Right)-1)+2;
            if(!Side(pixels,stride,left,top.Y,bottom.Y,corners)||!Side(pixels,stride,right,top.Y,bottom.Y,corners))return Rect.Empty;
            double sx=(double)screen.Width/width,sy=(double)screen.Height/height;
            return new Rect(screen.X+left*sx,screen.Y+top.Y*sy,(right-left)*sx,(bottom.Y-top.Y)*sy);
        }
        static int FindSide(byte[] pixels,int width,int stride,int x,int top,int bottom,int direction){
            int maximum=Math.Min(64,Math.Max(18,(bottom-top)/2-2));
            for(int distance=0;distance<=maximum;distance++){
                int actual=x+direction*distance;if(actual<1||actual>=width)continue;
                if(Side(pixels,stride,actual,top,bottom,distance+2))return actual;
            }
            return -1;
        }
        static bool Side(byte[] pixels,int stride,int x,int top,int bottom,int corner){
            // Exclude the estimated corner arc, then require a continuous fixed-x
            // central side (not isolated text strokes anywhere along the height).
            int start=top+corner,end=bottom-corner;if(end-start<3)return false;
            int samples=0,hits=0,longest=0,consecutive=0,step=Math.Max(1,(end-start)/40);
            for(int y=start;y<end;y+=step){
                samples++;int at=y*stride+x*4;bool hit=Edge(pixels,at,at-4);if(hit)hits++;
                if(hit){consecutive++;longest=Math.Max(longest,consecutive);}else consecutive=0;
            }
            return samples>=3&&hits>=samples*0.9&&longest>=samples*0.75;
        }
        static bool QuietRow(byte[] pixels,int stride,int y,int left,int right){
            int hits=0,total=0,step=Math.Max(1,(right-left)/48);
            for(int x=left+1;x<=right;x+=step){total++;int at=y*stride+x*4;if(!Edge(pixels,at,at-4))hits++;}
            return total>0&&hits>=total*0.8;
        }
        bool SameSides(Rect a,Rect b){return Math.Abs(a.Left-b.Left)<toleranceX&&Math.Abs(a.Right-b.Right)<toleranceX;}
        void Add(Rect rect,bool aggregate){
            for(int i=regions.Count-1;i>=0;i--){
                var prior=regions[i];if(!SameSides(prior.Bounds,rect))continue;
                if(Math.Abs(prior.Bounds.Top-rect.Top)<toleranceY&&Math.Abs(prior.Bounds.Bottom-rect.Bottom)<toleranceY){
                    if(!aggregate&&prior.Aggregate)regions[i]=new Region {Bounds=prior.Bounds,Aggregate=false};return;
                }
                // Growing outer prefixes are superseded by the full container.
                // Adjacent rows remain; arbitrary multi-row prefixes do not.
                if(aggregate&&prior.Aggregate){
                    if(rect.Top<=prior.Bounds.Top+toleranceY&&rect.Bottom>=prior.Bounds.Bottom-toleranceY)regions.RemoveAt(i);
                    else if(prior.Bounds.Top<=rect.Top+toleranceY&&prior.Bounds.Bottom>=rect.Bottom-toleranceY)return;
                }
            }
            regions.Add(new Region {Bounds=rect,Aggregate=aggregate});
        }
    }
}
