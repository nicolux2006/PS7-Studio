using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Collections.Concurrent;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace PS7Studio.App;

internal sealed class WebViewRuntimeMissingException(Exception inner):Exception("WebView2 initialization failed.",inner);

internal sealed class TerminalView : Grid, IAsyncDisposable
{
    private static CoreWebView2Environment? sharedEnvironment;
    internal static async Task EnsureRuntimeAvailableAsync(XamlRoot root)
    {
        try
        {
            sharedEnvironment=await CoreWebView2Environment.CreateWithOptionsAsync(null,Path.Combine(AppState.DirectoryPath,"terminal-webview"),new CoreWebView2EnvironmentOptions());
        }
        catch(Exception ex)
        {
            App.Log(ex);
            var content=new StackPanel{Spacing=12};
            content.Children.Add(new TextBlock{Text=L.T("WebViewRuntimeMissing"),TextWrapping=TextWrapping.Wrap});
            content.Children.Add(new HyperlinkButton{Content=L.T("WebViewRuntimeDownload"),NavigateUri=new Uri("https://developer.microsoft.com/microsoft-edge/webview2/")});
            var dialog=new ContentDialog{XamlRoot=root,Title=L.T("Error"),Content=content,CloseButtonText=L.T("Close"),DefaultButton=ContentDialogButton.Close};
            await dialog.ShowAsync();
            throw new WebViewRuntimeMissingException(ex);
        }
    }
    private const string Origin="https://ps7studio-terminal.local/";
    private readonly WebView2 browser=new();
    private readonly ConcurrentDictionary<long,TaskCompletionSource<string>> replies=new();
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim writing=new(1,1);
    private Task? initialization;
    private long sequence;
    private bool disposed;
    private Exception? rendererFailure;
    public Func<string,Task>? InputReceived {get;set;}
    public Func<byte[],Task>? BinaryInputReceived {get;set;}
    public Action<int,int>? ResizeRequested {get;set;}
    public int Columns {get;private set;}=120;
    public int Rows {get;private set;}=30;
    public bool HasInputFocus=>browser.FocusState!=FocusState.Unfocused;
    public TerminalView(){Children.Add(browser);}
    public Task InitializeAsync()=>initialization??=InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        CoreWebView2Environment environment;
        try
        {
            environment=sharedEnvironment??await CoreWebView2Environment.CreateWithOptionsAsync(null,Path.Combine(AppState.DirectoryPath,"terminal-webview"),new CoreWebView2EnvironmentOptions());
        }
        catch(Exception ex) when(ex.HResult==unchecked((int)0x80070002))
        {
            var message=L.T("WebViewRuntimeMissing");
            browser.Visibility=Visibility.Collapsed;
            var guidance=new StackPanel{Spacing=12,Margin=new Thickness(16)};
            guidance.Children.Add(new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap});
            guidance.Children.Add(new HyperlinkButton{Content=L.T("WebViewRuntimeDownload"),NavigateUri=new Uri("https://developer.microsoft.com/microsoft-edge/webview2/")});
            Children.Add(guidance);
            throw new InvalidOperationException(message,ex);
        }
        await browser.EnsureCoreWebView2Async(environment);
        var core=browser.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=false;
        core.Settings.IsStatusBarEnabled=false;core.Settings.IsZoomControlEnabled=false;
        core.Settings.AreBrowserAcceleratorKeysEnabled=false;
        core.NavigationStarting+=(_,e)=>{if(e.Uri!=Origin+"index.html")e.Cancel=true;};
        core.NewWindowRequested+=(_,e)=>e.Handled=true;
        core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
        core.WebMessageReceived+=OnMessage;
        core.ProcessFailed+=(_,_)=>
        {
            rendererFailure=new IOException("Terminal renderer stopped. Reset the session to reconnect.");
            browser.Visibility=Visibility.Collapsed;
            Children.Add(new TextBlock{Text=L.T("Error")+": "+rendererFailure.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(16)});
            foreach(var reply in replies.Values)reply.TrySetException(rendererFailure);ready.TrySetException(rendererFailure);
        };
        core.SetVirtualHostNameToFolderMapping("ps7studio-terminal.local",Path.Combine(AppContext.BaseDirectory,"Assets","Terminal"),CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate(Origin+"index.html");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private async void OnMessage(CoreWebView2 sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if(disposed||e.Source!=Origin+"index.html")return;
            using var document=JsonDocument.Parse(e.WebMessageAsJson);var message=document.RootElement;
            switch(message.GetProperty("type").GetString())
            {
                case "ready":ready.TrySetResult();break;
                case "ack":
                    if(replies.TryRemove(message.GetProperty("id").GetInt64(),out var reply))reply.TrySetResult(message.TryGetProperty("data",out var value)?value.GetString()??"":"");break;
                case "input":
                    var text=message.GetProperty("data").GetString()??"";
                    if(text.Length<=1024*1024&&InputReceived is {} input)await input(text);break;
                case "binary":
                    var binary=message.GetProperty("data").GetString()??"";
                    if(binary.Length<=1024*1024&&binary.All(c=>c<=255)&&BinaryInputReceived is {} bytes)await bytes(binary.Select(c=>(byte)c).ToArray());break;
                case "resize":
                    Columns=Math.Clamp(message.GetProperty("cols").GetInt32(),2,500);Rows=Math.Clamp(message.GetProperty("rows").GetInt32(),1,300);ResizeRequested?.Invoke(Columns,Rows);break;
                case "copy":
                    var selected=message.GetProperty("data").GetString();
                    if(!string.IsNullOrEmpty(selected)){var clipboard=new DataPackage();clipboard.SetText(selected);Clipboard.SetContent(clipboard);Clipboard.Flush();}break;
                case "paste":
                    await PasteAsync();break;
                case "contextMenu":
                    var menu=new MenuFlyout();
                    var copy=new MenuFlyoutItem{Text=L.T("Copy"),IsEnabled=message.GetProperty("selected").GetBoolean()};copy.Click+=(_,_)=>CopySelection();menu.Items.Add(copy);
                    var paste=new MenuFlyoutItem{Text=L.T("Paste")};paste.Click+=async(_,_)=>{try{await PasteAsync();}catch(Exception ex){App.Log(ex);}};menu.Items.Add(paste);
                    var all=new MenuFlyoutItem{Text=L.T("SelectAll")};all.Click+=(_,_)=>Post(new{type="selectAll"});menu.Items.Add(all);
                    menu.ShowAt(this,new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions{Position=new Windows.Foundation.Point(Math.Clamp(message.GetProperty("x").GetDouble(),0,ActualWidth),Math.Clamp(message.GetProperty("y").GetDouble(),0,ActualHeight))});break;
            }
        }
        catch(Exception ex){App.Log(ex);}
    }
    private async Task PasteAsync()
    {
        var content=Clipboard.GetContent();if(content.Contains(StandardDataFormats.Text)){var text=await content.GetTextAsync();if(!disposed)Post(new{type="paste",data=text});}
    }
    private void Post(object message)=>browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    private Task DispatchAsync(Action action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!DispatcherQueue.TryEnqueue(()=>{try{if(disposed)throw new ObjectDisposedException(nameof(TerminalView));action();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}}))done.TrySetException(new ObjectDisposedException(nameof(TerminalView)));
        return done.Task;
    }
    public async Task WriteAsync(string data)
    {
        if(rendererFailure is {} failure)throw new IOException(failure.Message,failure);
        await ready.Task;await writing.WaitAsync();
        try
        {
            var id=Interlocked.Increment(ref sequence);var ack=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);replies[id]=ack;
            try{await DispatchAsync(()=>Post(new {type="write",id,data}));await ack.Task.WaitAsync(TimeSpan.FromSeconds(30));}
            finally{replies.TryRemove(id,out _);}
        }
        finally{writing.Release();}
    }
    public void FocusTerminal(){if(ready.Task.IsCompletedSuccessfully&&!disposed){browser.Focus(FocusState.Programmatic);Post(new {type="focus"});}}
    public void Clear(){if(ready.Task.IsCompletedSuccessfully&&!disposed)Post(new {type="clear"});}
    public void CopySelection(){if(ready.Task.IsCompletedSuccessfully&&!disposed)Post(new {type="copy"});}
    public void ApplySettings(string font,double size,bool dark){if(ready.Task.IsCompletedSuccessfully&&!disposed)Post(new {type="settings",font,fontSize=size,dark});}
    public ValueTask DisposeAsync()
    {
        if(disposed)return ValueTask.CompletedTask;disposed=true;
        ready.TrySetCanceled();foreach(var reply in replies.Values)reply.TrySetCanceled();replies.Clear();browser.Close();return ValueTask.CompletedTask;
    }
}
