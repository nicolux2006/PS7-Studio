using Microsoft.UI.Xaml;

namespace PS7Studio.App;

public partial class App : Application
{
    private Window? window;
    public App() { InitializeComponent(); UnhandledException += (_,e)=> {Log(e.Exception);}; }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    { window=new MainWindow(); window.Activate(); }
    internal static void Log(Exception exception)
    {
        try
        {
            var folder=System.IO.Path.Combine(AppState.DirectoryPath,"logs");
            Directory.CreateDirectory(folder);
            var path=System.IO.Path.Combine(folder,"application.log");
            if(File.Exists(path)&&new FileInfo(path).Length>1024*1024)File.Move(path,path+".previous",true);
            File.AppendAllText(path,$"{DateTimeOffset.Now:O} {exception.GetType().Name} {exception.HResult:X8}\n{exception.StackTrace}\n");
        } catch { }
    }
}
