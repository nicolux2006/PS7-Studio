using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using System.Text.Json;
using Windows.System;
using Microsoft.UI.Text;

namespace PS7Studio.App;

internal sealed partial class ScriptEditor
{
    private Border? completionPanel;
    private ListView? completionList;
    private Action? acceptCompletion;
    internal bool AcceptingCompletion {get;private set;}
    internal bool CompletionVisible=>completionPanel?.Visibility==Visibility.Visible;

    public void DismissCompletion()
    {
        if(completionPanel is not null)completionPanel.Visibility=Visibility.Collapsed;
        acceptCompletion=null;
    }

    public void ShowCompletions(JsonElement result,Func<bool> stillCurrent)
    {
        DismissCompletion();
        var choices=result.GetProperty("items").EnumerateArray().ToArray();
        if(choices.Length==0)return;
        if(completionList is null)
        {
            completionList=new ListView{Padding=new Thickness(0),IsItemClickEnabled=true,SelectionMode=ListViewSelectionMode.Single};
            var rowStyle=new Style(typeof(ListViewItem));
            rowStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty,32d));
            rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty,32d));
            rowStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,0,10,0)));
            completionList.ItemContainerStyle=rowStyle;
            ScrollViewer.SetVerticalScrollBarVisibility(completionList,ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollMode(completionList,ScrollMode.Enabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(completionList,ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(completionList,L.T("Completion"));
            completionPanel=new Border{Child=completionList,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
            SetColumn(completionPanel,1);Children.Add(completionPanel);
            completionList.ItemClick+=(_,e)=>{completionList.SelectedItem=e.ClickedItem;acceptCompletion?.Invoke();};
            completionList.PreviewKeyDown+=(_,e)=>{if(HandleCompletionKey(e.Key))e.Handled=true;};
            Unloaded+=(_,_)=>DismissCompletion();
        }
        completionPanel!.Background=(Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        completionPanel.BorderBrush=(Brush)Application.Current.Resources["TextControlBorderBrush"];
        completionPanel.Width=Math.Min(420,Math.Max(100,Box.ActualWidth-12));
        Box.Document.Selection.GetRect(PointOptions.ClientCoordinates,out var rect,out _);
        var left=Math.Clamp(rect.X+Box.Padding.Left,0,Math.Max(0,Box.ActualWidth-completionPanel.Width-4));
        var top=rect.Y+Box.Padding.Top+Math.Max(rect.Height,Box.FontSize*1.4);
        var listHeight=Math.Min(5,choices.Length)*32d;
        if(top+listHeight+2>Box.ActualHeight&&rect.Y>listHeight+2)top=rect.Y-listHeight-2;
        completionList.Height=listHeight;
        completionPanel.Margin=new Thickness(left,Math.Max(0,top),0,0);
        completionList.ItemsSource=choices.Select(x=>x.GetProperty("label").GetString()).ToArray();
        completionList.SelectedIndex=0;
        var start=result.GetProperty("start").GetInt32();var length=result.GetProperty("length").GetInt32();
        acceptCompletion=()=>
        {
            var index=completionList.SelectedIndex;
            var valid=stillCurrent();DismissCompletion();
            if(!valid||index<0||index>=choices.Length)return;
            AcceptingCompletion=true;
            try{SelectSource(start,length);Insert(choices[index].GetProperty("text").GetString()!);Box.Focus(FocusState.Programmatic);}
            finally{AcceptingCompletion=false;}
        };
        completionPanel.Visibility=Visibility.Visible;
    }

    internal bool HandleCompletionKey(VirtualKey key)
    {
        if(!CompletionVisible)return false;
        if(key is VirtualKey.Enter or VirtualKey.Tab){acceptCompletion?.Invoke();return true;}
        if(key==VirtualKey.Escape){DismissCompletion();Box.Focus(FocusState.Programmatic);return true;}
        if(key is VirtualKey.Up or VirtualKey.Down)
        {
            completionList!.SelectedIndex=Math.Clamp(completionList.SelectedIndex+(key==VirtualKey.Down?1:-1),0,completionList.Items.Count-1);
            completionList.ScrollIntoView(completionList.SelectedItem);return true;
        }
        return false;
    }
}
