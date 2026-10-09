using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QingJie {
    // Created only when opened: no OCR, clipboard watcher, model or resident timer.
    public sealed class WriteTranslationWindow : Window {
        const int Limit=5000;
        readonly TextBox input=Editor("TranslationInput",false),output=Editor("TranslationOutput",true);
        static string IdleStatus {get{return Translation.Provider=="offline"?"本地 · 离线":Translation.Provider=="google"?"Google · 在线":"腾讯 · 在线";}}
        readonly TextBlock status=new TextBlock {Text=IdleStatus,FontSize=11,Foreground=Ui.Brush("#858A91"),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
        readonly TextBlock inputHint=new TextBlock {Text="输入文字…",FontSize=15,Foreground=Ui.Brush("#A2A6AC"),IsHitTestVisible=false,Margin=new Thickness(1,2,0,0)};
        readonly StackPanel resultArea=new StackPanel {Name="TranslationResultArea",Visibility=Visibility.Collapsed};
        readonly Button translate,copy,language;
        readonly Func<string,CancellationToken,string,Task<string>> translateText;
        CancellationTokenSource pending;
        string target="en";
        bool closed;

        public WriteTranslationWindow():this((text,token,language)=>Translation.Translate(text,token,language)){}
        internal WriteTranslationWindow(Func<string,CancellationToken,string,Task<string>> translator){
            translateText=translator;Title="Mori Desk · 输入翻译";Icon=Ui.AppIcon;Width=400;SizeToContent=SizeToContent.Height;MaxHeight=520;ResizeMode=ResizeMode.NoResize;WindowStyle=WindowStyle.None;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brushes.White;FontFamily=new FontFamily("Microsoft YaHei UI");UseLayoutRounding=true;
            var panel=new StackPanel {Margin=new Thickness(20,12,20,16)};var frame=new Border {Background=Brushes.White,BorderBrush=Ui.Brush("#DFE2E6"),BorderThickness=new Thickness(1),Child=panel};Content=frame;WindowBackdrop.Attach(this,frame);
            var header=new DockPanel {Margin=new Thickness(-7,-3,-7,10),Background=Brushes.Transparent};panel.Children.Add(header);
            header.MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==1){try{DragMove();}catch(InvalidOperationException){}}};
            var close=QuietButton("\uE711","关闭 · Esc",Close);close.Name="CloseTranslation";close.FontFamily=new FontFamily("Segoe MDL2 Assets");close.FontSize=11;DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
            language=QuietButton("译成英文  ⇄","切换译成中文或英文；自动识别原文语言",()=>ChangeTarget(target=="en"?"zh":"en"));language.Name="TranslationTarget";language.HorizontalAlignment=HorizontalAlignment.Left;header.Children.Add(language);
            input.MinHeight=62;input.MaxHeight=140;var inputArea=new Grid();inputArea.Children.Add(input);inputArea.Children.Add(inputHint);panel.Children.Add(inputArea);
            resultArea.Children.Add(new Border {Height=1,Background=Ui.Brush("#EAECF0"),Margin=new Thickness(0,14,0,14)});output.MinHeight=40;output.MaxHeight=170;resultArea.Children.Add(output);panel.Children.Add(resultArea);
            var actions=new DockPanel {Margin=new Thickness(0,14,0,0)};panel.Children.Add(actions);
            translate=QuietButton("翻译","翻译 · Ctrl+Enter",()=>StartOrCancel(),true);translate.Name="TranslateInput";translate.MinWidth=60;translate.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(translate,Dock.Right);actions.Children.Add(translate);
            copy=QuietButton("复制","复制译文",CopyOutput);copy.Name="CopyTranslation";copy.IsEnabled=false;copy.Visibility=Visibility.Collapsed;DockPanel.SetDock(copy,Dock.Right);actions.Children.Add(copy);actions.Children.Add(status);
            status.ToolTip="只在点击翻译时处理文字。在线模式发送到所选服务；离线模式仅在本机处理。不保存输入历史；缓存仅在内存中短期保留。";
            output.TextChanged+=(s,e)=>resultArea.Visibility=string.IsNullOrEmpty(output.Text)?Visibility.Collapsed:Visibility.Visible;
            input.TextChanged+=(s,e)=>InvalidateTranslation();RefreshActions();
            AppState.TranslationProviderChanged+=InvalidateTranslation;
            Loaded+=(s,e)=>input.Focus();Closed+=(s,e)=>{closed=true;AppState.TranslationProviderChanged-=InvalidateTranslation;CancelPending();input.Clear();output.Clear();};
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}else if(e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){e.Handled=true;StartOrCancel();}};
        }
        static TextBox Editor(string name,bool readOnly){return new TextBox {Name=name,IsReadOnly=readOnly,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=15,Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=Brushes.Transparent,Foreground=readOnly?Ui.Brush("#48515C"):Ui.Ink,IsInactiveSelectionHighlightEnabled=true};}
        internal static Button QuietButton(string text,string hint,Action action,bool primary=false){
            var button=new Button {Content=text,ToolTip=hint,FontSize=13,Padding=new Thickness(9,7,9,7),BorderThickness=new Thickness(0),Background=primary?Ui.Brush("#282E36"):Brushes.Transparent,Foreground=primary?Brushes.White:Ui.Brush("#616871"),Cursor=Cursors.Hand};
            var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);button.Template=new ControlTemplate(typeof(Button)){VisualTree=border};
            button.MouseEnter+=(s,e)=>button.Background=Ui.Brush(primary?"#414B58":"#F3F4F6");button.MouseLeave+=(s,e)=>button.Background=primary?Ui.Brush("#282E36"):Brushes.Transparent;button.IsEnabledChanged+=(s,e)=>button.Opacity=button.IsEnabled?1:.35;button.Click+=(s,e)=>action();System.Windows.Automation.AutomationProperties.SetName(button,hint);return button;
        }
        void RefreshActions(){inputHint.Visibility=string.IsNullOrEmpty(input.Text)?Visibility.Visible:Visibility.Collapsed;copy.Visibility=copy.IsEnabled?Visibility.Visible:Visibility.Collapsed;translate.Content=pending!=null?"取消":"翻译";translate.IsEnabled=pending!=null||(!string.IsNullOrWhiteSpace(input.Text)&&input.Text.Length<=Limit);}
        void CancelPending(){var request=pending;pending=null;if(request!=null)request.Cancel();}
        void InvalidateTranslation(){CancelPending();output.Clear();copy.IsEnabled=false;status.Text=input.Text.Length>Limit?"超过 5000 字，请分段翻译":IdleStatus;RefreshActions();}
        void ChangeTarget(string value){target=value;language.Content=value=="en"?"译成英文  ⇄":"译成中文  ⇄";InvalidateTranslation();}
        async void StartOrCancel(){if(pending!=null){CancelPending();status.Text="已取消，可以继续修改。";RefreshActions();return;}await TranslateNow();}
        internal async Task TranslateNow(){
            if(closed||pending!=null||string.IsNullOrWhiteSpace(input.Text)||input.Text.Length>Limit)return;
            string text=input.Text,language=target;var request=new CancellationTokenSource();pending=request;output.Clear();copy.IsEnabled=false;status.Text="正在翻译…";RefreshActions();
            try{var result=await translateText(text,request.Token,language);if(closed||pending!=request||request.IsCancellationRequested)return;output.Text=result;copy.IsEnabled=!string.IsNullOrWhiteSpace(result);status.Text=IdleStatus;}
            catch(OperationCanceledException){if(!closed&&pending==request)status.Text=request.IsCancellationRequested?"已取消。":"翻译超时，请重试。";}
            catch(Exception ex){if(!closed&&pending==request)status.Text=ex.Message;}
            finally{if(pending==request){pending=null;RefreshActions();}request.Dispose();}
        }
        void CopyOutput(){if(string.IsNullOrWhiteSpace(output.Text)||!copy.IsEnabled)return;try{Clipboard.SetText(output.Text);status.Text="译文已复制。";}catch{status.Text="剪贴板暂时被占用，请再次点击复制。";}}
    }
}
