using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace QingJie {
    // No background polling. Opening settings does not check for updates or download.
    public sealed class OfflinePackPanel : StackPanel,IDisposable {
        readonly TextBlock status=new TextBlock {FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=Ui.Brush("#718078"),Margin=new Thickness(0,4,0,0)};
        readonly Button download,check,remove;
        OfflineCatalog available=OfflinePacks.Bundled;
        CancellationTokenSource operation;
        bool closed;
        public OfflinePackPanel(){
            Children.Add(new TextBlock {Text="离线中英包 · 可选",FontSize=16,FontWeight=FontWeights.SemiBold,Foreground=Ui.Ink});
            Children.Add(new TextBlock {Text="中文 ↔ 英文，无需显卡。仅翻译时加载，用完释放内存。小模型可能误译或漏译，重要内容建议在线复核。",FontSize=12,Foreground=Ui.Brush("#718078"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,6)});
            var actions=new WrapPanel();download=Ui.TextButton("下载",Download);download.Name="OfflineDownload";check=Ui.TextButton("检查更新",Check);check.Name="OfflineCheck";remove=Ui.TextButton("卸载",Remove);remove.Name="OfflineRemove";actions.Children.Add(download);actions.Children.Add(check);actions.Children.Add(remove);Children.Add(actions);Children.Add(status);Refresh();
        }
        void Refresh(string message=null){
            var installed=OfflinePacks.Installed;bool busy=operation!=null;
            download.Content=busy?"取消":installed==null?"下载 · "+(available.DownloadBytes/1048576.0).ToString("0")+" MB":available.Revision>installed.Catalog.Revision?"更新 · "+(available.DownloadBytes/1048576.0).ToString("0")+" MB":"已安装";
            download.IsEnabled=busy||installed==null||available.Revision>installed.Catalog.Revision;check.IsEnabled=!busy;remove.IsEnabled=!busy&&System.IO.Directory.Exists(OfflinePacks.Folder);
            status.Text=(installed==null?"尚未安装，不占模型空间。":installed.Catalog.Version+" · 占用 "+(installed.Bytes/1048576.0).ToString("0")+" MB")+"\n"+(message??"下载不会更改当前翻译服务；安装后在上方选择离线翻译并应用。下载源可能受网络影响。");
        }
        CancellationTokenSource Begin(){var source=new CancellationTokenSource();operation=source;Refresh();return source;}
        void End(CancellationTokenSource source,string message){if(operation==source)operation=null;source.Dispose();if(!closed)Refresh(message);}
        async void Download(){
            if(operation!=null){operation.Cancel();return;}
            if(MessageBox.Show(Window.GetWindow(this),"下载中英双向离线包及运行组件，约 "+(available.DownloadBytes/1048576.0).ToString("0")+" MB。安装 / 更新请预留至少 800 MB 空间。\n\n仅下载组件，不上传文字或截图。继续？","Luma · 离线包",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            var source=Begin();string message;
            try{await OfflinePacks.Install(available,new Progress<string>(s=>{if(!closed&&operation==source)status.Text=s;}),source.Token);if(Translation.Provider=="offline")AppState.ProviderChanged();message="安装成功。选择上方的离线翻译并应用即可使用，无需重启。";}
            catch(OperationCanceledException){message="已取消，原离线包未改变。";}
            catch(Exception ex){message="下载 / 更新未完成，原包未改变。"+ex.Message;}
            End(source,message);
        }
        async void Check(){
            var source=Begin();string message;
            try{available=await OfflinePacks.CheckUpdates(source.Token);var current=OfflinePacks.Installed;message=current==null?"可下载："+available.Version:available.Revision>current.Catalog.Revision?"有可用更新，点击更新后才会下载。":"已是当前发布的兼容版本。";}
            catch(OperationCanceledException){message="已取消检查。";}
            catch(Exception ex){message="未能检查更新："+ex.Message;}
            End(source,message);
        }
        async void Remove(){
            if(MessageBox.Show(Window.GetWindow(this),"卸载中英离线包及其运行组件？需要时可重新下载。\n\n不会删除贴图、历史或文字识别语言包。离线模式不会自动切换到在线服务。","Luma · 离线包",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            var source=Begin();string message;status.Text="正在卸载…";
            try{if(Translation.Provider=="offline")AppState.ProviderChanged();await OfflinePacks.Uninstall(source.Token);message="离线包已卸载，模型空间已释放。贴图、历史与识别语言包未改动。";}
            catch(OperationCanceledException){message="已取消卸载。";}
            catch(Exception ex){message="卸载未完成："+ex.Message;}
            End(source,message);
        }
        public void Dispose(){closed=true;if(operation!=null)operation.Cancel();}
    }
}
