using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text;

namespace PS7Studio.App;

public sealed partial class MainWindow
{
    private async Task BuildCommandAsync()
    {
        if(results.SelectedItem is not CommandEntry entry)return;
        EnsureIdle();
        var owner=ActiveSession;var editor=Editor;
        var metadata=await engine!.RequestAsync("commandInfo",new {name=entry.Name});
        if(!ReferenceEquals(owner,ActiveSession)||!ReferenceEquals(editor,Editor))return;
        if(!metadata.TryGetProperty("sets",out var setsValue)||setsValue.ValueKind!=System.Text.Json.JsonValueKind.Array)return;
        var sets=setsValue.EnumerateArray().ToArray();if(sets.Length==0)return;
        var setPicker=new ComboBox{Header=L.T("ParameterSet"),ItemsSource=sets.Select(x=>x.GetProperty("name").GetString()).ToArray(),HorizontalAlignment=HorizontalAlignment.Stretch};
        var fields=new StackPanel{Spacing=10};
        var inputs=new List<(string name,bool mandatory,Control input)>();
        void Render()
        {
            fields.Children.Clear();inputs.Clear();if(setPicker.SelectedIndex<0)return;
            foreach(var parameter in sets[setPicker.SelectedIndex].GetProperty("parameters").EnumerateArray().OrderByDescending(x=>x.GetProperty("mandatory").GetBoolean()).ThenBy(x=>x.GetProperty("name").GetString()))
            {
                var name=parameter.GetProperty("name").GetString()!;var mandatory=parameter.GetProperty("mandatory").GetBoolean();var type=parameter.GetProperty("type").GetString();
                var label=name+(mandatory?" *":"")+" · "+type;
                Control input=type=="SwitchParameter"?new CheckBox{Content=label}:new TextBox{Header=label,PlaceholderText=L.T("Value")};
                inputs.Add((name,mandatory,input));fields.Children.Add(input);
            }
        }
        setPicker.SelectionChanged+=(_,_)=>Render();setPicker.SelectedIndex=0;
        var content=new StackPanel{Spacing=12,MinWidth=420};content.Children.Add(setPicker);content.Children.Add(new ScrollViewer{Content=fields,MaxHeight=380});
        var validation=new TextBlock{TextWrapping=TextWrapping.Wrap};content.Children.Add(validation);
        var dialog=new ContentDialog{XamlRoot=root.XamlRoot,Title=L.T("BuildCommand")+" · "+entry.Name,Content=content,PrimaryButtonText=L.T("Insert"),CloseButtonText=L.T("Cancel")};
        string? script=null;
        dialog.PrimaryButtonClick+=(_,e)=>
        {
            var missing=inputs.Where(x=>x.mandatory&&x.input is TextBox box&&string.IsNullOrWhiteSpace(box.Text)).Select(x=>x.name).ToArray();
            if(missing.Length>0){validation.Text=L.T("Required")+": "+string.Join(", ",missing);e.Cancel=true;return;}
            var text=new StringBuilder("& '").Append(entry.Name.Replace("'","''")).Append('\'');
            foreach(var (name,_,input) in inputs)
            {
                if(input is CheckBox check&&check.IsChecked==true)text.Append(" -").Append(name);
                if(input is TextBox box&&box.Text.Length>0)text.Append(" -").Append(name).Append(" '").Append(box.Text.Replace("'","''")).Append('\'');
            }
            script=text.ToString();
        };
        await dialogs.WaitAsync();dialogOpen=true;
        try{if(await dialog.ShowAsync()==ContentDialogResult.Primary&&script is not null&&ReferenceEquals(editor,Editor))editor?.Insert(script);}
        finally{dialogOpen=false;dialogs.Release();}
    }
    private Task ShowCommandHelpAsync()=>ShowHelpAsync((results.SelectedItem as CommandEntry)?.Name ?? "Get-Help");
    private async Task ShowHelpAsync(string name)
    {
        EnsureIdle();var owner=ActiveSession;
        var result=await engine!.RequestAsync("help",new {name});
        if(!ReferenceEquals(owner,ActiveSession))return;
        var text=result.GetProperty("text").GetString();
        if(string.IsNullOrWhiteSpace(text))text=L.T("NoResults");
        await dialogs.WaitAsync();dialogOpen=true;
        try
        {
            await new ContentDialog{XamlRoot=root.XamlRoot,Title=L.T("Help")+" · "+name,
                Content=new ScrollViewer{MaxHeight=480,Content=new TextBlock{Text=text,IsTextSelectionEnabled=true,TextWrapping=TextWrapping.Wrap,MaxWidth=650}},
                CloseButtonText=L.T("Close"),DefaultButton=ContentDialogButton.Close}.ShowAsync();
        }
        finally{dialogOpen=false;dialogs.Release();}
    }
}
