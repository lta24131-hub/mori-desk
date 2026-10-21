using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Mori;

namespace MoriDesk {
 internal sealed class FileConversionWindow:Window {
  internal readonly FileConversionPanel Panel;
  bool closeWhenIdle;
  internal FileConversionWindow(Window owner,string[] inputs,Func<IList<ConvertedFile>,Task<string>> collect){
   Title="Nook · 转换格式（试用）";Width=720;Height=700;MinWidth=620;MinHeight=660;WindowStartupLocation=WindowStartupLocation.CenterOwner;
   if(owner!=null&&owner.IsVisible){Owner=owner;ShowInTaskbar=false;}Theme.Attach(this,owner);
   var client=new FileConversionClient();
   Panel=new FileConversionPanel(FileConversionClient.IsAvailable,client.GetCapabilitiesAsync,client.ConvertAsync,collect);
   Content=Panel;
   Panel.ChooseFiles=()=>{var dialog=new Microsoft.Win32.OpenFileDialog{Title="添加要转换的文件 · 原文件保留",Multiselect=true,Filter="图片与 PDF|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.gif;*.pdf;*.psd;*.psb;*.ico;*.heic;*.avif;*.apng;*.jxl|所有文件|*.*"};return dialog.ShowDialog(this)==true?dialog.FileNames:null;};
   Panel.ChooseFolder=()=>{using(var dialog=new System.Windows.Forms.FolderBrowserDialog{Description="选择转换结果目录 · 原文件不会覆盖",SelectedPath=Panel.Output.Text,ShowNewFolderButton=true})return dialog.ShowDialog(new WindowHandle(this))==System.Windows.Forms.DialogResult.OK?dialog.SelectedPath:null;};
   Panel.OpenFolder=path=>Process.Start(new ProcessStartInfo("explorer.exe","\""+path+"\""){UseShellExecute=true});
   Panel.CloseRequested=Close;
   Panel.OperationEnded+=delegate{if(closeWhenIdle&&!Panel.IsBusy)Close();};
   Loaded+=async delegate{await Panel.AddFilesAsync(inputs??new string[0]);};
   PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key!=Key.Escape)return;if(Panel.Target.IsDropDownOpen){Panel.Target.IsDropDownOpen=false;e.Handled=true;return;}e.Handled=true;if(Panel.IsBusy)Panel.CancelOperation();else Close();};
   Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(Panel.IsBusy){closeWhenIdle=true;e.Cancel=true;Panel.CancelOperation();}};
   Closed+=delegate{Panel.Dispose();};
  }
 }
}
