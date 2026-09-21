using System.Security.Cryptography;
using System.Text.Json;

namespace PS7Studio.Core;

public sealed class StudioSettings
{
    public int Schema {get;set;}=2;
    public string Language {get;set;}=System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="de"?"de":"en";
    public string Theme {get;set;}="Default";
    public string Font {get;set;}="Cascadia Mono";
    public double FontSize {get;set;}=14;
    public int TabWidth {get;set;}=4;
    public bool UseSpaces {get;set;}=true;
    public bool WordWrap {get;set;}
    public bool LineNumbers {get;set;}=true;
    public bool ShowWhitespace {get;set;}
    public bool AutoIndent {get;set;}=true;
    public double InspectorWidth {get;set;}=310;
    public double ConsoleHeight {get;set;}=260;
    public bool RestoreSession {get;set;}=true;
    public bool LoadProfiles {get;set;}
    public string StartupDirectory {get;set;}=@"C:\";
    public List<string> RecentFiles {get;set;}=[];
    public List<string> OpenFiles {get;set;}=[];
    public int ActiveTab {get;set;}
    public int Width {get;set;}=1400;
    public int Height {get;set;}=900;
    public bool ConsoleVisible {get;set;}=true;
    public bool InspectorVisible {get;set;}=true;
}
public sealed record RecoveryDocument(Guid Id,string? Path,string Text);
public sealed record SessionSnapshot(DocumentSnapshot[] Documents,int ActiveTab);
public sealed class StateStore(string folder,string? recoveryFolder=null)
{
    private readonly SemaphoreSlim saving=new(1,1);
    private static readonly JsonSerializerOptions Options=new(){WriteIndented=true};
    public async Task<StudioSettings> LoadSettingsAsync()
    {
        try
        {
            await using var input=new FileStream(System.IO.Path.Combine(folder,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var value=await JsonSerializer.DeserializeAsync<StudioSettings>(input) ?? new();
            value.FontSize=Math.Clamp(value.FontSize,8,48);value.TabWidth=Math.Clamp(value.TabWidth,1,16);
            value.Width=Math.Clamp(value.Width,640,7680);value.Height=Math.Clamp(value.Height,480,4320);
            value.InspectorWidth=double.IsFinite(value.InspectorWidth)?Math.Clamp(value.InspectorWidth,220,650):310;
            value.ConsoleHeight=double.IsFinite(value.ConsoleHeight)?Math.Clamp(value.ConsoleHeight,100,1000):260;
            value.Language=value.Language=="de"?"de":"en";
            value.RecentFiles ??=[];value.OpenFiles ??=[];
            if(string.IsNullOrWhiteSpace(value.StartupDirectory)||(value.Schema<2&&string.Equals(value.StartupDirectory.TrimEnd('\\'),Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)))value.StartupDirectory=@"C:\";
            value.Schema=2;
            return value;
        }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException) {return new();}
    }
    public Task SaveSettingsAsync(StudioSettings settings)=>WriteAtomicAsync(folder,"settings.json",JsonSerializer.SerializeToUtf8Bytes(settings,Options));
    public async Task SaveSessionAsync(IEnumerable<ScriptDocument> documents,int activeTab)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var snapshot=new SessionSnapshot(documents.Select(d=>d.CaptureSnapshot()).ToArray(),activeTab);
        var content=JsonSerializer.SerializeToUtf8Bytes(snapshot);
        try{await WriteAtomicAsync(recoveryFolder??folder,"session.bin",ProtectedData.Protect(content,null,DataProtectionScope.CurrentUser));}
        finally{CryptographicOperations.ZeroMemory(content);}
    }
    public async Task<SessionSnapshot?> ReadSessionAsync()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        await saving.WaitAsync();
        try
        {
            var path=System.IO.Path.Combine(recoveryFolder??folder,"session.bin");
            if(!File.Exists(path))return null;
            await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var encrypted=new MemoryStream();await input.CopyToAsync(encrypted);
            var content=ProtectedData.Unprotect(encrypted.ToArray(),null,DataProtectionScope.CurrentUser);
            try{return JsonSerializer.Deserialize<SessionSnapshot>(content);}
            finally{CryptographicOperations.ZeroMemory(content);}
        }
        finally{saving.Release();}
    }
    public async Task ClearSessionAsync()
    {
        await saving.WaitAsync();
        try
        {
            var targetFolder=recoveryFolder??folder;
            if(!Directory.Exists(targetFolder))return;
            var path=System.IO.Path.Combine(targetFolder,"session.bin");
            using var publication=await AcquirePublicationLockAsync(path+".lock");
            File.Delete(path);
        }
        finally{saving.Release();}
    }
    public async Task SaveRecoveryAsync(IEnumerable<ScriptDocument> documents)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var content=JsonSerializer.SerializeToUtf8Bytes(documents.Where(d=>d.IsDirty).Select(d=>new RecoveryDocument(d.Id,d.Path,d.Text)));
        try { await WriteAtomicAsync(recoveryFolder??folder,"recovery.bin",ProtectedData.Protect(content,null,DataProtectionScope.CurrentUser)); }
        finally {CryptographicOperations.ZeroMemory(content);}
    }
    public async Task<RecoveryDocument[]> ReadRecoveryAsync()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var path=System.IO.Path.Combine(recoveryFolder??folder,"recovery.bin");
        if(!File.Exists(path))return [];
        var bytes=ProtectedData.Unprotect(await File.ReadAllBytesAsync(path),null,DataProtectionScope.CurrentUser);
        try{return JsonSerializer.Deserialize<RecoveryDocument[]>(bytes) ?? [];}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private async Task WriteAtomicAsync(string targetFolder,string name,byte[] data)
    {
        await saving.WaitAsync();
        try
        {
            Directory.CreateDirectory(targetFolder);var path=System.IO.Path.Combine(targetFolder,name);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp,data);
                // Serialize replacement across processes as Windows can reject simultaneous
                // overwrite renames with access denied, even when each temporary file is unique.
                using var publication=await AcquirePublicationLockAsync(path+".lock");
                for(var attempt=0;;attempt++)
                {
                    try{File.Move(temp,path,true);break;}
                    catch(IOException ex) when(attempt<10&&(ex.HResult&0xffff) is 32 or 33){await Task.Delay(20);}
                }
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        finally {saving.Release();}
    }
    private static async Task<FileStream> AcquirePublicationLockAsync(string path)
    {
        for(var attempt=0;;attempt++)
        {
            try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException ex) when(attempt<250&&(ex.HResult&0xffff) is 32 or 33){await Task.Delay(20);}
        }
    }
}
