using System;
using System.IO;
using System.Windows;
namespace MoriDesk {
 internal static class FilePreviewHost {
  internal static int Run(string file){try{var app=new Application{ShutdownMode=ShutdownMode.OnLastWindowClose};Mori.Theme.Apply(Mori.Settings.Load());Mori.FilePreview.Show(null,file,Path.GetFileName(file));app.Run();return 0;}catch(Exception ex){MessageBox.Show(ex.Message,"Nook · 文件预览");return 1;}}
 }
}
