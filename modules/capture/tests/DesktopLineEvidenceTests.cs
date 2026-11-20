using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;
internal static class DesktopLineEvidenceTests {
    static BitmapSource Draw(string text,int mark=0){
        int w=320,h=80;using(var b=new Bitmap(w,h,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using(var g=Graphics.FromImage(b))using(var f=new Font("Segoe UI",24,System.Drawing.FontStyle.Regular,GraphicsUnit.Pixel))
        using(var fmt=StringFormat.GenericTypographic){g.Clear(System.Drawing.Color.White);g.TextRenderingHint=TextRenderingHint.ClearTypeGridFit;
            g.DrawString(text,f,System.Drawing.Brushes.Black,new PointF(12,11),fmt);g.Flush();
            var data=b.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);var p=new byte[w*h*4];
            try{for(int y=0;y<h;y++)Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),p,y*w*4,w*4);}finally{b.UnlockBits(data);}
            if(mark!=0){int k=(w*30+268)*4;p[k+1]=(byte)(mark==1?254:190);}
            var source=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,p,w*4);source.Freeze();return source;
        }
    }
    internal static void Run(Action<bool,string> check){
        var image=Draw("Turn the wheel.");var native=DesktopLineEvidence.Analyze(image);
        var result=DesktopLineEvidence.Prove(native,"Turn the wheel.",CancellationToken.None);
        check(result!=null&&result.Text=="Turn the wheel."&&result.Words.Count==3&&result.Words[0].Box.Right<result.Words[1].Box.Left&&result.Words[1].Box.Right<result.Words[2].Box.Left,"desktop RGB proof returns actual separate word boxes for the verified line");
        foreach(string miss in new[]{"Turn thy wheel.","Turn the wheel","Turnthe wheel.","Turn th e wheel."})
            check(DesktopLineEvidence.Prove(native,miss,CancellationToken.None)==null,"desktop RGB proof rejects a different glyph, punctuation, or boundary: "+miss);
        foreach(int mark in new[]{1,2})check(DesktopLineEvidence.Prove(DesktopLineEvidence.Analyze(Draw("Turn the wheel.",mark)),"Turn the wheel.",CancellationToken.None)==null,"even one different green channel at an extra punctuation position prevents matching: "+mark);
        var guessed=VerifiedLineOcr.Hypothesis(new[]{new WordBox{Text="th",Box=new System.Windows.Rect(30,9,18,16)},new WordBox{Text="e",Box=new System.Windows.Rect(50,10,9,14)},new WordBox{Text="wheel•",Box=new System.Windows.Rect(67,10,40,15)}});
        check(guessed!=null&&string.Join(" ",guessed)=="the wheel.","close split glyphs and a possible final dot generate a hypothesis, not an accepted transcription");
        check(VerifiedLineOcr.Hypothesis(new[]{new WordBox{Text="th",Box=new System.Windows.Rect(30,9,18,16)},new WordBox{Text="e",Box=new System.Windows.Rect(60,10,9,14)}})==null,"separated tokens cannot be joined to make an apparently simple word");
    }
}
