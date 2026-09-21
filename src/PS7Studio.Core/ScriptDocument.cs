using System.Security.Cryptography;
using System.Text;

namespace PS7Studio.Core;

public sealed record DocumentSnapshot(Guid Id,string? Path,string Text,string SavedText,byte[]? DiskHash,int EncodingCodePage,bool Bom);

public sealed class ScriptDocument
{
    private string savedText = "";
    private byte[]? diskHash;
    private Encoding encoding = new UTF8Encoding(false, true);
    private bool bom;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? Path { get; private set; }
    public string Text { get; set; } = "";
    public bool IsDirty => Text != savedText;
    public string Title => Path is null ? "Untitled" : System.IO.Path.GetFileName(Path);
    public string EncodingName => encoding.WebName + (bom ? " BOM" : "");
    public string LineEnding => Text.Contains("\r\n", StringComparison.Ordinal) ? "CRLF" : "LF";

    public DocumentSnapshot CaptureSnapshot()=>new(Id,Path,Text,savedText,diskHash?.ToArray(),encoding.CodePage,bom);

    public static ScriptDocument RestoreSnapshot(DocumentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new()
        {
            Id=snapshot.Id,Path=snapshot.Path,Text=snapshot.Text,savedText=snapshot.SavedText,
            diskHash=snapshot.DiskHash?.ToArray(),bom=snapshot.Bom,
            encoding=snapshot.EncodingCodePage switch
            {
                65001=>new UTF8Encoding(snapshot.Bom,true),
                1200=>new UnicodeEncoding(false,snapshot.Bom,true),
                1201=>new UnicodeEncoding(true,snapshot.Bom,true),
                12000=>new UTF32Encoding(false,snapshot.Bom,true),
                12001=>new UTF32Encoding(true,snapshot.Bom,true),
                _=>throw new InvalidDataException("Unsupported document snapshot encoding.")
            }
        };
    }

    public static async Task<ScriptDocument> LoadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var (encoding, skip) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        return new() { Path = System.IO.Path.GetFullPath(path), Text = text, savedText = text,
            encoding = encoding, bom = skip > 0, diskHash = SHA256.HashData(bytes) };
    }
    private static (Encoding, int) DetectEncoding(byte[] data)
    {
        if (data.AsSpan().StartsWith(new byte[] {0xFF,0xFE,0,0})) return (new UTF32Encoding(false,true,true),4);
        if (data.AsSpan().StartsWith(new byte[] {0,0,0xFE,0xFF})) return (new UTF32Encoding(true,true,true),4);
        if (data.AsSpan().StartsWith(new byte[] {0xEF,0xBB,0xBF})) return (new UTF8Encoding(true,true),3);
        if (data.AsSpan().StartsWith(new byte[] {0xFF,0xFE})) return (new UnicodeEncoding(false,true,true),2);
        if (data.AsSpan().StartsWith(new byte[] {0xFE,0xFF})) return (new UnicodeEncoding(true,true,true),2);
        return (new UTF8Encoding(false,true),0);
    }
    public async Task<bool> HasExternalChangeAsync()
    {
        if (Path is null || diskHash is null) return false;
        if (!File.Exists(Path)) return true;
        return !SHA256.HashData(await File.ReadAllBytesAsync(Path)).AsSpan().SequenceEqual(diskHash);
    }
    public async Task SaveAsync(string? path = null, bool overwriteExternalChanges = false)
    {
        var destination = System.IO.Path.GetFullPath(path ?? Path ?? throw new InvalidOperationException("A file path is required."));
        if (!overwriteExternalChanges && string.Equals(destination, Path, StringComparison.OrdinalIgnoreCase) && await HasExternalChangeAsync())
            throw new IOException("The file changed outside PS7 Studio. Reload it or save to a different path.");
        var snapshot = Text;
        var preamble = bom ? encoding.GetPreamble() : [];
        var content = encoding.GetBytes(snapshot);
        var bytes = new byte[preamble.Length + content.Length];
        preamble.CopyTo(bytes,0); content.CopyTo(bytes,preamble.Length);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,4096,FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes); stream.Flush(true); }
            if (File.Exists(destination)) File.Move(temporary,destination,true);
            else File.Move(temporary,destination);
            Path = destination; savedText = snapshot; diskHash = SHA256.HashData(bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
