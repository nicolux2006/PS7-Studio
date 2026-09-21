using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PS7Studio.Core;

// Both public PSES transports use Content-Length framing; DAP and LSP have different envelopes.
internal sealed class PsesProtocolClient : IAsyncDisposable
{
    readonly NamedPipeClientStream stream;
    readonly bool dap;
    readonly SemaphoreSlim writer = new(1);
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    Task? reader;
    int sequence, failed, disposed;
    public event Action<string, JsonElement>? Notification;
    public event Action<Exception>? Disconnected;
    public bool IsConnected => Volatile.Read(ref failed)==0 && stream.IsConnected && !lifetime.IsCancellationRequested;
    public PsesProtocolClient(string name, bool dap)
    {
        this.dap = dap;
        stream = new NamedPipeClientStream(".", name.Replace(@"\\.\pipe\", ""), PipeDirection.InOut, PipeOptions.Asynchronous);
    }
    public async Task ConnectAsync(CancellationToken token)
    { await stream.ConnectAsync(token); reader = ReadAsync(); }
    public async Task<JsonElement> RequestAsync(string method, object arguments, CancellationToken token)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await SendAsync(dap ? new { seq=id, type="request", command=method, arguments } : (object)new {jsonrpc="2.0",id,method,@params=arguments}, token);
            return await completion.Task.WaitAsync(token);
        }
        finally {pending.TryRemove(id,out _);}
    }
    public Task NotifyAsync(string method, object arguments, CancellationToken token) => SendAsync(new {jsonrpc="2.0",method,@params=arguments},token);
    async Task SendAsync(object message, CancellationToken token)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(message);
        var header=Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        await writer.WaitAsync(token);
        // Once the frame starts, caller cancellation must not split its header/body.
        // Session cancellation closes the transport, so a partial frame is never reused.
        try {await stream.WriteAsync(header,lifetime.Token);await stream.WriteAsync(bytes,lifetime.Token);await stream.FlushAsync(lifetime.Token);}
        finally {writer.Release();}
    }
    async Task ReadAsync()
    {
        try
        {
            var one=new byte[1];
            while(!lifetime.IsCancellationRequested)
            {
                var header=new StringBuilder();
                while(true)
                {
                    await stream.ReadExactlyAsync(one,lifetime.Token);header.Append((char)one[0]);
                    if(header.Length>8192)throw new IOException("PSES response header too large.");
                    if(header.Length>=4 && header.ToString(header.Length-4,4)=="\r\n\r\n")break;
                }
                var lengthLine=header.ToString().Split("\r\n").First(x=>x.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase));
                var length=int.Parse(lengthLine.Split(':')[1]);
                if(length<0 || length>32*1024*1024)throw new IOException("PSES response too large.");
                var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,lifetime.Token);
                using var document=JsonDocument.Parse(bytes);var message=document.RootElement;
                if(dap && message.GetProperty("type").GetString()=="response")
                {
                    if(pending.TryRemove(message.GetProperty("request_seq").GetInt32(),out var completion))
                    {
                        if(!message.GetProperty("success").GetBoolean())completion.TrySetException(new InvalidOperationException(message.TryGetProperty("message",out var error)?error.GetString():"PSES request failed."));
                        else completion.TrySetResult(message.TryGetProperty("body",out var body)?body.Clone():JsonSerializer.SerializeToElement(new {}));
                    }
                }
                else if(!dap && message.TryGetProperty("id",out var id) && !message.TryGetProperty("method",out _))
                {
                    if(id.ValueKind==JsonValueKind.Number && pending.TryRemove(id.GetInt32(),out var completion))
                    {
                        if(message.TryGetProperty("error",out var error))completion.TrySetException(new InvalidOperationException(error.ToString()));
                        else completion.TrySetResult(message.GetProperty("result").Clone());
                    }
                }
                else if(!dap && message.TryGetProperty("id",out var requestId))
                {
                    // PSES may ask the editor to register capabilities or retrieve configuration.
                    object? result=message.GetProperty("method").GetString()=="workspace/configuration" ? new object[]{new {}} : null;
                    await SendAsync(new {jsonrpc="2.0",id=requestId.Clone(),result},lifetime.Token);
                }
                else
                {
                    var method=message.GetProperty(dap?"event":"method").GetString()!;
                    var payload=message.TryGetProperty(dap?"body":"params",out var body)?body.Clone():JsonSerializer.SerializeToElement(new {});
                    Notification?.Invoke(method,payload);
                }
            }
        }
        catch(Exception ex)
        {
            Interlocked.Exchange(ref failed,1);
            foreach(var item in pending)if(pending.TryRemove(item.Key,out var completion))completion.TrySetException(new IOException("PSES disconnected.",ex));
            if(!lifetime.IsCancellationRequested)Disconnected?.Invoke(ex);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        await lifetime.CancelAsync();stream.Dispose();
        if(reader is not null)try{await reader;}catch{}
        lifetime.Dispose();writer.Dispose();
    }
}
