using System;
using System.Windows;
using System.Windows.Controls;
using Mori;
namespace MoriDesk {
 internal sealed class CanvasDestination {
  internal string ProjectId,NewName;
  internal bool IsNew {get{return ProjectId==null&&NewName!=null;}}
 }
 // Choosing a destination is read-only. Creation happens after asset validation.
 internal sealed class CanvasDestinationWindow:Window {
  internal readonly CanvasProjectsPage Projects;
  internal readonly TextBox ProjectName;
  internal readonly Button ConfirmNew;
  readonly Grid newProject;
  readonly TextBlock validation=new TextBlock();
  internal CanvasDestination Result {get;private set;}
  internal CanvasDestinationWindow(Window owner,FreeCanvasStore store,MatureCanvasStore matureStore,int imageCount){
   Title="加入画布 · "+imageCount+" 张图片";Width=850;Height=620;MinWidth=620;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;if(owner!=null&&owner.IsVisible)Owner=owner;ShowInTaskbar=false;Theme.Attach(this,owner);
   var root=new Grid();root.SetResourceReference(Panel.BackgroundProperty,"Canvas");root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Content=root;
   var selection=new TextBlock{Text="已选 "+imageCount+" 张图片",Margin=new Thickness(24,14,24,0),FontSize=13};selection.SetResourceReference(TextBlock.ForegroundProperty,"Muted");root.Children.Add(selection);
   Projects=new CanvasProjectsPage(store,(id,paths)=>ChooseExisting(id),data=>new string[0],data=>new string[0],()=>true,()=>false,()=>false,message=>validation.Text=message,matureStore,BeginNew,true);Grid.SetRow(Projects,1);root.Children.Add(Projects);
   newProject=new Grid{Margin=new Thickness(24,0,24,12),Visibility=Visibility.Collapsed};newProject.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});newProject.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});Grid.SetRow(newProject,2);root.Children.Add(newProject);
   ProjectName=new TextBox{Name="NewCanvasName",MaxLength=120,MinHeight=34,VerticalContentAlignment=VerticalAlignment.Center,ToolTip="给新画布起个项目名",Margin=new Thickness(0,0,10,0)};System.Windows.Automation.AutomationProperties.SetName(ProjectName,"新画布名称");newProject.Children.Add(ProjectName);
   ConfirmNew=ReferencePage.Button("新建并加入",ChooseNew);ConfirmNew.Name="ConfirmNewCanvas";Grid.SetColumn(ConfirmNew,1);newProject.Children.Add(ConfirmNew);
   var footer=new DockPanel{Margin=new Thickness(24,0,24,18)};Grid.SetRow(footer,3);root.Children.Add(footer);var cancel=ReferencePage.Button("取消",()=>{DialogResult=false;});cancel.IsCancel=true;DockPanel.SetDock(cancel,Dock.Right);footer.Children.Add(cancel);
   validation.Text="原素材保留在素材库，可加入多个画布";validation.TextWrapping=TextWrapping.Wrap;validation.VerticalAlignment=VerticalAlignment.Center;validation.Margin=new Thickness(0,0,12,0);validation.SetResourceReference(TextBlock.ForegroundProperty,"Muted");footer.Children.Add(validation);
   ProjectName.KeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Enter){ChooseNew();e.Handled=true;}};
  }
  internal void BeginNew(){newProject.Visibility=Visibility.Visible;if(string.IsNullOrWhiteSpace(ProjectName.Text)){string name="画布 "+(Projects.ProjectsForTests.Count+1);while(Projects.ProjectsForTests.Exists(p=>p.Name==name))name+=" 新";ProjectName.Text=name;}ProjectName.Focus();ProjectName.SelectAll();}
  bool ChooseExisting(string id){Result=new CanvasDestination{ProjectId=id};if(IsVisible)DialogResult=true;return true;}
  internal void ChooseNew(){string name=ProjectName.Text.Trim();if(name.Length==0){validation.Text="请填写画布名称";ProjectName.Focus();return;}Result=new CanvasDestination{NewName=name};if(IsVisible)DialogResult=true;}
 }
}
