namespace PS7Studio.Core;

/// <summary>One owner per recovery directory; OS releases ownership after a crash.</summary>
public sealed class WorkspaceLease : IDisposable
{
    private readonly FileStream lease;
    public string DirectoryPath {get;}
    private WorkspaceLease(FileStream lease,string folder){this.lease=lease;DirectoryPath=folder;}
    public static WorkspaceLease AcquireInstance(string folder)
    {
        folder=Path.GetFullPath(folder);
        var primary=TryAcquire(folder);
        if(primary is not null)return primary;
        // Stable slots retain crash recovery and are reused only after their OS lock is released.
        for(var slot=1;slot<=1024;slot++)
        {
            var candidate=TryAcquire(Path.Combine(folder,"instances","instance-"+slot));
            if(candidate is not null)return candidate;
        }
        throw new IOException("No free application instance directory is available.");
    }
    public static WorkspaceLease? TryAcquire(string folder)
    {
        Directory.CreateDirectory(folder);
        try{return new(new FileStream(Path.Combine(folder,"workspace.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None),Path.GetFullPath(folder));}
        catch(IOException ex) when((ex.HResult&0xffff) is 32 or 33){return null;}
    }
    public void Dispose()=>lease.Dispose();
}
