using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PS7Studio.Core;
using PS7Studio.Protocol;
using System.Text.Json;

namespace PS7Studio.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<EngineClient,bool> terminalThemes=new();
    private readonly HashSet<EngineClient> terminalThemeUpdates=[];
    private async void RefreshTerminalThemes()
    {
        if(closing)return;
        var refreshAgain=false;
        foreach(var stale in terminalThemes.Keys.Where(client=>!sessions.Any(session=>ReferenceEquals(session.Engine,client))).ToArray())terminalThemes.Remove(stale);
        foreach(var owner in sessions.ToArray())
        {
            var client=owner.Engine;var dark=root.ActualTheme==ElementTheme.Dark;
            if(client is null||!client.IsConnected||owner.State!="Idle"||terminalThemeUpdates.Contains(client)||terminalThemes.TryGetValue(client,out var applied)&&applied==dark)continue;
            terminalThemeUpdates.Add(client);
            try
            {
                await client.RequestAsync("terminalTheme",new {dark});
                if(!closing&&sessions.Contains(owner)&&ReferenceEquals(owner.Engine,client))
                {
                    terminalThemes[client]=dark;
                    refreshAgain|=dark!=(root.ActualTheme==ElementTheme.Dark);
                }
            }
            catch(Exception ex) when(ex is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException){}
            finally{terminalThemeUpdates.Remove(client);}
        }
        if(refreshAgain)RefreshTerminalThemes();
    }
    private async Task StartEngineAsync(StudioSession? session=null)
    {
        var owner=session ?? ActiveSession;
        owner.State="Starting";if(ReferenceEquals(owner,ActiveSession))status.Text=L.T(owner.State);
        var candidate=new EngineClient(AppContext.BaseDirectory);owner.Engine=candidate;
        var startupConfigured=false;
        owner.Terminal.InputReceived=text=>candidate.WriteTerminalAsync(text);
        owner.Terminal.BinaryInputReceived=bytes=>candidate.WriteTerminalBytesAsync(bytes);
        owner.Terminal.ResizeRequested=(columns,rows)=>candidate.ResizeTerminal(columns,rows);
        candidate.TerminalOutputHandler=owner.Terminal.WriteAsync;
        candidate.EventReceived+=message=>
        {
            if(!ReferenceEquals(owner.Engine,candidate))return;
            if(message.Kind=="clear"){owner.Queue.Enqueue(("clear",""));return;}
            if(message.Kind=="output")
            {
                if(owner.Queue.Count<2048)owner.Queue.Enqueue((message.Data.GetProperty("stream").GetString() ?? "output",message.Data.GetProperty("text").GetString() ?? ""));
                else Interlocked.Increment(ref owner.DroppedOutput);
                return;
            }
            DispatcherQueue.TryEnqueue(async()=>await Guard(async()=>
            {
                if(!ReferenceEquals(owner.Engine,candidate))return;
                if(message.Kind=="state")
                {
                    if(!startupConfigured&&message.Data.GetProperty("state").GetString()=="Idle")return;
                    owner.State=message.Data.GetProperty("state").GetString() ?? "Faulted";
                    if(owner.State=="Idle")RefreshTerminalThemes();
                }
                if(message.Kind=="hello")owner.Version="PowerShell "+message.Data.GetProperty("version").GetString();
                if(message.Kind is "prompt" or "stopped")sessionPicker.SelectedItem=owner;
                if(!ReferenceEquals(ActiveSession,owner))return;
                switch(message.Kind)
                {
                    case "sessionError":owner.State="Faulted";status.Text=message.Data.GetProperty("message").GetString();foreach(var button in runButtons)button.IsEnabled=false;break;
                    case "hello":sessionBadge.Text="PowerShell "+message.Data.GetProperty("version").GetString();break;

                    case "state":
                        engineState=message.Data.GetProperty("state").GetString() ?? "Faulted";status.Text=L.T(engineState);foreach(var button in runButtons)button.IsEnabled=engineState is "Idle" or "DebugPaused";if(stopButton is not null)stopButton.IsEnabled=engineState is "Running" or "AwaitingInput" or "DebugPaused";if(engineState!="DebugPaused")foreach(var editor in Editors)editor.SetExecutionLine(0);if(engineState=="Idle")QueueAnalysis();break;
                    case "prompt":await ShowPromptAsync(candidate,message.Data);break;
                    case "progress":progress.Text=message.Data.GetProperty("completed").GetBoolean()?"":message.Data.GetProperty("activity").GetString()+" · "+message.Data.GetProperty("status").GetString()+"  "+message.Data.GetProperty("percent").GetInt32()+"%";break;
                    case "stopped":
                        var path=message.Data.GetProperty("path").GetString();
                        var sourceTab=tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t=>t.Content is ScriptEditor e&&string.Equals(DocumentSource(e),path,StringComparison.OrdinalIgnoreCase));
                        if(sourceTab is not null)tabs.SelectedItem=sourceTab;
                        else if(!string.IsNullOrWhiteSpace(path)&&File.Exists(path))await OpenPathAsync(path);
                        var line=message.Data.GetProperty("line").GetInt32();Editor?.SelectLine(line);Editor?.SetExecutionLine(line);panel.SelectedIndex=3;await RefreshPanelAsync();break;
                }
            },true));
        };
        // WebView2 and PowerShell startup are independent. Terminal.WriteAsync waits
        // for renderer readiness, preserving output and its existing backpressure.
        var terminalInitialization=owner.Terminal.InitializeAsync();
        var engineInitialization=candidate.StartAsync(settings.StartupDirectory);
        try
        {
            await terminalInitialization;
            if(!closing&&sessions.Contains(owner)&&ReferenceEquals(owner.Engine,candidate))
                owner.Terminal.ApplySettings(settings.Font,13,root.ActualTheme==ElementTheme.Dark);
            await engineInitialization;
        }
        catch
        {
            await candidate.DisposeAsync();
            try{await engineInitialization;}catch{}
            if(closing||!sessions.Contains(owner)||!ReferenceEquals(owner.Engine,candidate))return;
            owner.State="Faulted";if(ReferenceEquals(owner,ActiveSession))status.Text=L.T("Faulted");throw;
        }
        if(closing||!sessions.Contains(owner)||!ReferenceEquals(owner.Engine,candidate))return;
        candidate.ResizeTerminal(owner.Terminal.Columns,owner.Terminal.Rows);
        // The initial state may be queued behind this continuation; initialization requests remain serialized by the worker.
        var configuredDark=root.ActualTheme==ElementTheme.Dark;
        await candidate.RequestAsync("configure",new {directory=Directory.Exists(settings.StartupDirectory)?settings.StartupDirectory:"",profiles=settings.LoadProfiles,dark=configuredDark});
        if(closing||!sessions.Contains(owner)||!ReferenceEquals(owner.Engine,candidate))return;
        terminalThemes[candidate]=configuredDark;startupConfigured=true;owner.State="Idle";
        if(ReferenceEquals(owner,ActiveSession)){status.Text=L.T("Idle");foreach(var button in runButtons)button.IsEnabled=true;}
        RefreshTerminalThemes();
        var data=await candidate.RequestAsync("commands",new {});owner.Commands=data.GetProperty("items").EnumerateArray().Select(x=>new CommandEntry(x.GetProperty("name").GetString()!,x.GetProperty("module").GetString() ?? "",x.GetProperty("type").GetString() ?? "")).ToList();if(ReferenceEquals(ActiveSession,owner))FilterCommands();
    }
    private void EnsureIdle(){if(engine is null||engineState!="Idle")throw new InvalidOperationException(L.T("EngineBusy"));}
    private int ConsoleColumns()
    {
        var sample=new TextBlock{Text=new string('M',100),FontFamily=output.FontFamily,FontSize=output.FontSize};
        sample.Measure(new Windows.Foundation.Size(double.PositiveInfinity,double.PositiveInfinity));
        var cell=sample.DesiredSize.Width/100;
        return cell>0&&output.ActualWidth>0?Math.Clamp((int)((output.ActualWidth-32)/cell),20,500):120;
    }
    private async Task ExecuteCodeAsync(string code)
    {
        EnsureIdle();await engine!.RequestAsync("execute",new {code,columns=ActiveSession.Terminal.Columns});
    }
    private async Task RunCommandAsync()
    {
        var code=command.Text;if(string.IsNullOrWhiteSpace(code))return;EnsureIdle();history.Add(code);if(history.Count>200)history.RemoveAt(0);historyIndex=history.Count;command.Text="";await ExecuteCodeAsync(code);
    }
    private async Task RunAsync(bool selection)
    {
        if(engineState=="DebugPaused"){await ResumeAsync("Continue");return;}
        var editor=Editor;if(editor is null)return;editor.SynchronizeText();EnsureIdle();var owner=ActiveSession;
        if(selection){await ExecuteCodeAsync(editor.SelectionOrLine());return;}
        if(!sessions.Contains(owner)||owner.State!="Idle"||owner.Engine is null)throw new InvalidOperationException(L.T("EngineBusy"));
        await owner.Engine.RequestAsync("execute",new {code=editor.Model.Text,sourcePath=DocumentSource(editor),documentId=editor.Model.Id.ToString(),columns=owner.Terminal.Columns});
    }
    private async Task StopAsync(){if(engine is not null)await engine.RequestAsync("stop",new {});}
    private async Task ResumeAsync(string action){if(engine is not null&&engineState=="DebugPaused")await engine.RequestAsync("resume",new {action});}
    private async Task RestartAsync()
    {
        var owner=ActiveSession;
        if(await Ask(L.T("Reset"),L.T("ResetWarning"),L.T("Reset"),L.T("Cancel"))!=ContentDialogResult.Primary)return;
        if(!sessions.Contains(owner))return;
        var previous=owner.Engine;if(previous is not null)await previous.DisposeAsync();
        if(closing||!sessions.Contains(owner)||!ReferenceEquals(owner.Engine,previous))return;
        await owner.Terminal.DisposeAsync();terminalHost.Children.Remove(owner.Terminal);
        owner.Terminal=new TerminalView();EnsureTerminal(owner);await StartEngineAsync(owner);
    }
    private async Task ToggleBreakpointAsync()
    {
        if(Editor is not null)await ToggleBreakpointAtAsync(Editor,Editor.CurrentLine);
    }
    private async Task ToggleBreakpointAtAsync(ScriptEditor editor,int line)
    {
        EnsureIdle();var owner=ActiveSession;var client=owner.Engine;
        if(client is null||owner.State!="Idle"||!ReferenceEquals(client,owner.Engine))return;
        var result=await client.RequestAsync("breakpoints",new {path=DocumentSource(editor),line});
        if(!ReferenceEquals(owner,ActiveSession))return;
        panel.SelectedIndex=2;ShowBreakpoints(result);
    }
    private static string DocumentSource(ScriptEditor editor)=>editor.Model.Path??"untitled:"+editor.Model.Id.ToString("N")+".ps1";
    private async Task AnalyzeAsync()
    {
        var editor=Editor;if(engine is null||engineState!="Idle"||editor is null)return;
        var owner=ActiveSession;
        var text=editor.Model.Text;var result=await engine.RequestAsync("parse",new {code=text});
        if(editor.Model.Text!=text||!ReferenceEquals(owner,ActiveSession)||!ReferenceEquals(editor,Editor))return;
        editor.Highlight(result.GetProperty("tokens"),root.ActualTheme==ElementTheme.Dark);
        if((panel.SelectedItem as ComboBoxItem)?.Tag as string=="Diagnostics")results.ItemsSource=result.GetProperty("errors").EnumerateArray().Select(e=>$"{e.GetProperty("line").GetInt32()}:{e.GetProperty("column").GetInt32()}  {e.GetProperty("message").GetString()}").ToArray();
    }
    private Task CompleteAsync()=>CompleteAsync(false);
    private int completionGeneration;
    private async void QueueCompletion(ScriptEditor editor)
    {
        var generation=++completionGeneration;
        editor.DismissCompletion();
        if(editor.AcceptingCompletion)return;
        await Task.Delay(250);
        if(generation!=completionGeneration||closing||dialogOpen||!ReferenceEquals(editor,Editor)||editor.Box.FocusState==FocusState.Unfocused)return;
        var cursor=editor.SourceCursor;var text=editor.Model.Text;
        if(cursor==0||cursor>text.Length||!(char.IsLetterOrDigit(text[cursor-1])||text[cursor-1] is '-' or '$' or '_' or ':' or '\\' or '.'))return;
        try{await CompleteAsync(true);}catch(Exception ex){App.Log(ex);}
    }
    private async Task CompleteAsync(bool automatic)
    {
        var editor=Editor;if(editor is null)return;
        if(automatic&&(engine is null||engineState!="Idle"))return;
        EnsureIdle();
        var owner=ActiveSession;
        var client=engine!;
        var raw=editor.Model.Text;var cursor=editor.SourceCursor;
        var result=await client.RequestAsync("complete",new {code=raw,cursor});
        bool StillCurrent()=>ReferenceEquals(owner,ActiveSession)&&ReferenceEquals(client,owner.Engine)&&ReferenceEquals(editor,Editor)&&editor.Model.Text==raw&&editor.SourceCursor==cursor;
        if(!StillCurrent())return;
        if(automatic&&(editor.Box.FocusState==FocusState.Unfocused||dialogOpen))return;
        if(automatic)
        {
            var start=result.GetProperty("start").GetInt32();var length=result.GetProperty("length").GetInt32();
            var existing=raw.Substring(start,length);
            var matches=result.GetProperty("items");
            if(matches.GetArrayLength()==1&&string.Equals(matches[0].GetProperty("text").GetString(),existing,StringComparison.OrdinalIgnoreCase))return;
        }
        editor.ShowCompletions(result,StillCurrent);
    }
    private bool InsertSelectedCommand()
    {
        if(closing||closingAttempt)return false;
        if(results.SelectedItem is not CommandEntry entry||Editor is not {} editor)return false;
        editor.Insert(entry.Name+" ");editor.Box.Focus(FocusState.Programmatic);return true;
    }
    private void FilterCommands()
    {
        if((panel.SelectedItem as ComboBoxItem)?.Tag as string!="Commands")return;
        results.ItemsSource=commands.Where(c=>c.Name.Contains(search.Text,StringComparison.OrdinalIgnoreCase)||c.Module.Contains(search.Text,StringComparison.OrdinalIgnoreCase)).Take(1000).ToArray();
    }
    private void ShowBreakpoints(JsonElement data)
    {
        var items=data.GetProperty("items").EnumerateArray().ToArray();
        results.ItemsSource=items.Select(x=>new BreakpointEntry(x.GetProperty("id").GetInt32(),x.GetProperty("path").GetString() ?? "",x.GetProperty("line").GetInt32(),x.GetProperty("enabled").GetBoolean())).ToArray();
        foreach(var editor in Editors)editor.SetBreakpoints(items.Where(x=>x.GetProperty("enabled").GetBoolean()&&string.Equals(x.GetProperty("path").GetString(),DocumentSource(editor),StringComparison.OrdinalIgnoreCase)).Select(x=>x.GetProperty("line").GetInt32()));
    }
    private async Task RefreshPanelAsync()
    {
        var key=(panel.SelectedItem as ComboBoxItem)?.Tag as string;search.Visibility=key=="Commands"?Visibility.Visible:Visibility.Collapsed;insert.Visibility=key=="Commands"?Visibility.Visible:Visibility.Collapsed;
        var owner=ActiveSession;var client=owner.Engine;if(client is null)return;
        bool StillCurrent()=>ReferenceEquals(owner,ActiveSession)&&ReferenceEquals(client,owner.Engine)&&(panel.SelectedItem as ComboBoxItem)?.Tag as string==key;
        switch(key)
        {
            case "Commands":
                if(engineState=="Idle")
                {
                    var data=await client.RequestAsync("commands",new {});
                    owner.Commands=data.GetProperty("items").EnumerateArray().Select(x=>new CommandEntry(x.GetProperty("name").GetString()!,x.GetProperty("module").GetString() ?? "",x.GetProperty("type").GetString() ?? "")).ToList();
                }
                if(StillCurrent())FilterCommands();break;
            case "Diagnostics":await AnalyzeAsync();break;
            case "Breakpoints":if(owner.State=="Idle"){var data=await client.RequestAsync("breakpoints",new {});if(StillCurrent())ShowBreakpoints(data);}break;
            case "Variables":case "CallStack":
                if(engineState=="DebugPaused")
                {
                    var data=await client.RequestAsync("inspect",new {});if(!StillCurrent())return;
                    results.ItemsSource=key=="CallStack"?data.GetProperty("frames").EnumerateArray().Select(x=>$"{x.GetProperty("function").GetString()}  ·  {x.GetProperty("line").GetInt32()}").ToArray():data.GetProperty("variables").EnumerateArray().Select(x=>$"${x.GetProperty("name").GetString()} = {x.GetProperty("value").GetString()}").ToArray();
                }
                else if(engineState=="Idle"&&key=="Variables")
                {var data=await client.RequestAsync("variables",new {});if(!StillCurrent())return;results.ItemsSource=data.GetProperty("items").EnumerateArray().Select(x=>$"${x.GetProperty("name").GetString()} = {x.GetProperty("value").GetString()}").ToArray();}
                else results.ItemsSource=Array.Empty<string>();
                break;
        }
    }
    private async Task InspectSelectionAsync()
    {
        if(results.SelectedItem is not CommandEntry entry)return;EnsureIdle();var owner=ActiveSession;var data=await engine!.RequestAsync("commandInfo",new {name=entry.Name});
        if(!ReferenceEquals(owner,ActiveSession)||!ReferenceEquals(entry,results.SelectedItem))return;
        detail.Text=entry.Name+"\n"+entry.Type+" · "+entry.Module+"\n\n"+string.Join("\n\n",data.GetProperty("sets").EnumerateArray().Select(x=>x.GetProperty("syntax").GetString()));
    }
    private async Task EvaluateAsync()
    {
        if(engineState!="DebugPaused")return;var owner=ActiveSession;var client=owner.Engine;var text=await InputAsync(L.T("Expression"));if(text is null||client is null||owner.State!="DebugPaused"||!ReferenceEquals(client,owner.Engine))return;var result=await client.RequestAsync("evaluate",new {code=text});owner.Queue.Enqueue(("output",result.GetProperty("text").GetString() ?? ""));
    }
    private async Task ShowPromptAsync(EngineClient owner,JsonElement data)
    {
        var stack=new StackPanel{Spacing=12,MinWidth=360};stack.Children.Add(new TextBlock{Text=data.GetProperty("message").GetString(),TextWrapping=TextWrapping.Wrap,MaxWidth=520});
        var secret=data.GetProperty("secret").GetBoolean();var text=new TextBox();var password=new PasswordBox();var choices=new ComboBox{HorizontalAlignment=HorizontalAlignment.Stretch};
        var hasChoices=data.TryGetProperty("choices",out var values)&&values.ValueKind==JsonValueKind.Array;
        if(hasChoices){foreach(var choice in values.EnumerateArray())choices.Items.Add(choice.GetString());choices.SelectedIndex=Math.Max(0,data.GetProperty("defaultChoice").GetInt32());stack.Children.Add(choices);}else if(secret)stack.Children.Add(password);else stack.Children.Add(text);
        await dialogs.WaitAsync();dialogOpen=true;
        try
        {
            var result=await new ContentDialog{XamlRoot=root.XamlRoot,Title=data.GetProperty("caption").GetString(),Content=stack,PrimaryButtonText=L.T("OK"),CloseButtonText=L.T("Cancel"),DefaultButton=ContentDialogButton.Primary}.ShowAsync();
            await owner.RequestAsync("promptReply",new {promptId=data.GetProperty("promptId").GetString(),value=hasChoices?choices.SelectedIndex.ToString():secret?password.Password:text.Text,cancel=result!=ContentDialogResult.Primary});password.Password="";text.Text="";
        }
        finally{dialogOpen=false;dialogs.Release();}
    }
}
