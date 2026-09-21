using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PS7Studio.Localization;

namespace PS7Studio.App;

public sealed partial class MainWindow
{
    private readonly LocalizedBindings localized=new();
    private bool applyingLanguage;
    private TextBlock Label(string key)
    {
        var label=new TextBlock();localized.Add(key,value=>label.Text=value);return label;
    }
    private void ApplyLanguage()
    {
        var emptyDetail=detail.Text==L.T("NoSelection");
        localized.Apply(settings.Language);
        if(emptyDetail)detail.Text=L.T("NoSelection");
        applyingLanguage=true;
        try
        {
            var selected=panel.SelectedIndex;panel.SelectedIndex=-1;
            foreach(var item in panel.Items.OfType<ComboBoxItem>())item.Content=L.T((string)item.Tag);
            panel.SelectedIndex=selected;
        }
        finally{applyingLanguage=false;}
        command.PlaceholderText=L.T("CommandInput");search.PlaceholderText=L.T("SearchCommands");insert.Content=L.T("Insert");
        AutomationProperties.SetName(output,L.T("Console"));AutomationProperties.SetName(command,L.T("CommandInput"));AutomationProperties.SetName(sessionPicker,L.T("Session"));
        foreach(var editor in Editors){AutomationProperties.SetName(editor.Box,L.T("Script"));UpdateTab(editor);}
        LocalizeTabChrome(tabs);
        status.Text=L.T(engineState);UpdatePosition();
    }
    private void LocalizeTabChrome(DependencyObject parent)
    {
        if(parent is FrameworkElement element&&element.Name is "AddButton" or "CloseButton")
        {
            var text=L.T(element.Name=="AddButton"?"New":"Close");AutomationProperties.SetName(element,text);ToolTipService.SetToolTip(element,text);
        }
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)LocalizeTabChrome(VisualTreeHelper.GetChild(parent,i));
    }
}
