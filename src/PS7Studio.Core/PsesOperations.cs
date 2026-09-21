using System.Text;
using System.Text.Json;

namespace PS7Studio.Core;

public sealed partial class EngineClient
{
    sealed record StudioBreakpoint(int id,string path,int line,bool enabled,string condition)
    {public string type=>"LineBreakpoint";}
    readonly List<StudioBreakpoint> breakpoints=[];
    readonly Dictionary<string,int> documentVersions=new(StringComparer.OrdinalIgnoreCase);
    int breakpointSequence;
    async Task<JsonElement> ExecuteAsync(JsonElement data,CancellationToken token)
    {
        editorExecuting=true;executionCancelled=false;State("Running");
        var source=Text(data,"sourcePath");var path=Text(data,"path");var code=Text(data,"code");
        string? snapshotVariable=null;
        try
        {
            if(source.Length==0 && path.Length==0)
            {
                await AwaitExecutionAsync(language!.RequestAsync("evaluate",new {expression=code},lifetime.Token),token);
            }
            else
            {
                await CloseDebugAsync(token);
                await EnsureDebugAsync(token,forLaunch:true);
                execution=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                string script;
                if(source.Length>0)
                {
                    var uri=source.StartsWith("untitled:",StringComparison.OrdinalIgnoreCase)?source:new Uri(source).AbsoluteUri;
                    if(documentVersions.TryGetValue(uri,out var version))
                        await language!.NotifyAsync("textDocument/didChange",new {textDocument=new {uri,version=version+1},contentChanges=new[]{new {text=code}}},token);
                    else await language!.NotifyAsync("textDocument/didOpen",new {textDocument=new {uri,languageId="powershell",version=1,text=code}},token);
                    documentVersions[uri]=version+1;
                    await language!.RequestAsync("textDocument/documentSymbol",new {textDocument=new {uri}},token);
                    // Parsing with the source identity preserves breakpoint lines and $PSScriptRoot.
                    // Calling the block preserves normal script scope even for dirty/untitled buffers.
                    var encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
                    snapshotVariable="__PS7StudioRun_"+Guid.NewGuid().ToString("N");
                    await QueryAsync("$global:"+snapshotVariable+"=[System.Management.Automation.Language.Parser]::ParseInput([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+encoded+"')),"+Quote(source)+",[ref]$null,[ref]$null).GetScriptBlock(); @{}",token);
                    script="& $global:"+snapshotVariable;
                }
                else script=path;
                await debug!.RequestAsync("launch",new {script,executeMode="Call",createTemporaryIntegratedConsole=false,cwd="",noDebug=false},token);
                foreach(var file in breakpoints.Select(b=>b.path).Distinct(StringComparer.OrdinalIgnoreCase))await ApplyBreakpointsAsync(file,token);
                if(snapshotVariable is not null)runEcho.Register(snapshotVariable,code);
                await debug.RequestAsync("configurationDone",new {},token);
                try{await execution.Task.WaitAsync(token);}
                catch(OperationCanceledException) when(!lifetime.IsCancellationRequested)
                {
                    await AbortExecutionAsync();
                    try{await execution.Task.WaitAsync(TimeSpan.FromSeconds(10),lifetime.Token);}
                    catch(TimeoutException){await DisposeAsync();}
                    catch(Exception ex) when(ex is IOException or OperationCanceledException){}
                    throw;
                }
                // A completed DAP debug session must disconnect before PSES offers its next server.
                await CloseDebugAsync(token);
            }
            return Json(new {success=!executionCancelled,cancelled=executionCancelled});
        }
        catch(OperationCanceledException) when(!executionCancelled && !lifetime.IsCancellationRequested)
        {
            await AbortExecutionAsync();
            if(execution is not null)
            {
                try{await execution.Task.WaitAsync(TimeSpan.FromSeconds(10),lifetime.Token);}
                catch(TimeoutException){await DisposeAsync();}
                catch(Exception ex) when(ex is IOException or OperationCanceledException){}
            }
            return Json(new {success=false,cancelled=true});
        }
        catch(Exception ex) when(executionCancelled && ex is InvalidOperationException or IOException or OperationCanceledException)
        {return Json(new {success=false,cancelled=true});}
        finally
        {
            execution=null;paused=false;
            if(snapshotVariable is not null && IsConnected)
            {
                try{await QueryAsync("Remove-Variable -Scope Global -Name "+Quote(snapshotVariable)+" -ErrorAction Ignore; @{}",lifetime.Token);}catch(Exception ex) when(ex is IOException or InvalidOperationException or OperationCanceledException){}
            }
            editorExecuting=false;hostBusy=false;if(IsConnected)State("Idle");
        }
    }
    async Task<JsonElement> BreakpointsAsync(JsonElement data,CancellationToken token)
    {
        var changed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(data.TryGetProperty("clear",out var clear)&&clear.GetBoolean())
        {foreach(var b in breakpoints)changed.Add(b.path);breakpoints.Clear();}
        if(data.TryGetProperty("id",out var id))
        {
            var index=breakpoints.FindIndex(b=>b.id==id.GetInt32());if(index<0)throw new InvalidOperationException("Breakpoint not found.");
            var old=breakpoints[index];changed.Add(old.path);
            switch(Text(data,"operation"))
            {case "remove":breakpoints.RemoveAt(index);break;case "enable":breakpoints[index]=old with{enabled=true};break;case "disable":breakpoints[index]=old with{enabled=false};break;case "condition":breakpoints[index]=old with{condition=Text(data,"condition")};break;default:throw new InvalidOperationException("Unknown breakpoint operation.");}
        }
        else if(Text(data,"path") is {Length:>0} path)
        {
            var line=data.GetProperty("line").GetInt32();var index=breakpoints.FindIndex(b=>b.path.Equals(path,StringComparison.OrdinalIgnoreCase)&&b.line==line);
            if(index>=0)breakpoints.RemoveAt(index);else breakpoints.Add(new(++breakpointSequence,path,line,true,Text(data,"condition")));
            changed.Add(path);
        }
        // DAP setBreakpoints is also valid before launch; it updates the existing runspace.
        foreach(var path in changed)await ApplyBreakpointsAsync(path,token);
        return Json(new {items=breakpoints.ToArray()});
    }
    async Task ApplyBreakpointsAsync(string path,CancellationToken token)
    {
        await EnsureDebugAsync(token);
        await debug!.RequestAsync("setBreakpoints",new {source=new {path},breakpoints=breakpoints.Where(b=>b.enabled&&b.path.Equals(path,StringComparison.OrdinalIgnoreCase)).Select(b=>new {line=b.line,condition=b.condition.Length==0?null:b.condition}).ToArray()},token);
    }
    async Task<JsonElement> InspectAsync(CancellationToken token)
    {
        var stack=await debug!.RequestAsync("stackTrace",new {threadId,startFrame=0,levels=100},token);
        var frames=stack.GetProperty("stackFrames").EnumerateArray().Select(f=>new {id=f.GetProperty("id").GetInt32(),function=f.GetProperty("name").GetString(),line=f.GetProperty("line").GetInt32()}).ToArray();
        if(frames.Length==0)return Json(new {frames,variables=Array.Empty<object>()});
        frameId=frames[0].id;
        var scopes=await debug.RequestAsync("scopes",new {frameId},token);
        var variables=new List<object>();
        foreach(var scope in scopes.GetProperty("scopes").EnumerateArray())
        {
            var items=await debug.RequestAsync("variables",new {variablesReference=scope.GetProperty("variablesReference").GetInt32()},token);
            variables.AddRange(items.GetProperty("variables").EnumerateArray().Select(v=>(object)new {name=v.GetProperty("name").GetString()?.TrimStart('$'),value=v.GetProperty("value").GetString()}));
        }
        return Json(new {frames,variables});
    }
    static string TerminalThemeScript(JsonElement data)
    {
        var dark=data.TryGetProperty("dark",out var value)&&value.GetBoolean();
        // Configure only input highlighting; terminal output retains its original ANSI colors.
        return "Set-PSReadLineOption -Colors @{Command=([string][char]27+'[38;2;"+(dark?"86;212;221":"0;87;163")+"m')}; @{success=$true}";
    }
    static string QueryScript(string operation,JsonElement data)
    {
        var code=Quote(Text(data,"code"));var name=Quote(Text(data,"name"));
        return operation switch
        {
            "parse"=>"$t=$null;$e=$null;[void][System.Management.Automation.Language.Parser]::ParseInput("+code+",[ref]$t,[ref]$e); @{tokens=@($t | ForEach-Object { @{start=$_.Extent.StartOffset;length=$_.Extent.EndOffset-$_.Extent.StartOffset;kind=$_.Kind.ToString();flags=$_.TokenFlags.ToString()} });errors=@($e | ForEach-Object { @{message=$_.Message;line=$_.Extent.StartLineNumber;column=$_.Extent.StartColumnNumber;start=$_.Extent.StartOffset;length=$_.Extent.EndOffset-$_.Extent.StartOffset} })}",
            "complete"=>"$c=TabExpansion2 -inputScript "+code+" -cursorColumn "+data.GetProperty("cursor").GetInt32()+"; @{start=$c.ReplacementIndex;length=$c.ReplacementLength;items=@($c.CompletionMatches | ForEach-Object { @{text=$_.CompletionText;label=$_.ListItemText;detail=$_.ToolTip} })}",
            "commands"=>"@{items=@(Get-Command | Sort-Object Name | Select-Object -First 10000 | ForEach-Object { @{name=$_.Name;module=$_.ModuleName;type=$_.CommandType.ToString()} })}",
            "commandInfo"=>"$c=Get-Command -Name "+name+" -ErrorAction Stop | Select-Object -First 1; @{name=$c.Name;syntax=$c.Definition;sets=@($c.ParameterSets | ForEach-Object { @{name=$_.Name;syntax=$_.ToString();parameters=@($_.Parameters | ForEach-Object { @{name=$_.Name;type=$_.ParameterType.Name;mandatory=$_.IsMandatory;position=$_.Position} })} })}",
            "help"=>"@{text=(Get-Help -Name "+name+" -Full | Out-String -Width 110)}",
            "variables"=>"@{items=@(Get-Variable -Scope Global | Select-Object -First 1000 | ForEach-Object { @{name=$_.Name;type=$(if($null -eq $_.Value){'null'}else{$_.Value.GetType().Name});value=[string]$_.Value} })}",
            "location"=>"@{path=$PWD.Path}",
            "sessionIdentity"=>"@{pid=$PID;runspace=[runspace]::DefaultRunspace.InstanceId.ToString();path=$PWD.Path}",
            _=>throw new InvalidOperationException("Unknown session operation: "+operation)
        };
    }
}
