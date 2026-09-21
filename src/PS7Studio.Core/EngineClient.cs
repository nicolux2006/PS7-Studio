using System.Text;
using System.Text.Json;
using PS7Studio.Protocol;
using PS7Studio.Terminal;

namespace PS7Studio.Core;

public sealed partial class EngineClient(string applicationDirectory) : IAsyncDisposable
{
    readonly CancellationTokenSource lifetime = new();
    readonly object lifecycle=new();
    readonly TerminalRunEcho runEcho=new();
    readonly SemaphoreSlim operations = new(1), terminalWriter = new(1), debugConnection = new(1);
    readonly string sessionDirectory = Path.Combine(Path.GetTempPath(),"PS7Studio",Guid.NewGuid().ToString("N"));
    PsesProtocolClient? language, debug;
    PtyProcess? terminal;
    Task? terminalReader;
    string debugPipe="";
    TaskCompletionSource? execution;
    int disposed, started, frameId, threadId=1, metadataRequests;
    bool paused, hostBusy, editorExecuting, executionCancelled;
    public event Action<WireMessage>? EventReceived;
    public event Action<string>? TerminalOutputReceived;
    public Func<string,Task>? TerminalOutputHandler {get;set;}
    public bool IsConnected => language?.IsConnected==true && terminal?.HasExited==false && !lifetime.IsCancellationRequested;
    public int? ProcessId => terminal?.ProcessId;
    public async Task StartAsync(string? startupDirectory=null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("Session already started.");
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            var identifier="PS7Studio-"+Guid.NewGuid().ToString("N");debugPipe=identifier+"-debug";
            var directory=Directory.Exists(startupDirectory)?startupDirectory!:@"C:\";
            var start=Path.Combine(applicationDirectory,"runtimes","editorservices","PowerShellEditorServices","PowerShellEditorServices.psd1");
            var executable=Path.Combine(applicationDirectory,"runtimes","powershell","pwsh.exe");
            var command="[Console]::InputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); Import-Module "+Quote(start)+"; Start-EditorServices -StartupBanner '' -HostName 'PS7 Studio' -HostProfileId PS7Studio -HostVersion 26.9.0 -SessionDetailsPath "+Quote(Path.Combine(sessionDirectory,"session.json"))+" -LogPath "+Quote(Path.Combine(sessionDirectory,"pses.log"))+" -LogLevel Warning -LanguageServicePipeName "+Quote(identifier+"-language")+" -DebugServicePipeName "+Quote(debugPipe)+" -EnableConsoleRepl";
            lock(lifecycle)
            {
                ObjectDisposedException.ThrowIf(disposed!=0,this);
                terminal=PtyProcess.Start(executable,["-NoLogo","-NoProfile","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(command))],directory);
            }
            terminalReader=ReadTerminalAsync();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(45));
            language=new PsesProtocolClient(identifier+"-language",false);
            language.Notification+=LanguageNotification;language.Disconnected+=_=>State("Disconnected");
            await language.ConnectAsync(timeout.Token);
            await language.RequestAsync("initialize",new {processId=Environment.ProcessId,rootUri=new Uri(applicationDirectory+Path.DirectorySeparatorChar).AbsoluteUri,capabilities=new {},initializationOptions=new {enableProfileLoading=false,initialWorkingDirectory=directory}},timeout.Token);
            await language.NotifyAsync("initialized",new {},timeout.Token);
            await language.NotifyAsync("workspace/didChangeConfiguration",new {settings=new {powershell=new {scriptAnalysis=new {enable=false}}}},timeout.Token);
            await EnsureDebugAsync(timeout.Token);
            var version=await QueryAsync("@{version=$PSVersionTable.PSVersion.ToString()}",timeout.Token);
            Emit(WireMessage.Create("hello","",new {protocol=2,version=version.GetProperty("version").GetString(),pid=terminal.ProcessId}));State("Idle");
        }
        catch
        {
            await DisposeAsync();
            // Disposal may have raced initialization before these transports were assigned.
            if(language is not null)await language.DisposeAsync();if(debug is not null)await debug.DisposeAsync();
            State("Disconnected");throw;
        }
    }
    async Task ReadTerminalAsync()
    {
        try
        {
            using var reader=new StreamReader(terminal!.Output,new UTF8Encoding(false),false,8192,true);
            var chars=new char[8192];string carry="";
            while(true)
            {
                var count=await reader.ReadAsync(chars,lifetime.Token);if(count==0)break;
                var text=carry+new string(chars,0,count);carry="";
                if(char.IsHighSurrogate(text[^1])){carry=text[^1].ToString();text=text[..^1];}
                if(text.Length==0)continue;
                text=runEcho.Process(text);if(text.Length==0)continue;
                foreach(var handler in TerminalOutputReceived?.GetInvocationList()??[])try{((Action<string>)handler)(text);}catch{}
                if(TerminalOutputHandler is {} consume)await consume(text).WaitAsync(lifetime.Token);
            }
        }
        catch(Exception) when(lifetime.IsCancellationRequested){}
        catch(Exception ex){Emit(WireMessage.Create("sessionError","",new {message=ex.Message}));}
        finally{execution?.TrySetException(new IOException("PowerShell terminal disconnected."));State("Disconnected");}
    }
    public Task WriteTerminalAsync(string data,CancellationToken token=default)=>WriteTerminalBytesAsync(Encoding.UTF8.GetBytes(data),token);
    public async Task WriteTerminalBytesAsync(byte[] data,CancellationToken token=default)
    {
        if(terminal is null)throw new IOException("Terminal is not started.");
        await terminalWriter.WaitAsync(token);
        try{await terminal.Input.WriteAsync(data,token);await terminal.Input.FlushAsync(token);}
        finally{terminalWriter.Release();}
    }
    public void ResizeTerminal(int columns,int rows)=>terminal?.Resize(columns,rows);
    void Emit(WireMessage message)
    {foreach(var handler in EventReceived?.GetInvocationList()??[])try{((Action<WireMessage>)handler)(message);}catch{}}
    void State(string state)=>Emit(WireMessage.Create("state","",new {state}));
    void LanguageNotification(string method,JsonElement data)
    {
        // Console.ReadKey is not cancellable. PSES asks its client to wake it with a
        // discarded character, exactly as the reference VS Code client does.
        if(method=="powerShell/sendKeyPress")_ = WakeReadKeyAsync();
        if(method=="powerShell/executionBusyStatus" && metadataRequests==0 && data.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {hostBusy=data.GetBoolean();if(!paused && !editorExecuting)State(hostBusy?"Running":"Idle");}
        if(method=="powerShell/startDebugger")_ = AttachInteractiveAsync();
    }
    async Task WakeReadKeyAsync(){try{await WriteTerminalAsync("p",lifetime.Token);}catch(Exception ex) when(ex is IOException or ObjectDisposedException or OperationCanceledException){}}
    async Task AttachInteractiveAsync()
    {
        try
        {
            if(execution is not null)return;
            await EnsureDebugAsync(lifetime.Token);
        }
        catch(Exception ex){Emit(WireMessage.Create("sessionError","",new {message=ex.Message}));}
    }
    async Task CloseDebugAsync(CancellationToken token)
    {
        await debugConnection.WaitAsync(token);
        try
        {
            var closing=debug;
            if(closing is null)return;
            try
            {
                if(closing.IsConnected)await closing.RequestAsync("disconnect",new {terminateDebuggee=false},token);
            }
            // PSES ends its DAP server after its disconnect handler has aborted the pipeline.
            // Depending on scheduling, that EOF can precede delivery of the response. Only
            // accept it for this explicitly requested teardown while the shared host lives.
            catch(IOException) when(IsConnected){}
            finally
            {
                await closing.DisposeAsync();
                if(ReferenceEquals(debug,closing))debug=null;
            }
        }
        finally{debugConnection.Release();}
    }
    async Task EnsureDebugAsync(CancellationToken token,bool forLaunch=false)
    {
        await debugConnection.WaitAsync(token);
        try
        {
            if(debug?.IsConnected==true)return;
            if(debug is not null)await debug.DisposeAsync();
            debug=new PsesProtocolClient(debugPipe,true);debug.Notification+=DebugNotification;
            await debug.ConnectAsync(token);
            await debug.RequestAsync("initialize",new {adapterID="PowerShell",clientID="PS7Studio",linesStartAt1=true,columnsStartAt1=true,pathFormat="path",supportsVariableType=true},token);
            if(!forLaunch)
            {
                // Register the public DAP stop handlers even while sitting at the terminal.
                // Otherwise a metadata-only DAP connection suppresses PSES auto-attach requests.
                await debug.RequestAsync("launch",new {createTemporaryIntegratedConsole=false},token);
                await debug.RequestAsync("configurationDone",new {},token);

            }
        }
        finally{debugConnection.Release();}
    }
    void DebugNotification(string method,JsonElement data)
    {
        if(method=="stopped")
        {paused=true;threadId=data.TryGetProperty("threadId",out var id)?id.GetInt32():1;State("DebugPaused");_ = EmitStoppedAsync();}
        else if(method=="continued"){paused=false;State("Running");}
        else if(method=="terminated"){paused=false;execution?.TrySetResult();if(execution is null)State("Idle");}
    }
    async Task EmitStoppedAsync()
    {
        try
        {
            var stack=await debug!.RequestAsync("stackTrace",new {threadId},lifetime.Token);
            var top=stack.GetProperty("stackFrames").EnumerateArray().FirstOrDefault();
            if(top.ValueKind==JsonValueKind.Undefined)return;
            frameId=top.GetProperty("id").GetInt32();
            var path=top.TryGetProperty("source",out var source)&&source.TryGetProperty("path",out var p)?p.GetString():"";
            Emit(WireMessage.Create("stopped","",new {path,line=top.GetProperty("line").GetInt32()}));
        }
        catch(Exception ex){Emit(WireMessage.Create("sessionError","",new {message=ex.Message}));}
    }
    public async Task<JsonElement> RequestAsync(string operation,object payload,CancellationToken token=default)
    {
        if(!IsConnected)throw new IOException("PowerShell session is not connected.");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);token=linked.Token;
        var data=JsonSerializer.SerializeToElement(payload);
        if(operation=="stop")
        {
            executionCancelled=true;
            if(paused)
            {
                var stoppingExecution=execution;
                await CloseDebugAsync(token);
                paused=false;stoppingExecution?.TrySetResult();
            }
            else await WriteTerminalAsync("\u0003",token);
            return Json(new {success=true});
        }
        if(operation=="resume")
        {
            var action=Text(data,"action");if(action=="Stop")return await RequestAsync("stop",new {},token);
            var command=action switch{"StepInto"=>"stepIn","StepOver"=>"next","StepOut"=>"stepOut",_=>"continue"};
            await debug!.RequestAsync(command,new {threadId},token);paused=false;State("Running");return Json(new {success=true});
        }
        if(operation=="inspect")return await InspectAsync(token);
        if(operation=="evaluate")
        {var result=await debug!.RequestAsync("evaluate",new {expression=Text(data,"code"),frameId,context="watch"},token);return Json(new {text=result.GetProperty("result").GetString()});}
        await operations.WaitAsync(token);
        try
        {
            if(hostBusy && !editorExecuting)throw new InvalidOperationException("PowerShell is busy. Stop or finish the terminal command first.");
            return operation switch
            {
                "execute"=>await ExecuteAsync(data,token),"breakpoints"=>await BreakpointsAsync(data,token),
                "configure"=>await ConfigureAsync(data,token),"terminalTheme"=>await QueryAsync(TerminalThemeScript(data),token),_=>await QueryAsync(QueryScript(operation,data),token)
            };
        }
        finally{operations.Release();}
    }
    async Task<JsonElement> ConfigureAsync(JsonElement data,CancellationToken token)
    {
        var directory=Text(data,"directory");
        var changeDirectory=directory.Length>0?"Set-Location -LiteralPath "+Quote(directory)+"; ":"";
        if(!data.TryGetProperty("profiles",out var loadProfiles)||!loadProfiles.GetBoolean())
        {
            // Both operations are private metadata and can share one runspace dispatch.
            await QueryAsync(changeDirectory+TerminalThemeScript(data),token);
            return Json(new {success=true});
        }
        if(directory.Length>0)await QueryAsync(changeDirectory+"@{}",token);
        if(data.TryGetProperty("profiles",out var profiles)&&profiles.GetBoolean())
            await AwaitExecutionAsync(language!.RequestAsync("evaluate",new {expression="foreach ($studioProfile in @($PROFILE.AllUsersAllHosts,$PROFILE.AllUsersCurrentHost,$PROFILE.CurrentUserAllHosts,$PROFILE.CurrentUserCurrentHost)) { if (Test-Path -LiteralPath $studioProfile) { . $studioProfile } }; Remove-Variable studioProfile -ErrorAction Ignore"},lifetime.Token),token);
        await QueryAsync(TerminalThemeScript(data),token);
        return Json(new {success=true});
    }
    async Task<JsonElement> QueryAsync(string script,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));token=deadline.Token;
        Interlocked.Increment(ref metadataRequests);
        try
        {
        await EnsureDebugAsync(token);
        var path=Path.Combine(sessionDirectory,Guid.NewGuid().ToString("N")+".json");
        // Public DAP evaluation executes in the current runspace. Private results never enter terminal bytes.
        var expression="& { try { $r = & { "+script+" }; $j = @{ok=$true;value=$r} | ConvertTo-Json -Depth 24 -Compress } catch { $j = @{ok=$false;error=$_.ToString()} | ConvertTo-Json -Compress }; [IO.File]::WriteAllText("+Quote(path)+",$j,[Text.UTF8Encoding]::new($false)) }";
        try
        {
            await AwaitExecutionAsync(debug!.RequestAsync("evaluate",new {expression,context="repl"},lifetime.Token),token);
            using var result=JsonDocument.Parse(await File.ReadAllTextAsync(path,token));
            if(!result.RootElement.GetProperty("ok").GetBoolean())throw new InvalidOperationException(result.RootElement.GetProperty("error").GetString());
            return result.RootElement.GetProperty("value").Clone();
        }
        finally{File.Delete(path);}
        }
        finally{Interlocked.Decrement(ref metadataRequests);}
    }
    static string Quote(string text)=>"'"+text.Replace("'","''")+"'";
    async Task<T> AwaitExecutionAsync<T>(Task<T> response,CancellationToken token)
    {
        try{return await response.WaitAsync(token);}
        catch(OperationCanceledException) when(!lifetime.IsCancellationRequested)
        {
            await AbortExecutionAsync();
            try{await response.WaitAsync(TimeSpan.FromSeconds(10),lifetime.Token);}
            catch(TimeoutException){await DisposeAsync();}
            catch(Exception ex) when(ex is IOException or InvalidOperationException or OperationCanceledException){}
            throw;
        }
    }
    async Task AbortExecutionAsync()
    {
        executionCancelled=true;
        try
        {
            if(paused && debug?.IsConnected==true)
            {
                var stoppingExecution=execution;
                await CloseDebugAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(10),lifetime.Token);
                paused=false;stoppingExecution?.TrySetResult();
            }
            else await WriteTerminalAsync("\u0003",lifetime.Token);
        }
        catch(Exception ex) when(ex is TimeoutException or IOException or InvalidOperationException)
        {await DisposeAsync();}
    }
    static string Text(JsonElement data,string name)=>data.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
    public async ValueTask DisposeAsync()
    {
        lock(lifecycle){if(disposed!=0)return;disposed=1;}
        await lifetime.CancelAsync();execution?.TrySetCanceled();
        if(language is not null)await language.DisposeAsync();if(debug is not null)await debug.DisposeAsync();
        if(terminal is not null)await terminal.DisposeAsync();
        if(terminalReader is not null)try{await terminalReader;}catch{}
        try{Directory.Delete(sessionDirectory,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        lifetime.Dispose();
    }
}
