using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms=System.Windows.Forms;

namespace QingJie {
    // GDI exposes an 8-bit SDR bitmap even on an HDR desktop. Its white-point
    // conversion can clip pale text. Read the compositor's floating-point pixels
    // only on advanced-color screens and normalize their per-monitor SDR white.
    // All GPU resources are short-lived: nothing stays resident between captures.
    internal static class AdvancedScreenCapture {
        [StructLayout(LayoutKind.Sequential)] struct Luid {public uint Low;public int High;}
        [StructLayout(LayoutKind.Sequential)] struct Header {public uint Type,Size;public Luid Adapter;public uint Id;}
        [StructLayout(LayoutKind.Sequential)] struct ColorInfo {public Header Header;public uint Flags,Encoding,BitsPerChannel;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SourceName {public Header Header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;}
        [StructLayout(LayoutKind.Sequential)] struct WhiteLevel {public Header Header;public uint Value;}
        [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct OutputDesc {[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;public Rect Bounds;public int Attached;public uint Rotation;public IntPtr Monitor;}
        [StructLayout(LayoutKind.Sequential)] struct TextureDesc {public uint Width,Height,MipLevels,ArraySize,Format,SampleCount,SampleQuality,Usage,BindFlags,CpuAccessFlags,MiscFlags;}
        [StructLayout(LayoutKind.Sequential)] struct Mapped {public IntPtr Data;public uint RowPitch,DepthPitch;}
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags,ref uint paths,IntPtr pathData,ref uint modes,IntPtr modeData,IntPtr topology);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int DeviceInfo(ref ColorInfo value);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int DeviceInfo(ref SourceName value);
        [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")] static extern int DeviceInfo(ref WhiteLevel value);
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid id,out IntPtr factory);
        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter,uint driver,IntPtr software,uint flags,IntPtr levels,uint levelCount,uint sdk,out IntPtr device,out uint level,out IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumObject(IntPtr self,uint index,out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DescribeOutput(IntPtr self,out OutputDesc value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DescribeOutput1(IntPtr self,IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Duplicate(IntPtr self,IntPtr device,uint flags,uint count,[MarshalAs(UnmanagedType.LPArray,SizeParamIndex=3)]uint[] formats,out IntPtr duplication);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Acquire(IntPtr self,uint milliseconds,IntPtr info,out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFrame(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DescribeTexture(IntPtr self,out TextureDesc value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture(IntPtr self,ref TextureDesc desc,IntPtr initial,out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResource(IntPtr self,IntPtr destination,IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapResource(IntPtr self,IntPtr resource,uint subresource,uint type,uint flags,out Mapped mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapResource(IntPtr self,IntPtr resource,uint subresource);
        static readonly Guid FactoryId=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid Output5Id=new Guid("80a07424-ab52-42eb-833c-0c42fd282d98");
        static readonly Guid Output6Id=new Guid("068346e8-aaec-4b84-add7-137f513f77a1");
        static readonly Guid TextureId=new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        internal static string LastCaptureDetails;
        static T Method<T>(IntPtr value,int slot) where T:class {return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value),slot*IntPtr.Size),typeof(T)) as T;}
        static void Release(ref IntPtr value){if(value!=IntPtr.Zero){Marshal.Release(value);value=IntPtr.Zero;}}
        static void Require(int result){if(result<0)Marshal.ThrowExceptionForHR(result);}

        // DISPLAYCONFIG's SDRWhiteLevel is 1000 for the 80-nit scRGB reference
        // white, 2500 for 200 nits, etc. A failed read remains unknown: an HDR
        // screen must not silently assume reference white and clip gray text.
        internal static bool ReadColorState(string display,out double whiteMultiplier){
            whiteMultiplier=double.NaN;
            for(int attempt=0;attempt<3;attempt++){
                uint paths,modes;if(GetDisplayConfigBufferSizes(2,out paths,out modes)!=0||paths==0||paths>128||modes>512)return false;
                IntPtr pathData=Marshal.AllocHGlobal(checked((int)paths*72)),modeData=Marshal.AllocHGlobal(checked((int)modes*64));
                try {
                    int result=QueryDisplayConfig(2,ref paths,pathData,ref modes,modeData,IntPtr.Zero);if(result==122)continue;if(result!=0)return false;
                    for(int i=0;i<paths;i++){
                        IntPtr path=IntPtr.Add(pathData,i*72);
                        var name=new SourceName {Header=new Header {Type=1,Size=84,Adapter=(Luid)Marshal.PtrToStructure(path,typeof(Luid)),Id=unchecked((uint)Marshal.ReadInt32(path,8))}};
                        if(DeviceInfo(ref name)!=0||!string.Equals(name.Name,display,StringComparison.OrdinalIgnoreCase))continue;
                        IntPtr target=IntPtr.Add(path,20);var header=new Header {Type=9,Size=32,Adapter=(Luid)Marshal.PtrToStructure(target,typeof(Luid)),Id=unchecked((uint)Marshal.ReadInt32(target,8))};
                        var info=new ColorInfo {Header=header};if(DeviceInfo(ref info)!=0||(info.Flags&2)==0)return false;
                        header.Type=11;header.Size=24;var white=new WhiteLevel {Header=header};
                        if(DeviceInfo(ref white)==0&&white.Value>=100&&white.Value<=100000)whiteMultiplier=white.Value/1000.0;
                        return true;
                    }
                    return false;
                } finally {Marshal.FreeHGlobal(pathData);Marshal.FreeHGlobal(modeData);}
            }
            return false;
        }

        internal static bool TryCapture(Forms.Screen screen,out BitmapSource image,out string failure){
            image=null;failure=null;LastCaptureDetails=null;double white;
            try {if(!ReadColorState(screen.DeviceName,out white))return false;}
            catch(Exception ex){failure="显示颜色信息不可用："+ex.GetType().Name;return false;}
            IntPtr factory=IntPtr.Zero,adapter=IntPtr.Zero,output=IntPtr.Zero,output5=IntPtr.Zero,output6=IntPtr.Zero,device=IntPtr.Zero,context=IntPtr.Zero,duplication=IntPtr.Zero,resource=IntPtr.Zero,texture=IntPtr.Zero,staging=IntPtr.Zero,infoData=IntPtr.Zero;
            bool frame=false,mapped=false;
            try {
                Guid factoryId=FactoryId;Require(CreateDXGIFactory1(ref factoryId,out factory));OutputDesc outputDesc=new OutputDesc();bool found=false;
                for(uint a=0;a<128&&!found;a++){
                    if(Method<EnumObject>(factory,12)(factory,a,out adapter)<0)break;
                    for(uint o=0;o<128;o++){
                        if(Method<EnumObject>(adapter,7)(adapter,o,out output)<0)break;
                        Require(Method<DescribeOutput>(output,7)(output,out outputDesc));
                        if(string.Equals(outputDesc.Name,screen.DeviceName,StringComparison.OrdinalIgnoreCase)){found=true;break;}
                        Release(ref output);
                    }
                    if(!found)Release(ref adapter);
                }
                if(!found)throw new InvalidOperationException("未找到目标显示器。");
                // AdvancedColorEnabled also covers SDR automatic color management.
                // Only an HDR wire-output mode uses the HDR SDR-white multiplier.
                Guid output6Id=Output6Id;uint colorSpace=12;
                if(Marshal.QueryInterface(output,ref output6Id,out output6)>=0){
                    IntPtr desc1=Marshal.AllocHGlobal(152);
                    try{Require(Method<DescribeOutput1>(output6,27)(output6,desc1));colorSpace=unchecked((uint)Marshal.ReadInt32(desc1,100));}finally{Marshal.FreeHGlobal(desc1);}
                }
                if(colorSpace!=12)white=1;
                else if(double.IsNaN(white))throw new InvalidOperationException("无法读取 HDR 屏幕的 SDR 白亮度。");
                uint feature;Require(D3D11CreateDevice(adapter,0,IntPtr.Zero,0x20,IntPtr.Zero,0,7,out device,out feature,out context));
                Guid outputId=Output5Id;Require(Marshal.QueryInterface(output,ref outputId,out output5));
                Require(Method<Duplicate>(output5,26)(output5,device,0,2,new uint[]{10,87},out duplication));
                infoData=Marshal.AllocHGlobal(64);
                // A freshly created duplication can first return only a pointer
                // update (AccumulatedFrames == 0, LastPresentTime == 0). Its
                // texture is not a completed desktop image. Do not export that
                // uninitialized black texture, and do not reject real black art.
                var acquireTime=System.Diagnostics.Stopwatch.StartNew();
                for(int attempt=0;attempt<4;attempt++){
                    uint wait=(uint)Math.Max(1,350-acquireTime.ElapsedMilliseconds);
                    Require(Method<Acquire>(duplication,8)(duplication,wait,infoData,out resource));frame=true;
                    if(IsDesktopFrame(Marshal.ReadInt64(infoData),Marshal.ReadInt32(infoData,16)))break;
                    Release(ref resource);Require(Method<ReleaseFrame>(duplication,14)(duplication));frame=false;
                    if(acquireTime.ElapsedMilliseconds>=350||attempt==3)throw new InvalidOperationException("尚未收到完整桌面帧。");
                }
                Guid textureId=TextureId;Require(Marshal.QueryInterface(resource,ref textureId,out texture));TextureDesc desc;Method<DescribeTexture>(texture,10)(texture,out desc);
                LastCaptureDetails="format="+desc.Format+", outputColorSpace="+colorSpace+", SDRWhiteMultiplier="+white.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+", rotation="+outputDesc.Rotation+", texture="+desc.Width+"x"+desc.Height+", desktop="+outputDesc.Bounds.Left+","+outputDesc.Bounds.Top+","+outputDesc.Bounds.Right+","+outputDesc.Bounds.Bottom;
                if(colorSpace==12&&desc.Format!=10)throw new InvalidOperationException("HDR 桌面未提供浮点颜色，无法保证灰阶。");
                if((desc.Format!=10&&desc.Format!=87)||desc.Width<1||desc.Height<1||desc.Width>16384||desc.Height>16384||(long)desc.Width*desc.Height>40000000)throw new InvalidOperationException("不支持的桌面像素格式或尺寸。");
                var stagingDesc=desc;stagingDesc.Usage=3;stagingDesc.BindFlags=0;stagingDesc.CpuAccessFlags=0x20000;stagingDesc.MiscFlags=0;stagingDesc.ArraySize=1;stagingDesc.MipLevels=1;
                Require(Method<CreateTexture>(device,5)(device,ref stagingDesc,IntPtr.Zero,out staging));Method<CopyResource>(context,47)(context,staging,texture);
                Mapped data;Require(Method<MapResource>(context,14)(context,staging,0,1,0,out data));mapped=true;
                image=ReadPixels(data.Data,data.RowPitch,(int)desc.Width,(int)desc.Height,desc.Format,outputDesc.Rotation,white);
                LastCaptureDetails+=", mappedPitch="+data.RowPitch+", frames="+Marshal.ReadInt32(infoData,16);
                if(image.PixelWidth!=screen.Bounds.Width||image.PixelHeight!=screen.Bounds.Height)throw new InvalidOperationException("截图尺寸与目标显示器不一致。");
                return true;
            } catch(Exception ex){image=null;failure="HDR 兼容采集暂不可用（"+ex.GetType().Name+"）";return false;}
            finally {
                if(mapped&&context!=IntPtr.Zero&&staging!=IntPtr.Zero)Method<UnmapResource>(context,15)(context,staging,0);
                Release(ref staging);Release(ref texture);Release(ref resource);
                if(frame&&duplication!=IntPtr.Zero)Method<ReleaseFrame>(duplication,14)(duplication);
                if(infoData!=IntPtr.Zero)Marshal.FreeHGlobal(infoData);
                Release(ref duplication);Release(ref context);Release(ref device);Release(ref output6);Release(ref output5);Release(ref output);Release(ref adapter);Release(ref factory);
            }
        }
        internal static bool IsDesktopFrame(long presentTime,int accumulatedFrames){return presentTime>0&&accumulatedFrames>0;}

        // SDR colors occupy [0, SDRWhiteLevel] in linear scRGB. Normalize first,
        // then apply the sRGB transfer curve. Never gamma-transform BGRA8 twice.
        internal static byte LinearToSrgb(double value,double white){
            if(double.IsNaN(value)||value<=0)return 0;
            if(double.IsNaN(white)||double.IsInfinity(white)||white<=0)white=1;
            value/=white;if(value>=1)return 255;
            double srgb=value<=.0031308?12.92*value:1.055*Math.Pow(value,1/2.4)-.055;
            return (byte)Math.Max(0,Math.Min(255,Math.Round(srgb*255)));
        }
        internal static double HalfValue(ushort value){
            int sign=(value&0x8000)==0?1:-1,exponent=(value>>10)&31,mantissa=value&1023;
            if(exponent==31)return mantissa==0?sign*double.PositiveInfinity:double.NaN;
            return sign*(exponent==0?mantissa*Math.Pow(2,-24):(1+mantissa/1024.0)*Math.Pow(2,exponent-15));
        }
        static BitmapSource ReadPixels(IntPtr source,uint pitch,int width,int height,uint format,uint rotation,double white){
            if(source==IntPtr.Zero||(format!=10&&format!=87)||width<1||height<1||width>16384||height>16384||(long)width*height>40000000)throw new InvalidOperationException("无效的桌面像素格式或尺寸。");
            int pixelBytes=format==10?8:4;if(pitch<(long)width*pixelBytes||pitch>262144)throw new InvalidOperationException("无效的桌面像素步长。");
            bool rotated=rotation==2||rotation==4;int targetWidth=rotated?height:width,targetHeight=rotated?width:height;
            var pixels=new byte[checked(targetWidth*targetHeight*4)];byte[] lookup=null;short[] halfRow=null;byte[] byteRow=null;
            if(format==10){lookup=new byte[65536];for(int i=0;i<lookup.Length;i++)lookup[i]=LinearToSrgb(HalfValue((ushort)i),white);halfRow=new short[checked(width*4)];}else byteRow=new byte[checked(width*4)];
            for(int y=0;y<height;y++){
                IntPtr row=IntPtr.Add(source,checked((int)(y*pitch)));if(format==10)Marshal.Copy(row,halfRow,0,halfRow.Length);else Marshal.Copy(row,byteRow,0,byteRow.Length);
                for(int x=0;x<width;x++){
                    int tx=x,ty=y;if(rotation==2){tx=height-1-y;ty=x;}else if(rotation==3){tx=width-1-x;ty=height-1-y;}else if(rotation==4){tx=y;ty=width-1-x;}
                    int destination=(ty*targetWidth+tx)*4,from=x*4;
                    if(format==10){pixels[destination]=lookup[unchecked((ushort)halfRow[from+2])];pixels[destination+1]=lookup[unchecked((ushort)halfRow[from+1])];pixels[destination+2]=lookup[unchecked((ushort)halfRow[from])];}
                    else {pixels[destination]=byteRow[from];pixels[destination+1]=byteRow[from+1];pixels[destination+2]=byteRow[from+2];}
                    pixels[destination+3]=255;
                }
            }
            var image=BitmapSource.Create(targetWidth,targetHeight,96,96,PixelFormats.Bgra32,null,pixels,targetWidth*4);image.Freeze();return image;
        }
        internal static BitmapSource PixelsForTest(IntPtr source,uint pitch,int width,int height,uint format,uint rotation,double white){return ReadPixels(source,pitch,width,height,format,rotation,white);}
    }
}
