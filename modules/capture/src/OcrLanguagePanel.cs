using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QingJie {
    internal sealed class OcrLanguagePanel : StackPanel,IDisposable {
        sealed class Row {public DockPanel Root;public CheckBox Check;public TextBlock State;public Button Download,More;}
        readonly OcrLanguageSelection selection;
        readonly Func<string,IProgress<int>,CancellationToken,Task> install;
        readonly Dictionary<string,Row> rows=new Dictionary<string,Row>();
        readonly StackPanel installedRows=new StackPanel(),availableRows=new StackPanel();
        readonly Expander available=new Expander {Margin=new Thickness(0,8,0,8)};
        readonly TextBlock status=new TextBlock {Name="OcrLanguageStatus",FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=Ui.Brush("#718078"),Margin=new Thickness(0,8,0,0)};
        CancellationTokenSource download;
        string downloadingId;
        bool refreshing,closed;
        readonly bool bundledKorean=OcrLanguagePacks.HasBundledKorean;
        internal OcrLanguagePanel(OcrLanguageSelection selection,Func<string,IProgress<int>,CancellationToken,Task> install=null){
            this.selection=selection;this.install=install??OcrLanguagePacks.Install;Name="OcrLanguages";
            Children.Add(new TextBlock {Text="识别语言",FontSize=16,FontWeight=FontWeights.SemiBold,Foreground=Ui.Ink});
            Children.Add(new TextBlock {Text=bundledKorean?"默认识别中文和英文，遇到疑似韩文时会自动尝试识别。也可以指定每次都要识别的语言。":"附加语言可多选，勾选即生效。只影响图片识别，不改变翻译目标。",FontSize=12,Foreground=Ui.Brush("#718078"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,10)});
            var basics=new WrapPanel();
            foreach(string name in new[]{"中文","英文"})basics.Children.Add(new CheckBox {Content=name,IsChecked=true,IsEnabled=false,Name=name=="中文"?"OcrBaseChinese":"OcrBaseEnglish",Margin=new Thickness(0,0,16,0),VerticalAlignment=VerticalAlignment.Center,ToolTip="基础识别始终保留"});
            basics.Children.Add(new TextBlock {Text="始终开启",FontSize=12,Foreground=Ui.Brush("#718078")});
            Children.Add(new Border {Child=basics,Padding=new Thickness(10),Background=Ui.Brush("#F3F6F4"),CornerRadius=new CornerRadius(5),Margin=new Thickness(0,0,0,8)});
            Children.Add(installedRows);available.Content=availableRows;Children.Add(available);Children.Add(status);
            foreach(var pack in OcrLanguagePacks.Available){
                string id=pack.Id;var row=new Row {Root=new DockPanel {LastChildFill=true,Margin=new Thickness(0,3,0,3),MinHeight=32}};
                row.Check=new CheckBox {Content=pack.Name,Name="OcrLanguage_"+id,FontSize=13,VerticalAlignment=VerticalAlignment.Center,MinWidth=84};
                row.State=new TextBlock {FontSize=12,Foreground=Ui.Brush("#718078"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,6,0)};
                row.More=Ui.TextButton("···",()=>ShowMore(id));row.More.Name="OcrMore_"+id;row.More.ToolTip="导入 / 卸载 "+pack.Name+" 语言包";row.More.Padding=new Thickness(9,3,9,3);row.More.Margin=new Thickness(4,0,0,0);
                row.Download=Ui.TextButton("下载",async()=>await Download(id));row.Download.Name="OcrDownload_"+id;row.Download.ToolTip="下载并启用 "+pack.Name+"，不会取消其他语言";row.Download.Padding=new Thickness(9,3,9,3);row.Download.Margin=new Thickness(0);
                DockPanel.SetDock(row.More,Dock.Right);row.Root.Children.Add(row.More);DockPanel.SetDock(row.Download,Dock.Right);row.Root.Children.Add(row.Download);DockPanel.SetDock(row.Check,Dock.Left);row.Root.Children.Add(row.Check);row.Root.Children.Add(row.State);
                row.Check.Checked+=(s,e)=>Toggle(id,true);row.Check.Unchecked+=(s,e)=>Toggle(id,false);rows.Add(id,row);
            }
            Refresh();available.IsExpanded=installedRows.Children.Count==0;Ui.ThemeControls(this);
        }
        internal void Refresh(){
            refreshing=true;
            try{foreach(var pack in OcrLanguagePacks.Available){
                var row=rows[pack.Id];bool ready=selection.Installed(pack.Id),enabled=selection.Enabled(pack.Id),busy=download!=null;
                row.Check.IsChecked=ready&&enabled;row.Check.IsEnabled=!busy&&ready;
                row.Check.ToolTip=ready?"勾选启用，取消勾选后语言包仍保留":enabled?"语言包缺失，暂未生效；重新下载即可恢复":"请先下载或从更多菜单导入";
                row.State.Text=ready?(enabled?"已启用":"已安装"):enabled?"语言包缺失":(pack.Bytes/1048576.0).ToString("0.0")+" MB";
                row.Download.Visibility=ready?Visibility.Collapsed:Visibility.Visible;row.Download.IsEnabled=!busy||downloadingId==pack.Id;row.Download.Content=busy&&downloadingId==pack.Id?"取消":"下载";
                if(bundledKorean&&pack.Id=="kor"){
                    row.Check.ToolTip="勾选后，每次识别都使用韩文模型；取消勾选可恢复自动判断";
                    row.State.Text=ready&&enabled?"每次使用":"内置模型";
                    row.More.Visibility=System.IO.File.Exists(OcrLanguagePacks.ModelPath("kor"))?Visibility.Visible:Visibility.Collapsed;
                    row.More.ToolTip="管理韩文识别设置";
                }
                row.More.IsEnabled=!busy;
                var parent=ready||enabled?installedRows:availableRows;
                if(row.Root.Parent!=parent){var old=row.Root.Parent as Panel;if(old!=null)old.Children.Remove(row.Root);parent.Children.Add(row.Root);}
            }}finally{refreshing=false;}
            available.Header="添加更多语言（"+availableRows.Children.Count+"）";available.Visibility=availableRows.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
            var effective=selection.EffectiveIds;
            status.Text=bundledKorean&&effective.Length==0?"当前：自动识别":"当前：中文 + 英文"+string.Concat(effective.Select(id=>" + "+OcrLanguagePacks.Find(id).Name))+"。\n启用的语言越多，识别可能越慢。";
            var missing=selection.CurrentIds.Where(id=>!selection.Installed(id)).ToArray();if(missing.Length>0)status.Text+="\n"+string.Join("、",missing.Select(id=>OcrLanguagePacks.Find(id).Name))+"语言包缺失，已跳过，不影响其他语言。";
        }
        void Toggle(string id,bool enabled){if(refreshing||closed||download!=null)return;try{selection.SetEnabled(id,enabled);Refresh();}catch(Exception ex){Refresh();status.Text+="\n保存失败，原选择保留："+ex.Message;}}
        internal async Task Download(string id){
            if(closed)return;if(download!=null){if(downloadingId==id)download.Cancel();return;}
            var pack=OcrLanguagePacks.Find(id);if(pack==null)return;
            var cancel=new CancellationTokenSource();download=cancel;downloadingId=id;Refresh();rows[id].State.Text=bundledKorean&&id=="kor"?"正在启用…":"下载中…";
            string error=null;
            try{await install(id,new Progress<int>(value=>{if(!closed&&download==cancel)rows[id].State.Text="下载 "+value+"%";}),cancel.Token);cancel.Token.ThrowIfCancellationRequested();if(!closed)selection.SetEnabled(id,true);}
            catch(OperationCanceledException){error="已取消或下载超时，原选择保留。";}
            catch(Exception ex){error="未能启用，原选择保留："+ex.Message+" 可重试或从 ··· 导入语言包。";}
            finally{download=null;downloadingId=null;cancel.Dispose();if(!closed){Refresh();if(error!=null)status.Text+="\n"+error;}}
        }
        void ShowMore(string id){
            if(closed||download!=null)return;
            var menu=Ui.Menu(rows[id].More);var importItem=new MenuItem {Header="导入语言包…"};importItem.Click+=(s,e)=>Import(id);menu.Items.Add(importItem);
            if(selection.Installed(id)||selection.Enabled(id)){var removeItem=new MenuItem {Header=bundledKorean&&id=="kor"?"移除手动配置…":selection.Installed(id)?"卸载语言包…":"移除失效选择"};removeItem.Click+=(s,e)=>Remove(id);menu.Items.Add(removeItem);}
            menu.PlacementTarget=rows[id].More;menu.IsOpen=true;
        }
        void Import(string id){
            if(closed||download!=null)return;
            var pack=OcrLanguagePacks.Find(id);var dialog=new Microsoft.Win32.OpenFileDialog {Title="导入 "+pack.Name+" 官方语言包",Filter="Tesseract 语言包|*.traineddata",FileName=id+".traineddata"};
            if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
            try{OcrLanguagePacks.Import(dialog.FileName,id);selection.SetEnabled(id,true);Refresh();}catch(Exception ex){Refresh();status.Text+="\n未能启用，原选择保留："+ex.Message;}
        }
        void Remove(string id){
            if(closed||download!=null)return;
            var pack=OcrLanguagePacks.Find(id);
            string prompt=bundledKorean&&id=="kor"?"移除韩文的手动配置？默认模式仍可自动识别韩文。":"卸载 "+pack.Name+" 语言包？之后可重新下载。\n其他已启用语言、截图和贴图保持不变。";
            if(selection.Installed(id)&&MessageBox.Show(Window.GetWindow(this),prompt,"Nook",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            try{selection.Remove(id);Refresh();}catch(Exception ex){Refresh();status.Text+="\n卸载未完成："+ex.Message;}
        }
        public void Dispose(){closed=true;if(download!=null)download.Cancel();}
    }
}
