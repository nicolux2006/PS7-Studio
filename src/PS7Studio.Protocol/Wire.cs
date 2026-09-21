using System.Buffers.Binary;
using System.Text.Json;

namespace PS7Studio.Protocol;

public sealed record WireMessage(string Kind, string Id, JsonElement Data)
{
    public static WireMessage Create(string kind,string id,object data) => new(kind,id,JsonSerializer.SerializeToElement(data));
}
public sealed class Wire(Stream stream) : IDisposable
{
    private readonly SemaphoreSlim writing = new(1,1);
    public const int MaxFrame = 16 * 1024 * 1024;
    public async Task SendAsync(WireMessage message,CancellationToken token = default)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        if(data.Length > MaxFrame) throw new IOException("Message exceeds protocol limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,data.Length);
        await writing.WaitAsync(token);
        try { await stream.WriteAsync(header,token); await stream.WriteAsync(data,token); await stream.FlushAsync(token); }
        finally { writing.Release(); }
    }
    public async Task<WireMessage> ReadAsync(CancellationToken token = default)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header,token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length < 1 || length > MaxFrame) throw new IOException("Invalid protocol frame.");
        var data = new byte[length]; await stream.ReadExactlyAsync(data,token);
        return JsonSerializer.Deserialize<WireMessage>(data) ?? throw new IOException("Invalid protocol message.");
    }
    public void Dispose() => stream.Dispose();
}
