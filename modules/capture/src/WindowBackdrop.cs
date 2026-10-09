using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace QingJie {
    // Let DWM blur the background: no captured desktop, WPF BlurEffect or timer.
    internal static class WindowBackdrop {
        [StructLayout(LayoutKind.Sequential)] struct Margins {public int Left,Right,Top,Bottom;}
        [StructLayout(LayoutKind.Sequential)] struct Accent {public int State,Flags,Color,Animation;}
        [StructLayout(LayoutKind.Sequential)] struct Composition {public int Attribute;public IntPtr Data;public int Size;}
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowCompositionAttribute(IntPtr window,ref Composition data);
        public static void Attach(Window window,Border frame){
            frame.CornerRadius=new CornerRadius(12);
            WindowChrome.SetWindowChrome(window,new WindowChrome {CaptionHeight=0,ResizeBorderThickness=new Thickness(0),GlassFrameThickness=new Thickness(-1),CornerRadius=new CornerRadius(12),UseAeroCaptionButtons=false});
            window.SourceInitialized+=(s,e)=>{
                bool enabled=false;
                try{
                    var handle=new WindowInteropHelper(window).Handle;int round=2;DwmSetWindowAttribute(handle,33,ref round,4);
                    if(!SystemParameters.HighContrast){
                        int light=0;DwmSetWindowAttribute(handle,20,ref light,4);int backdrop=1;DwmSetWindowAttribute(handle,38,ref backdrop,4);
                        var accent=new Accent {State=4,Color=unchecked((int)0x99F4F4F4)};IntPtr memory=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Accent)));try{Marshal.StructureToPtr(accent,memory,false);var data=new Composition {Attribute=19,Data=memory,Size=Marshal.SizeOf(typeof(Accent))};enabled=SetWindowCompositionAttribute(handle,ref data);}finally{Marshal.FreeHGlobal(memory);}
                        if(!enabled){backdrop=3;enabled=DwmSetWindowAttribute(handle,38,ref backdrop,4)==0;}
                        if(enabled){var margins=new Margins {Left=-1,Right=-1,Top=-1,Bottom=-1};enabled=DwmExtendFrameIntoClientArea(handle,ref margins)==0;}
                        if(enabled){var source=HwndSource.FromHwnd(handle);if(source!=null&&source.CompositionTarget!=null)source.CompositionTarget.BackgroundColor=Colors.Transparent;}
                    }
                }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}catch(ExternalException){}
                window.Background=enabled?Brushes.Transparent:Brushes.White;
                frame.Background=enabled?new SolidColorBrush(Color.FromArgb(48,255,255,255)):Brushes.White;
                frame.BorderBrush=Ui.Brush(enabled?"#80FFFFFF":"#DFE2E6");
            };
        }
    }
}
