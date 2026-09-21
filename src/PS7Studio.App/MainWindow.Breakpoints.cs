using System.Text.Json;

namespace PS7Studio.App;

public sealed partial class MainWindow
{
    private async Task ManageSelectedBreakpointAsync(string operation)
    {
        EnsureIdle();if(results.SelectedItem is not BreakpointEntry breakpoint)return;
        var owner=ActiveSession;var data=await owner.Engine!.RequestAsync("breakpoints",new {id=breakpoint.Id,operation});
        if(ReferenceEquals(owner,ActiveSession))ShowBreakpoints(data);
    }
    private async Task ClearBreakpointsAsync()
    {
        EnsureIdle();var owner=ActiveSession;
        var data=await owner.Engine!.RequestAsync("breakpoints",new {clear=true});
        if(ReferenceEquals(owner,ActiveSession)){panel.SelectedIndex=2;ShowBreakpoints(data);}
    }
    private async Task SetConditionalBreakpointAsync()
    {
        EnsureIdle();var editor=Editor;if(editor is null)return;
        var owner=ActiveSession;var client=owner.Engine!;var line=editor.CurrentLine;
        var condition=await InputAsync(L.T("Condition"));if(string.IsNullOrWhiteSpace(condition))return;
        if(!ReferenceEquals(client,owner.Engine)||owner.State!="Idle")return;
        var syntax=await client.RequestAsync("parse",new {code="if ("+condition+") { break }"});
        var errors=syntax.GetProperty("errors").EnumerateArray().ToArray();
        if(errors.Length>0)throw new InvalidOperationException(errors[0].GetProperty("message").GetString());
        var data=await client.RequestAsync("breakpoints",new {});
        var existing=data.GetProperty("items").EnumerateArray().FirstOrDefault(x=>string.Equals(x.GetProperty("path").GetString(),DocumentSource(editor),StringComparison.OrdinalIgnoreCase)&&x.GetProperty("line").GetInt32()==line);
        if(existing.ValueKind!=JsonValueKind.Undefined)await client.RequestAsync("breakpoints",new {id=existing.GetProperty("id").GetInt32(),operation="remove"});
        var updated=await client.RequestAsync("breakpoints",new {path=DocumentSource(editor),line,condition});
        if(ReferenceEquals(owner,ActiveSession)){panel.SelectedIndex=2;ShowBreakpoints(updated);}
    }
}
