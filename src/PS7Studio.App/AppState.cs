namespace PS7Studio.App;
internal static class AppState
{
    private static readonly Lazy<PS7Studio.Core.WorkspaceLease> instance=new(()=>PS7Studio.Core.WorkspaceLease.AcquireInstance(SettingsDirectoryPath));
    public static PS7Studio.Core.WorkspaceLease Workspace=>instance.Value;
    public static string DirectoryPath=>Workspace.DirectoryPath;
    public static string SettingsDirectoryPath
    {
        get
        {
            var args=Environment.GetCommandLineArgs();var index=Array.IndexOf(args,"--data-directory");
            return index>=0&&index+1<args.Length?System.IO.Path.GetFullPath(args[index+1]):System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PS7Studio");
        }
    }
    public static IEnumerable<string> StartupFiles
    {
        get
        {
            var args=Environment.GetCommandLineArgs();
            for(var i=1;i<args.Length;i++){if(args[i]=="--data-directory"){i++;continue;}if(File.Exists(args[i]))yield return System.IO.Path.GetFullPath(args[i]);}
        }
    }
}
