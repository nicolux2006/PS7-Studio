using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PS7Studio.App;

public sealed partial class MainWindow
{
    private async Task ShowAboutAsync()
    {
        var content=new StackPanel{Spacing=12,MaxWidth=520};
        content.Children.Add(new TextBlock{Text="PS7 Studio 26.09.0",FontSize=22});
        content.Children.Add(new TextBlock{Text="Modern PowerShell 7 Integrated Scripting Environment",TextWrapping=TextWrapping.Wrap});
        var credit=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};
        credit.Children.Add(new TextBlock{Text="Developed by",VerticalAlignment=VerticalAlignment.Center});
        credit.Children.Add(new HyperlinkButton{Content="@nicolux2006",NavigateUri=new Uri("https://github.com/nicolux2006"),Padding=new Thickness(0)});
        content.Children.Add(credit);
        content.Children.Add(new TextBlock{Text=L.T("Development"),TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new TextBlock{Text="Independent software. Not an official Microsoft product.",TextWrapping=TextWrapping.Wrap});
        await dialogs.WaitAsync();dialogOpen=true;
        try{await new ContentDialog{XamlRoot=root.XamlRoot,Title=L.T("About"),Content=content,CloseButtonText=L.T("Close"),DefaultButton=ContentDialogButton.Close}.ShowAsync();}
        finally{dialogOpen=false;dialogs.Release();}
    }
}
