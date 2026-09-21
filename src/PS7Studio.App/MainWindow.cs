using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Windowing;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using PS7Studio.Core;
using PS7Studio.Protocol;
using static Microsoft.UI.Xaml.Controls.Grid;

namespace PS7Studio.App;

public sealed partial class MainWindow : Window
{
    private readonly Grid root=new(){RowSpacing=0};
    private readonly Grid workspace=new(){ColumnSpacing=1};
    private readonly Grid central=new(){RowSpacing=1};
    private readonly TabView tabs;
    private readonly Grid console=new(){Padding=new Thickness(14,8,14,10),RowSpacing=6};
    private readonly Grid terminalHost=new();
    private readonly Grid inspector=new(){Padding=new Thickness(12),RowSpacing=10};
    private readonly TextBlock status=new(){Text=L.T("Starting"),VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock position=new(){HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock sessionBadge=new(){Text="PowerShell 7.6.6",FontSize=12,Opacity=.75};
    private readonly TextBlock progress=new(){FontSize=12,Opacity=.7};
    private readonly TextBox command=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=130,MinHeight=38};
    private readonly ListView output=new(){SelectionMode=ListViewSelectionMode.Extended,IsMultiSelectCheckBoxEnabled=false};
    private OutputBuffer outputBuffer=>ActiveSession.Output;
    private ObservableCollection<OutputLine> lines=>outputBuffer.Lines;
    private ConcurrentQueue<(string stream,string text)> outputQueue=>ActiveSession.Queue;

    private readonly ComboBox panel=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly TextBox search=new();
    private readonly ListView results=new(){SelectionMode=ListViewSelectionMode.Single};
    private readonly TextBox detail=new(){AcceptsReturn=true,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=240};
    private readonly Button insert=new();
    private readonly StateStore store=new(AppState.SettingsDirectoryPath,AppState.DirectoryPath);
    private readonly WorkspaceLease workspaceLease=AppState.Workspace;
    private StudioSettings settings=new();
    private EngineClient? engine {get=>ActiveSession.Engine;set=>ActiveSession.Engine=value;}
    private string engineState {get=>ActiveSession.State;set=>ActiveSession.State=value;}
    private bool initialized,closing,dialogOpen,closingAttempt;
    private readonly SemaphoreSlim dialogs=new(1,1);
    private readonly DispatcherTimer analysisTimer=new(){Interval=TimeSpan.FromMilliseconds(550)};
    private readonly DispatcherTimer recoveryTimer=new(){Interval=TimeSpan.FromSeconds(3)};
    private readonly DispatcherTimer outputTimer=new(){Interval=TimeSpan.FromMilliseconds(60)};
    private List<string> history=>ActiveSession.History;
    private int historyIndex {get=>ActiveSession.HistoryIndex;set=>ActiveSession.HistoryIndex=value;}
    private string lastFind="";
    private List<CommandEntry> commands {get=>ActiveSession.Commands;set=>ActiveSession.Commands=value;}
    private readonly List<Button> runButtons=[];
    private Button? stopButton;
    private ScriptEditor? Editor=>(tabs.SelectedItem as TabViewItem)?.Content as ScriptEditor;
    private IEnumerable<ScriptEditor> Editors=>tabs.TabItems.OfType<TabViewItem>().Select(t=>t.Content).OfType<ScriptEditor>();

    private record CommandEntry(string Name,string Module,string Type){public override string ToString()=>Name+(Module.Length>0?"  ·  "+Module:"");}
    private record BreakpointEntry(int Id,string Path,int Line,bool Enabled){public override string ToString()=>(Enabled?"● ":"○ ")+System.IO.Path.GetFileName(Path)+":"+Line;}

    public MainWindow()
    {
        tabs=new(){IsAddTabButtonVisible=true,TabWidthMode=TabViewWidthMode.SizeToContent,VerticalContentAlignment=VerticalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        Title="PS7 Studio";
        Content=root;
        root.ActualThemeChanged+=(_,_)=>ApplySurfaceColors();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400,900));
        var icon=System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","Studio.ico");if(File.Exists(icon))AppWindow.SetIcon(icon);
        root.Loaded+=async (_,_)=>{if(initialized)return;initialized=true;await Guard(InitializeAsync);};
        AppWindow.Closing+=OnClosing;
        Closed+=(_,_)=>workspaceLease.Dispose();
        Activated+=async (_,e)=>{if(e.WindowActivationState!=WindowActivationState.Deactivated&&initialized&&!closing&&!dialogOpen)await Guard(CheckExternalAsync);};
    }
    private async Task InitializeAsync()
    {
        settings=await store.LoadSettingsAsync();L.Language=settings.Language;
        try{await TerminalView.EnsureRuntimeAvailableAsync(root.XamlRoot);}
        catch(WebViewRuntimeMissingException){closing=true;Close();return;}
        if(closing)return;
        BuildWorkspace();ApplySettings();
        var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
        var scale=root.XamlRoot.RasterizationScale;
        var width=Math.Min(area.Width-40,(int)(settings.Width*scale));var height=Math.Min(area.Height-40,(int)(settings.Height*scale));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X+Math.Max(0,(area.Width-width)/2),area.Y+Math.Max(0,(area.Height-height)/2),width,height));
        var restoredSession=false;
        var restoredActiveTab=settings.ActiveTab;
        if(settings.RestoreSession)
        {
            try
            {
                var snapshot=await store.ReadSessionAsync();
                if(snapshot is not null)
                {
                    var documents=snapshot.Documents.Select(ScriptDocument.RestoreSnapshot).ToArray();
                    foreach(var document in documents)AddDocument(document);
                    restoredActiveTab=snapshot.ActiveTab;restoredSession=true;
                }
            }
            catch(Exception ex){App.Log(ex);await Ask(L.T("Recovery"),L.T("RecoveryFailed"),L.T("OK"));}
        }
        if(settings.RestoreSession&&!restoredSession)
            foreach(var path in settings.OpenFiles.Where(File.Exists))try{await OpenPathAsync(path);}catch(Exception ex){App.Log(ex);}
        try
        {
            var recovery=restoredSession?[]:await store.ReadRecoveryAsync();
            if(recovery.Length>0&&await Ask(L.T("Recovery"),L.T("RecoveryText"),L.T("Restore"),L.T("Discard"))==ContentDialogResult.Primary)
                foreach(var item in recovery)
                {
                    var existing=Editors.FirstOrDefault(e=>e.Model.Path==item.Path&&item.Path is not null);
                    if(existing is not null){existing.Model.Text=item.Text;existing.Load(existing.Model);UpdateTab(existing);}
                    else {var doc=item.Path is not null&&File.Exists(item.Path)?await ScriptDocument.LoadAsync(item.Path):new ScriptDocument();doc.Text=item.Text;AddDocument(doc);}
                }
        }
        catch(Exception ex){App.Log(ex);await Ask(L.T("Error"),ex.Message,L.T("OK"));}
        if(closing)return;
        startupRecoveryLoaded=true;
        foreach(var path in AppState.StartupFiles)await OpenPathAsync(path);
        if(tabs.TabItems.Count==0)AddDocument(new());
        tabs.UpdateLayout();tabs.SelectedItem=tabs.TabItems[Math.Clamp(restoredActiveTab,0,tabs.TabItems.Count-1)];
        analysisTimer.Tick+=async (_,_)=>{analysisTimer.Stop();await Guard(AnalyzeAsync);};
        recoveryTimer.Tick+=async (_,_)=>{if(closingAttempt)return;try{await SaveRecoveryAsync();}catch(Exception ex){App.Log(ex);status.Text=L.T("RecoveryFailed");}};recoveryTimer.Start();
        outputTimer.Tick+=(_,_)=>DrainOutput();outputTimer.Start();
        await StartEngineAsync();
    }
    private void BuildWorkspace()
    {
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        root.Children.Add(BuildMenus());
        var toolbar=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,Padding=new Thickness(12,6,12,8)};
        toolbar.Children.Add(ActionButton("New","\uE710",()=>{AddDocument(new());return Task.CompletedTask;}));
        toolbar.Children.Add(ActionButton("Open","\uE8E5",OpenAsync));toolbar.Children.Add(ActionButton("Save","\uE74E",()=>SaveAsync(false)));
        toolbar.Children.Add(new Border{Width=1,Margin=new Thickness(8,4,8,4),Background=Brush(128,128,128)});
        var run=ActionButton("Run","\uE768",()=>RunAsync(false));runButtons.Add(run);toolbar.Children.Add(run);
        var selection=ActionButton("RunSelection","\uE8A5",()=>RunAsync(true));runButtons.Add(selection);toolbar.Children.Add(selection);
        stopButton=ActionButton("Stop","\uE71A",StopAsync);toolbar.Children.Add(stopButton);
        toolbar.Children.Add(ActionButton("StepOver","\uE7F2",()=>ResumeAsync("StepOver")));
        toolbar.Children.Add(new Border{Width=1,Margin=new Thickness(8,4,8,4),Background=Brush(128,128,128)});
        toolbar.Children.Add(ActionButton("Settings","\uE713",ShowSettingsAsync));
        SetRow(toolbar,1);root.Children.Add(toolbar);
        workspace.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});workspace.ColumnDefinitions.Add(new(){Width=new GridLength(5)});workspace.ColumnDefinitions.Add(new(){Width=new GridLength(310),MinWidth=220});
        central.RowDefinitions.Add(new(){Height=new GridLength(3,GridUnitType.Star),MinHeight=120});central.RowDefinitions.Add(new(){Height=new GridLength(5)});central.RowDefinitions.Add(new(){Height=new GridLength(2,GridUnitType.Star),MinHeight=100});
        tabs.AddTabButtonClick+=(_,_)=>AddDocument(new());
        tabs.TabCloseRequested+=async (_,e)=>await Guard(async()=>{await CloseTabAsync(e.Tab);});
        tabs.SelectionChanged+=(_,_)=>{UpdatePosition();QueueAnalysis();};
        tabs.AllowDrop=true;tabs.DragOver+=(_,e)=>{if(e.DataView.Contains(StandardDataFormats.StorageItems))e.AcceptedOperation=DataPackageOperation.Copy;};
        tabs.Drop+=async (_,e)=>await Guard(async()=>{foreach(var item in await e.DataView.GetStorageItemsAsync())if(item is Windows.Storage.StorageFile)await OpenPathAsync(item.Path);});
        central.Children.Add(tabs);
        var horizontal=new Thumb{Height=5,HorizontalAlignment=HorizontalAlignment.Stretch,Background=Brush(70,90,105)};
        horizontal.DragDelta+=(_,e)=>{var height=Math.Max(100,central.RowDefinitions[2].ActualHeight-e.VerticalChange);settings.ConsoleHeight=Math.Min(height,central.ActualHeight-150);central.RowDefinitions[2].Height=new GridLength(settings.ConsoleHeight);};SetRow(horizontal,1);central.Children.Add(horizontal);
        BuildConsole();SetRow(console,2);central.Children.Add(console);workspace.Children.Add(central);
        var vertical=new Thumb{Width=5,VerticalAlignment=VerticalAlignment.Stretch,Background=Brush(70,90,105)};
        vertical.DragDelta+=(_,e)=>{settings.InspectorWidth=Math.Clamp(workspace.ColumnDefinitions[2].ActualWidth-e.HorizontalChange,220,650);workspace.ColumnDefinitions[2].Width=new GridLength(settings.InspectorWidth);};SetColumn(vertical,1);workspace.Children.Add(vertical);
        BuildInspector();SetColumn(inspector,2);workspace.Children.Add(inspector);SetRow(workspace,2);root.Children.Add(workspace);
        var bottom=new Grid{Padding=new Thickness(14,7,14,7),ColumnSpacing=16,Background=Brush(22,65,67)};
        bottom.ColumnDefinitions.Add(new(){Width=GridLength.Auto});bottom.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});bottom.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        status.Foreground=Brush(225,245,241);bottom.Children.Add(status);var development=new TextBlock{Text=L.T("Development"),FontSize=11,Opacity=.75,Foreground=Brush(225,245,241),VerticalAlignment=VerticalAlignment.Center};localized.Add("Development",value=>development.Text=value);SetColumn(development,1);bottom.Children.Add(development);SetColumn(position,2);position.Foreground=Brush(225,245,241);bottom.Children.Add(position);SetRow(bottom,3);root.Children.Add(bottom);
        AddShortcuts();
    }
    private void BuildConsole()
    {
        for(var i=0;i<4;i++)console.RowDefinitions.Add(new(){Height=i==1?new GridLength(1,GridUnitType.Star):GridLength.Auto});
        var header=new Grid();header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var title=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};var caption=Label("Console");caption.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;title.Children.Add(caption);BuildSessionPicker(title);title.Children.Add(sessionBadge);header.Children.Add(title);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};actions.Children.Add(ActionButton("Clear","\uE74D",()=>{ActiveSession.Terminal.Clear();return Task.CompletedTask;},false));actions.Children.Add(ActionButton("Reset","\uE777",RestartAsync,false));SetColumn(actions,1);header.Children.Add(actions);console.Children.Add(header);
        output.ItemsSource=lines;output.FontFamily=new FontFamily("Cascadia Mono");output.FontSize=13;
        ScrollViewer.SetHorizontalScrollMode(output,ScrollMode.Enabled);ScrollViewer.SetHorizontalScrollBarVisibility(output,ScrollBarVisibility.Auto);
        AutomationProperties.SetName(output,L.T("Console"));
        output.ContainerContentChanging+=(_,e)=>{if(e.Item is OutputLine line&&e.ItemContainer is ListViewItem item){item.Foreground=line.Stream=="error"?Brush(238,114,123):line.Stream is "warning" or "debug" or "verbose"?Brush(225,183,91):root.ActualTheme==ElementTheme.Dark?Brush(207,217,225):Brush(32,40,50);item.Padding=new Thickness(0);item.MinHeight=18;}};
        EnsureTerminal(ActiveSession);SetRow(terminalHost,1);console.Children.Add(terminalHost);SetRow(progress,2);console.Children.Add(progress);
        command.PlaceholderText=L.T("CommandInput");command.FontFamily=new FontFamily("Cascadia Mono");AutomationProperties.SetName(command,L.T("CommandInput"));
        command.PreviewKeyDown+=async (_,e)=>
        {
            var shift=Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if(e.Key==VirtualKey.Enter&&!shift){e.Handled=true;await Guard(RunCommandAsync);}
            else if(e.Key==VirtualKey.Up&&history.Count>0){historyIndex=Math.Max(0,historyIndex-1);command.Text=history[historyIndex];command.SelectionStart=command.Text.Length;e.Handled=true;}
            else if(e.Key==VirtualKey.Down&&history.Count>0){historyIndex=Math.Min(history.Count,historyIndex+1);command.Text=historyIndex==history.Count?"":history[historyIndex];e.Handled=true;}
        };
    }
    private void BuildInspector()
    {
        inspector.RowDefinitions.Add(new(){Height=GridLength.Auto});inspector.RowDefinitions.Add(new(){Height=GridLength.Auto});inspector.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});inspector.RowDefinitions.Add(new(){Height=GridLength.Auto});inspector.RowDefinitions.Add(new(){Height=GridLength.Auto});
        foreach(var key in new[]{"Commands","Variables","Breakpoints","CallStack","Diagnostics"})panel.Items.Add(new ComboBoxItem{Content=L.T(key),Tag=key});panel.SelectedIndex=0;
        panel.SelectionChanged+=async (_,_)=>{if(!applyingLanguage)await Guard(RefreshPanelAsync);};inspector.Children.Add(panel);
        search.PlaceholderText=L.T("SearchCommands");search.TextChanged+=(_,_)=>FilterCommands();SetRow(search,1);inspector.Children.Add(search);
        results.DoubleTapped+=async (_,_)=>await Guard(InspectSelectionAsync);SetRow(results,2);inspector.Children.Add(results);
        results.PreviewKeyDown+=(_,e)=>{if(e.Key==VirtualKey.Enter&&InsertSelectedCommand())e.Handled=true;};
        detail.Text=L.T("NoSelection");SetRow(detail,3);inspector.Children.Add(detail);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};buttons.Children.Add(ActionButton("Refresh","\uE72C",RefreshPanelAsync,false));insert.Content=L.T("Insert");insert.Click+=(_,_)=>InsertSelectedCommand();buttons.Children.Add(insert);SetRow(buttons,4);inspector.Children.Add(buttons);
    }
    private static SolidColorBrush Brush(byte r,byte g,byte b)=>new(Windows.UI.Color.FromArgb(255,r,g,b));
    private Button ActionButton(string key,string glyph,Func<Task> action,bool text=true)
    {
        var stack=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};stack.Children.Add(new FontIcon{Glyph=glyph,FontSize=14});if(text)stack.Children.Add(Label(key));
        var button=new Button{Content=stack,Padding=new Thickness(10,6,10,6)};localized.Add(key,value=>{ToolTipService.SetToolTip(button,value);AutomationProperties.SetName(button,value);});button.Click+=async (_,_)=>await Guard(action);return button;
    }
    private async Task Guard(Func<Task> action,bool allowDuringClose=false)
    {
        if(closingAttempt&&!allowDuringClose)return;
        try{await action();}
        catch(Exception ex){App.Log(ex);if(!closing)await Ask(L.T("Error"),ex.Message,L.T("OK"));}
    }
    private async Task<ContentDialogResult> Ask(string title,string message,string primary,string? secondary=null)
    {
        await dialogs.WaitAsync();dialogOpen=true;
        try{return await new ContentDialog{XamlRoot=root.XamlRoot,Title=title,Content=new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap,MaxWidth=520},PrimaryButtonText=primary,SecondaryButtonText=secondary ?? "",CloseButtonText=secondary is null?"":L.T("Cancel"),DefaultButton=ContentDialogButton.Primary,RequestedTheme=root.ActualTheme}.ShowAsync();}
        finally{dialogOpen=false;dialogs.Release();}
    }
    private void ApplySettings()
    {
        root.RequestedTheme=Enum.TryParse<ElementTheme>(settings.Theme,out var theme)?theme:ElementTheme.Default;
        ApplySurfaceColors();
        foreach(var editor in Editors)editor.ApplySettings(settings);
        foreach(var session in sessions)session.Terminal.ApplySettings(settings.Font,13,root.ActualTheme==ElementTheme.Dark);
        console.Visibility=settings.ConsoleVisible?Visibility.Visible:Visibility.Collapsed;
        inspector.Visibility=settings.InspectorVisible?Visibility.Visible:Visibility.Collapsed;
        if(central.RowDefinitions.Count>2){central.RowDefinitions[2].MinHeight=settings.ConsoleVisible?100:0;central.RowDefinitions[2].Height=new GridLength(settings.ConsoleVisible?settings.ConsoleHeight:0);}
        if(workspace.ColumnDefinitions.Count>2){workspace.ColumnDefinitions[2].MinWidth=settings.InspectorVisible?220:0;workspace.ColumnDefinitions[2].Width=new GridLength(settings.InspectorVisible?settings.InspectorWidth:0);}
    }
    private void ApplySurfaceColors()
    {
        var dark=root.ActualTheme==ElementTheme.Dark;
        root.Background=dark?Brush(26,29,35):Brush(246,247,249);
        console.Background=dark?Brush(20,23,28):Brush(255,255,255);
        inspector.Background=dark?Brush(30,34,41):Brush(240,243,246);
        RefreshTerminalThemes();
    }
    private void AddDocument(ScriptDocument model)
    {
        var editor=new ScriptEditor(model,settings);var tab=new TabViewItem{Header=model.Path is null?L.T("Untitled"):model.Title,Content=editor,VerticalContentAlignment=VerticalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,IconSource=new FontIconSource{Glyph="\uE943"}};
        editor.Changed+=()=>{UpdateTab(editor);QueueAnalysis();QueueCompletion(editor);};editor.CaretChanged+=UpdatePosition;
        editor.BreakpointRequested+=async line=>await Guard(()=>ToggleBreakpointAtAsync(editor,line));
        tab.Loaded+=(_,_)=>LocalizeTabChrome(tabs);
        tabs.TabItems.Add(tab);tabs.SelectedItem=tab;UpdateTab(editor);
    }
    private void UpdateTab(ScriptEditor editor)
    {var tab=tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t=>ReferenceEquals(t.Content,editor));if(tab is not null)tab.Header=(editor.Model.Path is null?L.T("Untitled"):editor.Model.Title)+(editor.Model.IsDirty?" •":"");}
    private void UpdatePosition(){if(Editor is not null)position.Text=$"{L.T("Line")} {Editor.CurrentLine}   ·   {Editor.Model.EncodingName}   ·   {Editor.Model.LineEnding}";}
    private void QueueAnalysis(){analysisTimer.Stop();analysisTimer.Start();}
    private async Task SaveRecoveryAsync()
    {
        var documents=Editors.Select(e=>e.Model).ToArray();
        if(settings.RestoreSession)await store.SaveSessionAsync(documents,tabs.SelectedIndex);
        await store.SaveRecoveryAsync(documents);
    }
    private void DrainOutput()
    {
        foreach(var session in sessions)
        {
            var dropped=Interlocked.Exchange(ref session.DroppedOutput,0);if(dropped>0)session.Output.Append("warning",Environment.NewLine+string.Format(L.T("OutputSkipped"),dropped)+Environment.NewLine);
            var count=0;
            while(count++<32&&session.Queue.TryDequeue(out var item))
                if(item.stream=="clear")session.Output.Clear();else session.Output.Append(item.stream,item.text);
            if(ReferenceEquals(session,ActiveSession)&&lines.Count>0&&count>1)output.ScrollIntoView(lines[^1]);
        }
    }
}
