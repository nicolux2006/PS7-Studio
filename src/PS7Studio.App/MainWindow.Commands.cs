using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.UI.Text;
using PS7Studio.Core;

namespace PS7Studio.App;

public sealed partial class MainWindow
{
    private MenuBar BuildMenus()
    {
        var bar=new MenuBar();
        MenuBarItem Menu(string key){var item=new MenuBarItem();localized.Add(key,value=>item.Title=value);bar.Items.Add(item);return item;}
        void Item(MenuBarItem parent,string key,Func<Task> action,string? shortcut=null)
        {var item=new MenuFlyoutItem{KeyboardAcceleratorTextOverride=shortcut ?? ""};localized.Add(key,value=>item.Text=value);item.Click+=async (_,_)=>await Guard(action);parent.Items.Add(item);}
        Func<Task> Sync(Action action)=>()=>{action();return Task.CompletedTask;};
        var file=Menu("File");Item(file,"New",Sync(()=>AddDocument(new())),"Ctrl+N");Item(file,"Open",OpenAsync,"Ctrl+O");Item(file,"Save",()=>SaveAsync(false),"Ctrl+S");Item(file,"SaveAs",()=>SaveAsync(true),"Ctrl+Shift+S");Item(file,"SaveAll",SaveAllAsync);
        var recent=new MenuFlyoutSubItem();localized.Add("Recent",value=>recent.Text=value);foreach(var path in settings.RecentFiles.Take(15)){var entry=new MenuFlyoutItem{Text=path};entry.Click+=async(_,_)=>await Guard(()=>OpenPathAsync(path));recent.Items.Add(entry);}file.Items.Add(recent);
        Item(file,"Close",async()=>{if(tabs.SelectedItem is TabViewItem tab)await CloseTabAsync(tab);});Item(file,"CloseAll",CloseAllAsync);Item(file,"Exit",CloseApplicationAsync);
        var edit=Menu("Edit");Item(edit,"Undo",Sync(()=>Editor?.Undo()),"Ctrl+Z");Item(edit,"Redo",Sync(()=>Editor?.Redo()),"Ctrl+Y");Item(edit,"Cut",Sync(()=>Editor?.Box.Document.Selection.Cut()));Item(edit,"Copy",Sync(()=>Editor?.Box.Document.Selection.Copy()));Item(edit,"Paste",Sync(()=>Editor?.Box.Document.Selection.Paste(0)));Item(edit,"SelectAll",Sync(()=>Editor?.Box.Document.Selection.SetRange(0,int.MaxValue)));
        Item(edit,"Find",()=>FindAsync(false),"Ctrl+F");Item(edit,"Replace",()=>FindAsync(true),"Ctrl+H");Item(edit,"Next",Sync(()=>Editor?.Find(lastFind)));Item(edit,"Previous",Sync(()=>Editor?.Find(lastFind,true)));Item(edit,"GoTo",GoToAsync,"Ctrl+G");
        foreach(var pair in new[]{("Comment","comment"),("Uncomment","uncomment"),("Indent","indent"),("Outdent","outdent")})Item(edit,pair.Item1,Sync(()=>Editor?.TransformLines(pair.Item2)));
        var view=Menu("View");Item(view,"Console",Sync(()=>{settings.ConsoleVisible=!settings.ConsoleVisible;ApplySettings();}));Item(view,"Inspector",Sync(()=>{settings.InspectorVisible=!settings.InspectorVisible;ApplySettings();}));
        foreach(var key in new[]{"Commands","Variables","Breakpoints","CallStack","Diagnostics"})Item(view,key,Sync(()=>{settings.InspectorVisible=true;ApplySettings();panel.SelectedItem=panel.Items.OfType<ComboBoxItem>().First(x=>(string)x.Tag==key);}));
        Item(view,"Wrap",Sync(()=>{settings.WordWrap=!settings.WordWrap;ApplySettings();}));Item(view,"ZoomIn",Sync(()=>Zoom(1)));Item(view,"ZoomOut",Sync(()=>Zoom(-1)));Item(view,"Fullscreen",Sync(()=>AppWindow.SetPresenter(AppWindow.Presenter.Kind==AppWindowPresenterKind.FullScreen?AppWindowPresenterKind.Overlapped:AppWindowPresenterKind.FullScreen)));
        var debugMenu=Menu("Debug");Item(debugMenu,"Run",()=>RunAsync(false),"F5");Item(debugMenu,"RunSelection",()=>RunAsync(true),"F8");Item(debugMenu,"Stop",StopAsync,"Shift+F5");Item(debugMenu,"ToggleBreakpoint",ToggleBreakpointAsync,"F9");Item(debugMenu,"ClearBreakpoints",ClearBreakpointsAsync);Item(debugMenu,"Continue",()=>ResumeAsync("Continue"));Item(debugMenu,"StepInto",()=>ResumeAsync("StepInto"),"F11");Item(debugMenu,"StepOver",()=>ResumeAsync("StepOver"),"F10");Item(debugMenu,"StepOut",()=>ResumeAsync("StepOut"),"Shift+F11");Item(debugMenu,"Evaluate",EvaluateAsync);
        var tools=Menu("Tools");Item(tools,"Snippets",SnippetsAsync);Item(tools,"Modules",async()=>await ExecuteCodeAsync("Get-Module -ListAvailable | Sort-Object Name | Select-Object Name,Version,ModuleType"));Item(tools,"Reset",RestartAsync);Item(tools,"Settings",ShowSettingsAsync);
        var help=Menu("Help");Item(help,"Shortcuts",async()=>{await Ask(L.T("Shortcuts"),"Ctrl+N / O / S / Shift+S\nCtrl+F / H / G\nCtrl+Space — "+L.T("Completion")+"\nF5 — "+L.T("Run")+"\nF8 — "+L.T("RunSelection")+"\nShift+F5 — "+L.T("Stop")+"\nF9 — "+L.T("ToggleBreakpoint")+"\nF10 / F11 / Shift+F11",L.T("OK"));});Item(help,"About",ShowAboutAsync);
        Item(view,"ToggleFold",Sync(()=>Editor?.ToggleFold()));Item(view,"ExpandAll",Sync(()=>Editor?.ExpandAll()));
        Item(view,"Whitespace",Sync(()=>{settings.ShowWhitespace=!settings.ShowWhitespace;ApplySettings();}));
        Item(view,"ResetLayout",Sync(()=>{settings.ConsoleVisible=true;settings.InspectorVisible=true;settings.ConsoleHeight=260;settings.InspectorWidth=310;ApplySettings();}));
        Item(tools,"BuildCommand",BuildCommandAsync);Item(help,"Help",ShowCommandHelpAsync);
        Item(tools,"NewSession",NewSessionAsync);Item(tools,"CloseSession",CloseSessionAsync);
        foreach(var operation in new[]{("RemoveBreakpoint","remove"),("EnableBreakpoint","enable"),("DisableBreakpoint","disable")})
            Item(debugMenu,operation.Item1,()=>ManageSelectedBreakpointAsync(operation.Item2));
        Item(debugMenu,"ConditionalBreakpoint",SetConditionalBreakpointAsync);
        Item(edit,"CopyOutput",Sync(()=>ActiveSession.Terminal.CopySelection()));
        return bar;
    }
    private void AddShortcuts()
    {
        void Add(VirtualKey key,VirtualKeyModifiers modifiers,Func<Task> action){var accelerator=new KeyboardAccelerator{Key=key,Modifiers=modifiers};accelerator.Invoked+=async(_,e)=>{if(ActiveSession.Terminal.HasInputFocus)return;e.Handled=true;await Guard(action);};root.KeyboardAccelerators.Add(accelerator);}
        Add(VirtualKey.N,VirtualKeyModifiers.Control,()=>{AddDocument(new());return Task.CompletedTask;});Add(VirtualKey.O,VirtualKeyModifiers.Control,OpenAsync);Add(VirtualKey.S,VirtualKeyModifiers.Control,()=>SaveAsync(false));Add(VirtualKey.S,VirtualKeyModifiers.Control|VirtualKeyModifiers.Shift,()=>SaveAsync(true));
        Add(VirtualKey.F,VirtualKeyModifiers.Control,()=>FindAsync(false));Add(VirtualKey.H,VirtualKeyModifiers.Control,()=>FindAsync(true));Add(VirtualKey.G,VirtualKeyModifiers.Control,GoToAsync);Add(VirtualKey.Space,VirtualKeyModifiers.Control,CompleteAsync);
        Add(VirtualKey.F5,VirtualKeyModifiers.None,()=>RunAsync(false));Add(VirtualKey.F8,VirtualKeyModifiers.None,()=>RunAsync(true));Add(VirtualKey.F5,VirtualKeyModifiers.Shift,StopAsync);Add(VirtualKey.F9,VirtualKeyModifiers.None,ToggleBreakpointAsync);Add(VirtualKey.F10,VirtualKeyModifiers.None,()=>ResumeAsync("StepOver"));Add(VirtualKey.F11,VirtualKeyModifiers.None,()=>ResumeAsync("StepInto"));Add(VirtualKey.F11,VirtualKeyModifiers.Shift,()=>ResumeAsync("StepOut"));
        Add(VirtualKey.F3,VirtualKeyModifiers.None,()=>{Editor?.Find(lastFind);return Task.CompletedTask;});
    }
    private void Zoom(int step){settings.FontSize=Math.Clamp(settings.FontSize+step,8,48);ApplySettings();}
    private async Task OpenAsync()
    {
        var picker=new FileOpenPicker();foreach(var extension in new[]{".ps1",".psm1",".psd1",".txt"})picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files=await picker.PickMultipleFilesAsync();foreach(var file in files)await OpenPathAsync(file.Path);
    }
    private async Task OpenPathAsync(string path)
    {
        var existing=tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t=>(t.Content as ScriptEditor)?.Model.Path?.Equals(path,StringComparison.OrdinalIgnoreCase)==true);
        if(existing is not null){tabs.SelectedItem=existing;return;}
        AddDocument(await ScriptDocument.LoadAsync(path));settings.RecentFiles.RemoveAll(p=>p.Equals(path,StringComparison.OrdinalIgnoreCase));settings.RecentFiles.Insert(0,path);settings.RecentFiles=settings.RecentFiles.Take(20).ToList();
    }
    private async Task<bool> SaveEditorAsync(ScriptEditor editor,bool saveAs)
    {
        editor.SynchronizeText();
        string? path=editor.Model.Path;
        if(path is null||saveAs)
        {
            var picker=new FileSavePicker{SuggestedFileName=path is null?"Script":System.IO.Path.GetFileName(path)};picker.FileTypeChoices.Add(L.T("Script"),new List<string>{".ps1",".psm1",".psd1"});WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file=await picker.PickSaveFileAsync();if(file is null)return false;path=file.Path;
        }
        await editor.Model.SaveAsync(path);UpdateTab(editor);await SaveRecoveryAsync();return true;
    }
    private async Task SaveAsync(bool saveAs){if(Editor is not null)await SaveEditorAsync(Editor,saveAs);}
    private async Task SaveAllAsync(){foreach(var editor in Editors.ToArray())if(editor.Model.IsDirty||editor.Model.Path is null)if(!await SaveEditorAsync(editor,false))return;}
    private async Task<bool> ConfirmDocumentAsync(ScriptEditor editor)
    {
        if(!editor.Model.IsDirty)return true;
        var result=await Ask(L.T("SaveChanges"),editor.Model.Title+"\n\n"+L.T("Unsaved"),L.T("Save"),L.T("Discard"));
        return result==ContentDialogResult.Secondary || result==ContentDialogResult.Primary&&await SaveEditorAsync(editor,false)&&!editor.Model.IsDirty;
    }
    private async Task<bool> CloseTabAsync(TabViewItem tab)
    {
        if(tab.Content is ScriptEditor editor&&!await ConfirmDocumentAsync(editor))return false;
        tabs.TabItems.Remove(tab);if(tabs.TabItems.Count==0&&!closing)AddDocument(new());await SaveRecoveryAsync();return true;
    }
    private async Task CloseAllAsync(){foreach(var tab in tabs.TabItems.OfType<TabViewItem>().ToArray())if(!await CloseTabAsync(tab))break;}
    private bool startupRecoveryLoaded;
    private async void OnClosing(AppWindow sender,AppWindowClosingEventArgs args)
    {
        if(closing)return;args.Cancel=true;
        await CloseApplicationAsync();
    }
    private async Task CloseApplicationAsync()
    {
        if(closing)return;
        if(!startupRecoveryLoaded){closing=true;Close();return;}
        if(closingAttempt)return;
        if(workspaceLease is null){closing=true;Close();return;}
        closingAttempt=true;tabs.IsEnabled=false;command.IsEnabled=false;sessionPicker.IsEnabled=false;results.IsEnabled=false;
        try{await Guard(async()=>
        {
            foreach(var editor in Editors)editor.SynchronizeText();
            if(!settings.RestoreSession)
                foreach(var editor in Editors.ToArray())if(!await ConfirmDocumentAsync(editor))return;
            settings.OpenFiles=Editors.Select(e=>e.Model.Path).OfType<string>().ToList();settings.ActiveTab=tabs.SelectedIndex;settings.Width=(int)(AppWindow.Size.Width/root.XamlRoot.RasterizationScale);settings.Height=(int)(AppWindow.Size.Height/root.XamlRoot.RasterizationScale);
            if(settings.RestoreSession)await store.SaveSessionAsync(Editors.Select(e=>e.Model).ToArray(),tabs.SelectedIndex);
            else await store.ClearSessionAsync();
            await store.SaveSettingsAsync(settings);await store.SaveRecoveryAsync([]);
            closing=true;analysisTimer.Stop();recoveryTimer.Stop();outputTimer.Stop();foreach(var session in sessions){if(session.Engine is not null)await session.Engine.DisposeAsync();await session.Terminal.DisposeAsync();}workspaceLease?.Dispose();Close();
        },true);}
        finally{closingAttempt=false;if(!closing){tabs.IsEnabled=true;command.IsEnabled=true;sessionPicker.IsEnabled=true;results.IsEnabled=true;}}
    }
    private readonly HashSet<string> externalDismissed=[];
    private async Task CheckExternalAsync()
    {
        var editor=Editor;if(editor?.Model.Path is null||externalDismissed.Contains(editor.Model.Path))return;
        if(await editor.Model.HasExternalChangeAsync())
        {
            if(await Ask(L.T("ExternalChange"),L.T("ExternalText"),L.T("Reload"),L.T("Keep"))==ContentDialogResult.Primary)
            {editor.Load(await ScriptDocument.LoadAsync(editor.Model.Path));UpdateTab(editor);}
            else externalDismissed.Add(editor.Model.Path);
        }
    }
    private async Task FindAsync(bool replace)
    {
        var find=new TextBox{Header=L.T("Find"),Text=lastFind,MinWidth=380};var replacement=new TextBox{Header=L.T("Replace")};var stack=new StackPanel{Spacing=12};stack.Children.Add(find);if(replace)stack.Children.Add(replacement);
        await dialogs.WaitAsync();dialogOpen=true;
        try
        {
            var result=await new ContentDialog{XamlRoot=root.XamlRoot,Title=L.T(replace?"Replace":"Find"),Content=stack,PrimaryButtonText=L.T(replace?"Replace":"Next"),SecondaryButtonText=replace?L.T("Next"):L.T("Previous"),CloseButtonText=L.T("Close"),DefaultButton=ContentDialogButton.Primary}.ShowAsync();
            lastFind=find.Text;
            if(replace&&result==ContentDialogResult.Primary&&Editor is not null){if(Editor.SelectedText.Equals(lastFind,StringComparison.CurrentCultureIgnoreCase))Editor.Insert(replacement.Text);Editor.Find(lastFind);}
            else if(result!=ContentDialogResult.None)Editor?.Find(lastFind,!replace&&result==ContentDialogResult.Secondary);
        }
        finally{dialogOpen=false;dialogs.Release();}
    }
    private async Task<string?> InputAsync(string title,string initial="")
    {
        var box=new TextBox{Text=initial,MinWidth=380};await dialogs.WaitAsync();dialogOpen=true;
        try {var result=await new ContentDialog{XamlRoot=root.XamlRoot,Title=title,Content=box,PrimaryButtonText=L.T("OK"),CloseButtonText=L.T("Cancel"),DefaultButton=ContentDialogButton.Primary}.ShowAsync();return result==ContentDialogResult.Primary?box.Text:null;}
        finally{dialogOpen=false;dialogs.Release();}
    }
    private async Task GoToAsync(){var text=await InputAsync(L.T("GoTo"),Editor?.CurrentLine.ToString() ?? "1");if(int.TryParse(text,out var line))Editor?.SelectLine(Math.Max(1,line));}
    private async Task SnippetsAsync()
    {
        var snippets=new Dictionary<string,string>{{"Advanced function","function Verb-Noun {\r\n    [CmdletBinding()]\r\n    param(\r\n        [Parameter(Mandatory)]\r\n        [string]$Name\r\n    )\r\n\r\n    process {\r\n        $Name\r\n    }\r\n}\r\n"},{"Try / Catch / Finally","try {\r\n    \r\n} catch {\r\n    Write-Error $_\r\n} finally {\r\n    \r\n}\r\n"},{"ForEach","foreach ($item in $collection) {\r\n    $item\r\n}\r\n"},{"Comment-based help","<#\r\n.SYNOPSIS\r\n    \r\n.DESCRIPTION\r\n    \r\n.EXAMPLE\r\n    \r\n#>\r\n"}};
        var list=new ListView{ItemsSource=snippets.Keys.ToArray(),SelectedIndex=0,MinWidth=380};await dialogs.WaitAsync();dialogOpen=true;
        try{if(await new ContentDialog{XamlRoot=root.XamlRoot,Title=L.T("Snippets"),Content=list,PrimaryButtonText=L.T("Insert"),CloseButtonText=L.T("Cancel")}.ShowAsync()==ContentDialogResult.Primary&&list.SelectedItem is string key)Editor?.Insert(snippets[key]);}
        finally{dialogOpen=false;dialogs.Release();}
    }
}

