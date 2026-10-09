using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QingJie {
    public sealed class SettingsWindow : Window {
        readonly CheckBox startup=new CheckBox {Content="开机自动启动",FontSize=15,Margin=new Thickness(0,10,0,7)};
        readonly TextBlock status=new TextBlock {FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=Ui.Green,Margin=new Thickness(0,8,0,0)};
        bool updating;
        readonly TextBlock shortcutStatus=new TextBlock {FontSize=12,Foreground=Ui.Brush("#718078"),TextWrapping=TextWrapping.Wrap};
        ShortcutEditor captureKey,pinKey,translationKey;
        public SettingsWindow():this(new OcrLanguageSelection(AppState.Settings,()=>AppState.Settings.SaveChecked(),OcrLanguagePacks.Installed,id=>File.Delete(OcrLanguagePacks.ModelPath(id)))){}
        internal SettingsWindow(OcrLanguageSelection selection){
            Title="Mori Desk · 截图与翻译设置";Icon=Ui.AppIcon;Width=520;Height=600;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brushes.White;FontFamily=new FontFamily("Microsoft YaHei UI");
            var panel=new StackPanel {Margin=new Thickness(26,20,26,20)};Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            panel.Children.Add(new TextBlock {Text="设置",FontSize=23,FontWeight=FontWeights.SemiBold,Foreground=Ui.Ink});panel.Children.Add(startup);
            panel.Children.Add(new TextBlock {Text="登录 Windows 后在托盘运行，并恢复未收起的贴图。",FontSize=12,Foreground=Ui.Brush("#718078"),TextWrapping=TextWrapping.Wrap});
            panel.Children.Add(new Border {Height=1,Background=Ui.Brush("#EBEFED"),Margin=new Thickness(0,18,0,16)});
            panel.Children.Add(new TextBlock {Text="快捷键",FontSize=16,FontWeight=FontWeights.SemiBold,Foreground=Ui.Ink});
            panel.Children.Add(new TextBlock {Text="点击右侧按键，再按新组合键。保存后立即生效。",FontSize=12,Foreground=Ui.Brush("#718078"),Margin=new Thickness(0,6,0,6)});
            captureKey=ShortcutRow(panel,"截图",AppState.Settings.Shortcuts.Capture);pinKey=ShortcutRow(panel,"贴图 / 找回",AppState.Settings.Shortcuts.Pin);translationKey=ShortcutRow(panel,"输入翻译",AppState.Settings.Shortcuts.Translate);
            var shortcutActions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            shortcutActions.Children.Add(Ui.TextButton("恢复默认",()=>{var defaults=new ShortcutSet();captureKey.Reset(defaults.Capture);pinKey.Reset(defaults.Pin);translationKey.Reset(defaults.Translate);shortcutStatus.Text="已填入默认按键，点击保存后生效。";}));
            shortcutActions.Children.Add(Ui.TextButton("保存快捷键",()=>{var error=AppState.SaveShortcuts(new ShortcutSet {Capture=captureKey.Value,Pin=pinKey.Value,Translate=translationKey.Value});shortcutStatus.Foreground=error==null?Ui.Green:Ui.Brush("#B94B56");shortcutStatus.Text=error??"已保存，无需重启。";},true));panel.Children.Add(shortcutActions);panel.Children.Add(shortcutStatus);
            Closed+=(s,e)=>AppState.ShortcutRecorder=null;
            panel.Children.Add(new Border {Height=1,Background=Ui.Brush("#EBEFED"),Margin=new Thickness(0,18,0,16)});
            panel.Children.Add(new TextBlock {Text="翻译服务",FontSize=16,FontWeight=FontWeights.SemiBold,Foreground=Ui.Ink});
            var provider=new ComboBox {FontSize=13,Margin=new Thickness(0,10,0,8),Name="TranslationProvider"};provider.Items.Add("腾讯翻译 · 国内直连");provider.Items.Add("Google 翻译 · 使用系统网络 / 代理");provider.Items.Add("离线翻译 · 中文 ↔ 英文");provider.SelectedIndex=Translation.Provider=="offline"?2:Translation.Provider=="google"?1:0;panel.Children.Add(provider);
            var providerStatus=new TextBlock {Text="只发送文字，不上传图片。Google 需要能访问谷歌；沿用现有系统代理，不自动更换线路。",FontSize=12,Foreground=Ui.Brush("#718078"),TextWrapping=TextWrapping.Wrap};panel.Children.Add(providerStatus);
            panel.Children.Add(Ui.TextButton("应用翻译服务",()=>{string selected=provider.SelectedIndex==2?"offline":provider.SelectedIndex==1?"google":"tencent",previous=AppState.Settings.TranslationProvider;try{AppState.Settings.TranslationProvider=selected;AppState.Settings.SaveChecked();if(previous!=selected)AppState.ProviderChanged();providerStatus.Text="当前："+Translation.ProviderName+"。输入、截图及所选文字统一使用此服务。"+(selected=="offline"?"仅本机处理，请先安装下方离线包；失败时也不会联网。":"网页接口可能限流，失败时不会自动改用另一家。");}catch(Exception ex){AppState.Settings.TranslationProvider=previous;providerStatus.Text="保存失败："+ex.Message;}}));
            panel.Children.Add(new Border {Height=1,Background=Ui.Brush("#EBEFED"),Margin=new Thickness(0,18,0,16)});
            var offline=new OfflinePackPanel();panel.Children.Add(offline);Closed+=(s,e)=>offline.Dispose();
            panel.Children.Add(new Border {Height=1,Background=Ui.Brush("#EBEFED"),Margin=new Thickness(0,18,0,16)});
            var languages=new OcrLanguagePanel(selection);panel.Children.Add(languages);Closed+=(s,e)=>languages.Dispose();
            panel.Children.Add(new Border {Height=1,Background=Ui.Brush("#EBEFED"),Margin=new Thickness(0,20,0,16)});
            panel.Children.Add(new TextBlock {Text="贴图保存位置",FontSize=14,Foreground=Ui.Ink});
            panel.Children.Add(new TextBox {Text=AppState.Pins.Folder,IsReadOnly=true,BorderThickness=new Thickness(0),Background=Ui.Brush("#F3F6F4"),Padding=new Thickness(8),Margin=new Thickness(0,7,0,7),FontSize=12});
            var actions=new StackPanel {Orientation=Orientation.Horizontal};actions.Children.Add(Ui.TextButton("打开文件夹",()=>{try{Process.Start(new ProcessStartInfo(AppState.Pins.Folder){UseShellExecute=true});}catch(Exception ex){status.Text=ex.Message;}}));panel.Children.Add(actions);panel.Children.Add(status);
            try{updating=true;startup.IsChecked=Mori.StartupRegistration.IsEnabled(typeof(Program).Assembly.Location);}catch(Exception ex){startup.IsEnabled=false;status.Text=ex.Message;}finally{updating=false;}
            startup.Checked+=(s,e)=>SaveStartup();startup.Unchecked+=(s,e)=>SaveStartup();
        }
        static ShortcutEditor ShortcutRow(Panel panel,string name,Shortcut value){var row=new DockPanel();var editor=new ShortcutEditor(value);DockPanel.SetDock(editor,Dock.Right);row.Children.Add(editor);row.Children.Add(new TextBlock {Text=name,FontSize=13,Foreground=Ui.Ink,VerticalAlignment=VerticalAlignment.Center});panel.Children.Add(row);return editor;}
        void SaveStartup(){if(updating)return;try{Mori.StartupRegistration.SetEnabled(startup.IsChecked==true,typeof(Program).Assembly.Location);status.Foreground=Ui.Green;status.Text=startup.IsChecked==true?"已开启 Mori Desk 随 Windows 启动。":"已关闭，不影响贴图记录。";}catch(Exception ex){updating=true;startup.IsChecked=!(startup.IsChecked==true);updating=false;status.Foreground=Ui.Brush("#B94B56");status.Text=ex.Message;}}
    }
}
