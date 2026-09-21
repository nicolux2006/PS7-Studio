using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace PS7Studio.App;
public sealed partial class MainWindow
{
    private async Task ShowSettingsAsync()
    {
        var stack=new StackPanel{Spacing=12,MinWidth=400};
        var language=new ComboBox{Header=L.T("Language"),ItemsSource=new[]{"English","Deutsch"},SelectedIndex=settings.Language=="de"?1:0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var theme=new ComboBox{Header=L.T("Theme"),ItemsSource=new[]{L.T("System"),L.T("Light"),L.T("Dark")},SelectedIndex=settings.Theme=="Light"?1:settings.Theme=="Dark"?2:0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var fonts=InstalledFonts.GetFamilies();
        var font=new ComboBox{Header=L.T("Font"),ItemsSource=fonts,IsEditable=false,IsTextSearchEnabled=true,HorizontalAlignment=HorizontalAlignment.Stretch,MaxDropDownHeight=300};
        font.SelectedItem=fonts.FirstOrDefault(x=>x.Equals(settings.Font,StringComparison.OrdinalIgnoreCase)) ?? fonts.FirstOrDefault(x=>x=="Consolas") ?? fonts[0];
        var preview=new TextBlock{Text="Aa Bb 0123456789  {} [] ()  $Name",FontSize=18,FontFamily=new Microsoft.UI.Xaml.Media.FontFamily((string)font.SelectedItem)};
        font.SelectionChanged+=(_,_)=>{if(font.SelectedItem is string name)preview.FontFamily=new Microsoft.UI.Xaml.Media.FontFamily(name);};
        var size=new NumberBox{Header=L.T("FontSize"),Minimum=8,Maximum=48,Value=settings.FontSize,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};var width=new NumberBox{Header=L.T("TabWidth"),Minimum=1,Maximum=16,Value=settings.TabWidth,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
        var spaces=new CheckBox{Content=L.T("Spaces"),IsChecked=settings.UseSpaces};var wrap=new CheckBox{Content=L.T("Wrap"),IsChecked=settings.WordWrap};var numbers=new CheckBox{Content=L.T("LineNumbers"),IsChecked=settings.LineNumbers};var restore=new CheckBox{Content=L.T("RestoreSession"),IsChecked=settings.RestoreSession};var profiles=new CheckBox{Content=L.T("Profiles"),IsChecked=settings.LoadProfiles};var directory=new TextBox{Header=L.T("StartupDirectory"),Text=settings.StartupDirectory};
        var whitespace=new CheckBox{Content=L.T("Whitespace"),IsChecked=settings.ShowWhitespace};var autoIndent=new CheckBox{Content=L.T("AutoIndent"),IsChecked=settings.AutoIndent};
        foreach(var control in new UIElement[]{language,theme,font,preview,size,width,spaces,autoIndent,wrap,numbers,whitespace,restore,profiles,directory})stack.Children.Add(control);
        await dialogs.WaitAsync();dialogOpen=true;
        try
        {
            if(await new ContentDialog{XamlRoot=root.XamlRoot,Title=L.T("Settings"),Content=new ScrollViewer{Content=stack,MaxHeight=580},PrimaryButtonText=L.T("Apply"),CloseButtonText=L.T("Cancel"),DefaultButton=ContentDialogButton.Primary}.ShowAsync()!=ContentDialogResult.Primary)return;
            settings.Language=language.SelectedIndex==1?"de":"en";settings.Theme=theme.SelectedIndex==1?"Light":theme.SelectedIndex==2?"Dark":"Default";settings.Font=(string)font.SelectedItem;settings.FontSize=double.IsNaN(size.Value)?14:Math.Clamp(size.Value,8,48);settings.TabWidth=double.IsNaN(width.Value)?4:(int)Math.Clamp(width.Value,1,16);settings.UseSpaces=spaces.IsChecked==true;settings.WordWrap=wrap.IsChecked==true;settings.LineNumbers=numbers.IsChecked==true;settings.RestoreSession=restore.IsChecked==true;settings.LoadProfiles=profiles.IsChecked==true;settings.StartupDirectory=directory.Text;
            settings.ShowWhitespace=whitespace.IsChecked==true;settings.AutoIndent=autoIndent.IsChecked==true;
            ApplyLanguage();ApplySettings();if(!settings.RestoreSession)await store.ClearSessionAsync();await store.SaveSettingsAsync(settings);QueueAnalysis();
        }
        finally{dialogOpen=false;dialogs.Release();}
    }
}
