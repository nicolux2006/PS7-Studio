using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PS7Studio.Core;
using System.Collections.Concurrent;

namespace PS7Studio.App;

public sealed partial class MainWindow
{
    private sealed class StudioSession(string name)
    {
        public string Name {get;}=name;
        public EngineClient? Engine;
        public TerminalView Terminal {get;set;}=new();
        public string State="Starting";
        public string Version="PowerShell 7.6.6";
        public string Input="";
        public OutputBuffer Output {get;}=new();
        public ConcurrentQueue<(string stream,string text)> Queue {get;}=new();
        public int DroppedOutput;
        public List<string> History {get;}=[];
        public int HistoryIndex;
        public List<CommandEntry> Commands=[];
        public override string ToString()=>Name;
    }
    private readonly StudioSession initialSession=new("PowerShell 1");
    private readonly List<StudioSession> sessions=[];
    private StudioSession? selectedSession;
    private StudioSession ActiveSession=>selectedSession ?? initialSession;
    private readonly ComboBox sessionPicker=new(){MinWidth=140,MaxWidth=210};
    private int sessionNumber=1;
    private void BuildSessionPicker(StackPanel header)
    {
        sessions.Add(initialSession);sessionPicker.Items.Add(initialSession);sessionPicker.SelectedIndex=0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sessionPicker,L.T("Session"));
        sessionPicker.SelectionChanged+=(_,_)=>{if(sessionPicker.SelectedItem is StudioSession session)ActivateSession(session);};
        header.Children.Add(sessionPicker);
        header.Children.Add(ActionButton("NewSession","\uE710",NewSessionAsync,false));
        header.Children.Add(ActionButton("CloseSession","\uE711",CloseSessionAsync,false));
    }
    private void ActivateSession(StudioSession session)
    {
        if(ReferenceEquals(ActiveSession,session))return;
        ActiveSession.Input=command.Text;selectedSession=session;
        EnsureTerminal(session);
        command.Text=session.Input;output.ItemsSource=lines;sessionBadge.Text=session.Version;status.Text=L.T(session.State);
        foreach(var editor in Editors){editor.SetBreakpoints([]);editor.SetExecutionLine(0);}
        foreach(var button in runButtons)button.IsEnabled=engineState is "Idle" or "DebugPaused";
        if(stopButton is not null)stopButton.IsEnabled=engineState is "Running" or "AwaitingInput" or "DebugPaused";
        progress.Text="";detail.Text=L.T("NoSelection");results.ItemsSource=Array.Empty<string>();FilterCommands();QueueAnalysis();
    }
    private void EnsureTerminal(StudioSession session)
    {
        if(!terminalHost.Children.Contains(session.Terminal))terminalHost.Children.Add(session.Terminal);
        foreach(var item in sessions)item.Terminal.Visibility=ReferenceEquals(item,ActiveSession)?Visibility.Visible:Visibility.Collapsed;
    }
    private async Task NewSessionAsync()
    {
        var session=new StudioSession("PowerShell "+ ++sessionNumber);sessions.Add(session);sessionPicker.Items.Add(session);sessionPicker.SelectedItem=session;
        await StartEngineAsync();
    }
    private async Task CloseSessionAsync()
    {
        var session=ActiveSession;
        if(session.State is "Running" or "AwaitingInput" or "DebugPaused")
            if(await Ask(L.T("CloseSession"),L.T("ResetWarning"),L.T("CloseSession"),L.T("Cancel"))!=ContentDialogResult.Primary)return;
        if(session.Engine is not null)await session.Engine.DisposeAsync();
        await session.Terminal.DisposeAsync();terminalHost.Children.Remove(session.Terminal);
        sessions.Remove(session);sessionPicker.Items.Remove(session);
        if(sessions.Count==0)await NewSessionAsync();else sessionPicker.SelectedItem=sessions[0];
    }
}
