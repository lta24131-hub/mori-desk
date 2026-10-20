using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace QingJie {
    // Deliberately independent of AppState and native services: changing this
    // draft neither starts an engine nor changes an in-progress capture.
    internal sealed class CaptureFeaturesPanel : StackPanel {
        readonly Preferences preferences;
        readonly Action save;
        readonly Dictionary<string,CheckBox> choices=new Dictionary<string,CheckBox>();
        readonly TextBlock status;
        bool updating;
        internal CaptureFeaturesPanel(Preferences preferences,Action save){
            if(preferences==null)throw new ArgumentNullException("preferences");
            if(save==null)throw new ArgumentNullException("save");
            this.preferences=preferences;this.save=save;
            Name="CaptureFeatures";SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,new FontFamily("Microsoft YaHei UI"));
            Children.Add(Label("截图功能",16,true));
            Children.Add(Help("勾选你需要的工具。关闭的工具不再显示，仅按需加载已启用功能所需的组件。实际占用取决于正在使用的功能。"));
            var presets=new WrapPanel {Margin=new Thickness(-4,5,0,4)};
            presets.Children.Add(ActionButton("基础截图","CaptureFeaturesBasic",()=>SetDraft(CaptureFeatures.Basic())));
            presets.Children.Add(ActionButton("全部开启","CaptureFeaturesAll",()=>SetDraft(new CaptureFeatures())));
            Children.Add(presets);
            var annotations=Group("标注工具");
            AddChoice(annotations,"Rectangle","矩形");AddChoice(annotations,"Ellipse","椭圆");
            AddChoice(annotations,"Lines","箭头 / 直线");AddChoice(annotations,"Pen","画笔");
            AddChoice(annotations,"Text","文字");AddChoice(annotations,"Mosaic","马赛克");
            var enhancements=Group("识别与增强");
            AddChoice(enhancements,"SmartSelection","自动识别选区");
            AddChoice(enhancements,"Ocr","提取文字");AddChoice(enhancements,"Translation","截图翻译","截图翻译会按需使用文字识别；无需同时勾选“提取文字”。");
            AddChoice(enhancements,"Scrolling","滚动长截图");
            Children.Add(Help("截图翻译仍会按需识别文字，无需同时勾选“提取文字”。"));
            var startup=new WrapPanel {Margin=new Thickness(0,4,0,0)};Children.Add(startup);
            AddChoice(startup,"ScrollPrewarm","长截图快速启动","进入截图后提前加载长截图组件，减少点击后的等待；只在滚动长截图开启时生效。");
            Children.Add(Help("快速启动会在截图期间临时多占内存，结束截图即释放；取消勾选则点击长截图时再加载。预加载不会读取屏幕内容。"));
            var outputs=Group("输出方式");
            AddChoice(outputs,"Pin","钉在桌面");AddChoice(outputs,"Collect","收藏到素材库");
            Children.Add(Help("框选、调整边框、复制、保存和 Esc 退出始终保留。关闭截图功能不影响独立翻译和已有贴图，也不会卸载已安装的组件或模型。"));
            var actions=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
            actions.Children.Add(ActionButton("保存截图功能","SaveCaptureFeatures",()=>SaveDraft(),true));Children.Add(actions);
            status=Help("保存后从下一次截图生效，无需重启。");status.Name="CaptureFeaturesStatus";AutomationProperties.SetName(status,"截图功能保存状态");Children.Add(status);
            SetDraft(preferences.CaptureFeatures??new CaptureFeatures());
            status.Text="保存后从下一次截图生效，无需重启。";
        }
        static TextBlock Label(string text,double size,bool strong=false){return Ui.Theme(new TextBlock {Text=text,FontSize=size,FontWeight=strong?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap},TextBlock.ForegroundProperty,"Text");}
        static TextBlock Help(string text){return Ui.Theme(new TextBlock {Text=text,FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,4)},TextBlock.ForegroundProperty,"Muted");}
        static Button ActionButton(string text,string name,Action action,bool primary=false){var button=Ui.TextButton(text,action,primary);button.Name=name;AutomationProperties.SetName(button,text);return button;}
        WrapPanel Group(string title){var label=Label(title,13,true);label.Margin=new Thickness(0,10,0,4);Children.Add(label);var group=new WrapPanel();Children.Add(group);return group;}
        void AddChoice(WrapPanel group,string key,string title,string help=null){
            var checkbox=new CheckBox {Name="CaptureFeature_"+key,Content=title,MinWidth=126,MinHeight=28,FontSize=13,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};
            Ui.Theme(checkbox,Control.ForegroundProperty,"Text");AutomationProperties.SetName(checkbox,title);
            string description=help??("在截图中显示“"+title+"”；保存后从下一次截图生效。");
            checkbox.ToolTip=description;AutomationProperties.SetHelpText(checkbox,description);
            checkbox.Checked+=(s,e)=>DraftChanged();checkbox.Unchecked+=(s,e)=>DraftChanged();choices.Add(key,checkbox);group.Children.Add(checkbox);
        }
        void DraftChanged(){
            if(choices.ContainsKey("ScrollPrewarm")&&choices.ContainsKey("Scrolling"))choices["ScrollPrewarm"].IsEnabled=Read("Scrolling");
            if(updating||status==null)return;Ui.Theme(status,TextBlock.ForegroundProperty,"Muted");status.Text="尚未保存，点击“保存截图功能”后从下一次截图生效。";
        }
        internal CaptureFeatures Draft {get{return new CaptureFeatures {
            SmartSelection=Read("SmartSelection"),Rectangle=Read("Rectangle"),Ellipse=Read("Ellipse"),Lines=Read("Lines"),Pen=Read("Pen"),Text=Read("Text"),Mosaic=Read("Mosaic"),Ocr=Read("Ocr"),Translation=Read("Translation"),Scrolling=Read("Scrolling"),ScrollPrewarm=Read("ScrollPrewarm"),Pin=Read("Pin"),Collect=Read("Collect")
        };}}
        bool Read(string key){return choices[key].IsChecked==true;}
        internal void SetDraft(CaptureFeatures features){
            if(features==null)throw new ArgumentNullException("features");
            updating=true;
            try{foreach(var pair in choices)pair.Value.IsChecked=(bool)typeof(CaptureFeatures).GetField(pair.Key).GetValue(features);}finally{updating=false;}
            DraftChanged();
        }
        internal bool SaveDraft(){
            var previous=preferences.CaptureFeatures;
            try{
                preferences.CaptureFeatures=Draft;save();
                Ui.Theme(status,TextBlock.ForegroundProperty,"Accent");status.Text="已保存，从下一次截图生效，无需重启。";return true;
            }catch(Exception error){
                preferences.CaptureFeatures=previous;
                Ui.Theme(status,TextBlock.ForegroundProperty,"ErrorText");status.Text="保存失败，原设置仍然有效。你的勾选已保留，可重试。\n"+error.Message;return false;
            }
        }
    }
}
