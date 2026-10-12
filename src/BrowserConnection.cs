using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace MoriDesk {
 public static class BrowserConnection {
  public const string Host="app.mori.desk";
  public static string ExtensionDirectory(string executable){return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable)),"browser-extension");}
  public static string ValidateId(string id){id=(id??"").Trim();if(!Regex.IsMatch(id,"\\A[a-p]{32}\\z"))throw new ArgumentException("请粘贴扩展管理页面中 32 位小写字母的 ID，不是网址或插件名称。");return id;}
  public static string Manifest(string executable,string id,string existing){
   id=ValidateId(id);var origins=new HashSet<string>(StringComparer.Ordinal);var json=new JavaScriptSerializer();
   if(!string.IsNullOrWhiteSpace(existing)){
    var old=json.Deserialize<Dictionary<string,object>>(existing);object values,name;
    if(old==null||!old.TryGetValue("name",out name)||Convert.ToString(name)!=Host)throw new InvalidDataException("已有连接配置不属于 Mori Desk，未覆盖。请检查配置文件。");
    if(old.TryGetValue("allowed_origins",out values)&&values is IEnumerable)foreach(object value in (IEnumerable)values){var origin=Convert.ToString(value);if(Regex.IsMatch(origin,"\\Achrome-extension://[a-p]{32}/\\z"))origins.Add(origin);}
   }
   origins.Add("chrome-extension://"+id+"/");
   return json.Serialize(new Dictionary<string,object>{{"name",Host},{"description","Mori Desk local library"},{"path",Path.GetFullPath(executable)},{"type","stdio"},{"allowed_origins",origins.OrderBy(x=>x).ToArray()}});
  }
  public static string WriteManifest(string folder,string executable,string id){
   if(!File.Exists(executable))throw new FileNotFoundException("Mori Desk 程序不存在，请从完整安装目录运行。",executable);
   string path=Path.Combine(folder,Host+".json");string data=Manifest(executable,id,File.Exists(path)?File.ReadAllText(path):null);
   Directory.CreateDirectory(folder);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllText(temp,data,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}return path;
  }
  public static void Register(string id){
   string path=WriteManifest(Path.Combine(Entry.DataRoot,"browser"),typeof(Entry).Assembly.Location,id);
   foreach(string browser in new[]{"Google\\Chrome","Microsoft\\Edge"})using(var key=Registry.CurrentUser.CreateSubKey("Software\\"+browser+"\\NativeMessagingHosts\\"+Host))key.SetValue("",path);
  }
  public static void Show(Window owner,bool preview){
   var w=new Window{Title="Mori Desk · 浏览器收藏",Owner=owner,Width=600,Height=570,MinWidth=480,MinHeight=480,WindowStartupLocation=WindowStartupLocation.CenterOwner};Mori.Theme.Attach(w,owner);
   var p=new StackPanel{Margin=new Thickness(24)};w.Content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
   Action<string,double> label=(text,size)=>p.Children.Add(new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,8,4,10),LineHeight=size+9});
   label("连接浏览器收藏",23);
   label("1  加载插件\n在 Chrome / Edge 扩展管理页开启开发者模式，选择“加载已解压的扩展程序”，选择下面的文件夹。",13);
   string dir=ExtensionDirectory(typeof(Entry).Assembly.Location);
   p.Children.Add(ReferencePage.Button("打开插件文件夹",()=>{try{if(!Directory.Exists(dir))throw new DirectoryNotFoundException("缺少 browser-extension 文件夹，请完整解压或重新安装。");Process.Start("explorer.exe",FileRouter.Quote(dir));}catch(Exception ex){MessageBox.Show(w,ex.Message,"插件文件夹");}}));
   var location=new TextBox{Text=dir,IsReadOnly=true,Margin=new Thickness(4),Padding=new Thickness(8),ToolTip="可复制此路径，在加载扩展时选择"};p.Children.Add(location);
   label("2  粘贴插件 ID\n找到“Mori Desk · 图片与网址收藏”，复制其 32 位 ID。Chrome 和 Edge 可分别连接，不会互相覆盖。",13);
   var input=new TextBox{Margin=new Thickness(4),Padding=new Thickness(10),MaxLength=64};System.Windows.Automation.AutomationProperties.SetName(input,"Mori Desk 扩展 ID");p.Children.Add(input);
   var result=new TextBlock{Text=preview?"隔离测试模式：不会修改真实浏览器连接。":"只注册当前用户的 Mori Desk 连接，不改动旧 Mori。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,12,4,4)};
   p.Children.Add(ReferencePage.Button("连接本机程序",()=>{try{ValidateId(input.Text);if(preview){result.Text="ID 格式正确。隔离测试未写入注册表。";return;}Register(input.Text);result.Text="本机配置已保存。请回浏览器打开插件检查连接，再刷新普通网页测试收藏。";}catch(Exception ex){result.Text="未连接："+ex.Message;}}));p.Children.Add(result);
   label("3  验证收藏\n回浏览器点击插件图标检查连接，刷新普通网页后拖拽或右键收藏。升级或移动软件后，再连接一次即可更新程序路径。",13);
   w.PreviewKeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Escape){w.Close();e.Handled=true;}};w.ShowDialog();
  }
 }
}
