using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Text;
using Windows.System;
using PS7Studio.Core;
using System.Text.Json;

namespace PS7Studio.App;

internal sealed partial class ScriptEditor : Grid
{
    public ScriptDocument Model {get;private set;}
    public RichEditBox Box {get;}=new(){AcceptsReturn=true,BorderThickness=new Thickness(0),Padding=new Thickness(14,12,12,12),TextWrapping=TextWrapping.NoWrap};
    private readonly Canvas gutter=new(){Width=60};
    private readonly EditHistory history=new();
    private TextCoordinates coordinates=new("");
    private bool lineNumbers=true;
    private readonly HashSet<int> breakpointLines=[];
    private readonly List<(int start,int end)> folds=[];
    private readonly List<(int start,int end)> collapsed=[];
    private int executionLine;
    private bool loading;
    private string newline="\r\n";
    public event Action? Changed;
    public event Action? CaretChanged;
    public event Action<int>? BreakpointRequested;
    public int SelectionStart=>Box.Document.Selection.StartPosition;
    public int SelectionLength=>Box.Document.Selection.EndPosition-SelectionStart;
    public int SourceCursor=>coordinates.NativeToSource(SelectionStart);
    public int CurrentLine=>coordinates.LineAtNative(SelectionStart);
    public ScriptEditor(ScriptDocument document,StudioSettings settings)
    {
        Model=document;
        ColumnDefinitions.Add(new(){Width=GridLength.Auto});ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        Children.Add(gutter);SetColumn(Box,1);Children.Add(Box);SetColumn(adornments,1);Children.Add(adornments);
        AutomationProperties.SetName(Box,L.T("Script"));
        Box.TextChanged+=(_,_)=>SynchronizeText();
        Box.SelectionChanged+=(_,_)=>{DismissCompletion();CaretChanged?.Invoke();UpdateGutter();};
        Box.PreviewKeyDown+=OnKeyDown;
        Box.LostFocus+=(_,_)=>DispatcherQueue.TryEnqueue(()=>
        {
            var focused=FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            for(var current=focused;current is not null;current=VisualTreeHelper.GetParent(current))
                if(ReferenceEquals(current,completionPanel)||ReferenceEquals(current,Box))return;
            DismissCompletion();
        });
        Box.Loaded+=(_,_)=>{var scroll=Descendant<ScrollViewer>(Box);if(scroll is not null)scroll.ViewChanged+=(_,_)=>UpdateGutter();UpdateGutter();};
        Box.SizeChanged+=(_,_)=>UpdateGutter();
        Box.IsSpellCheckEnabled=false;Box.IsTextPredictionEnabled=false;
        Load(document);ApplySettings(settings);
    }
    public void Load(ScriptDocument model)
    {
        loading=true;Model=model;newline=model.Text.Contains("\r\n")?"\r\n":"\n";
        if(model.Text.Length==0)newline="\r\n";
        coordinates=new(model.Text);history.Clear();Box.Document.SetText(TextSetOptions.None,coordinates.Native);Box.Document.ClearUndoRedoHistory();loading=false;UpdateGutter();
    }
    private string ReadNative()
    {
        Box.Document.GetText(TextGetOptions.None,out var text);
        if(text.EndsWith('\r'))text=text[..^1];
        return text;
    }
    // RichEditBox can deliver TextChanged after a toolbar click. Read its document
    // synchronously at execution/save boundaries so the visible edit is never skipped.
    public void SynchronizeText()
    {
        if(loading)return;var native=ReadNative();if(native==coordinates.Native)return;
        ExpandAll();bracketPairs.Clear();folds.Clear();
        var text=coordinates.ApplyNativeEdit(native,newline);history.Record(Model.Text,text);Model.Text=text;coordinates=new(text);collapsed.Clear();UpdateGutter();Changed?.Invoke();
    }
    public string ReadText()=>Model.Text;
    public string SelectedText=>Model.Text[coordinates.NativeToSource(SelectionStart)..coordinates.NativeToSource(Box.Document.Selection.EndPosition)];
    public string SelectionOrLine()=>SelectionLength>0?SelectedText:coordinates.Native.Split('\r').ElementAtOrDefault(CurrentLine-1) ?? "";
    public void Insert(string text)
    {
        Box.Document.Selection.SetText(TextSetOptions.None,text);
        var end=Box.Document.Selection.EndPosition;Box.Document.Selection.SetRange(end,end);
    }
    private static T? Descendant<T>(DependencyObject parent) where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T found)return found;var nested=Descendant<T>(child);if(nested is not null)return nested;}return null;}
    private void UpdateGutter()
    {
        if(!Box.IsLoaded||Box.ActualHeight<=0)return;
        gutter.Clip=new RectangleGeometry{Rect=new Windows.Foundation.Rect(0,0,60,Box.ActualHeight)};gutter.Children.Clear();adornments.Children.Clear();adornments.Clip=new RectangleGeometry{Rect=new Windows.Foundation.Rect(0,0,Box.ActualWidth,Box.ActualHeight)};
        try
        {
            var first=Box.Document.GetRangeFromPoint(new Windows.Foundation.Point(14,14),PointOptions.ClientCoordinates).StartPosition;
            var firstLine=Math.Max(1,coordinates.LineAtNative(first)-1);
            for(var line=firstLine;line<=Math.Min(coordinates.LineStarts.Length,firstLine+150);line++)
            {
                var offset=coordinates.LineStarts[line-1];var range=Box.Document.GetRange(offset,offset);
                range.GetRect(PointOptions.ClientCoordinates,out var rect,out _);if(rect.Y>Box.ActualHeight)break;if(rect.Y< -30||range.CharacterFormat.Hidden==FormatEffect.On)continue;
                DrawLineAdornments(line,offset,rect);
                var n=line;var label=new TextBlock{Text=lineNumbers?line.ToString():"",FontFamily=Box.FontFamily,FontSize=Box.FontSize,Opacity=line==CurrentLine?1:.45,Width=38,TextAlignment=TextAlignment.Right};Canvas.SetTop(label,rect.Y+Box.Padding.Top);gutter.Children.Add(label);
                var hit=new Border{Width=58,Height=Math.Max(rect.Height,Box.FontSize*1.25),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent)};Canvas.SetTop(hit,rect.Y+Box.Padding.Top);hit.DoubleTapped+=(_,_)=>BreakpointRequested?.Invoke(n);gutter.Children.Add(hit);
                if(breakpointLines.Contains(line)||executionLine==line){var marker=new TextBlock{Text=executionLine==line?"➜":"●",Foreground=new SolidColorBrush(executionLine==line?Microsoft.UI.Colors.Goldenrod:Microsoft.UI.Colors.IndianRed),FontSize=12};Canvas.SetTop(marker,rect.Y+Box.Padding.Top);Canvas.SetLeft(marker,43);gutter.Children.Add(marker);}
            }
        }
        catch(System.Runtime.InteropServices.COMException){ }
        DrawBracketMatch();
    }
    public void ApplySettings(StudioSettings settings)
    {
        Box.FontFamily=new FontFamily(settings.Font);Box.FontSize=settings.FontSize;
        Box.TextWrapping=settings.WordWrap?TextWrapping.Wrap:TextWrapping.NoWrap;
        lineNumbers=settings.LineNumbers;showWhitespace=settings.ShowWhitespace;autoIndent=settings.AutoIndent;
        tabText=settings.UseSpaces?new string(' ',settings.TabWidth):"\t";
        Box.Document.DefaultTabStop=(float)(settings.TabWidth*settings.FontSize*.6);UpdateGutter();
    }
    private string tabText="    ";
    private bool autoIndent=true;
    private void OnKeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(HandleCompletionKey(e.Key)){e.Handled=true;return;}
        bool Down(VirtualKey key)=>Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if(Down(VirtualKey.Control)&&e.Key==VirtualKey.Z){e.Handled=true;Undo();}
        else if(Down(VirtualKey.Control)&&e.Key==VirtualKey.Y){e.Handled=true;Redo();}
        else if(e.Key==VirtualKey.Tab){e.Handled=true;if(SelectionLength>0||Down(VirtualKey.Shift))TransformLines(Down(VirtualKey.Shift)?"outdent":"indent");else Insert(tabText);}
        else if(e.Key==VirtualKey.Enter)
        {e.Handled=true;var before=coordinates.Native[..Math.Min(SelectionStart,coordinates.Native.Length)];var line=before[(before.LastIndexOf('\r')+1)..];var indent=autoIndent?new string(line.TakeWhile(c=>c is ' ' or '\t').ToArray()):"";if(autoIndent&&line.TrimEnd().EndsWith('{'))indent+=tabText;Insert(newline+indent);}
    }
    public void SelectLine(int line)
    {
        ExpandAll();
        Box.Document.GetText(TextGetOptions.None,out var raw);
        var start=0;for(var n=1;n<line&&start<raw.Length;n++){var next=raw.IndexOf('\r',start);if(next<0)break;start=next+1;}
        var end=raw.IndexOf('\r',start);if(end<0)end=raw.Length;
        Box.Document.Selection.SetRange(start,end);Box.Document.Selection.ScrollIntoView(PointOptions.Start);Box.Focus(FocusState.Programmatic);
    }
    public bool Find(string text,bool backwards=false)
    {
        if(text.Length==0)return false;
        Box.Document.GetText(TextGetOptions.None,out var raw);
        var start=Math.Clamp(backwards?SelectionStart-1:Box.Document.Selection.EndPosition,0,Math.Max(0,raw.Length-1));
        var index=backwards?raw.LastIndexOf(text,start,StringComparison.CurrentCultureIgnoreCase):raw.IndexOf(text,start,StringComparison.CurrentCultureIgnoreCase);
        if(index<0)index=backwards?raw.LastIndexOf(text,StringComparison.CurrentCultureIgnoreCase):raw.IndexOf(text,StringComparison.CurrentCultureIgnoreCase);
        if(index<0)return false;
        Box.Document.Selection.SetRange(index,index+text.Length);Box.Document.Selection.ScrollIntoView(PointOptions.Start);Box.Focus(FocusState.Programmatic);return true;
    }
    public void TransformLines(string operation)
    {
        var first=coordinates.LineStarts[CurrentLine-1];var lastPosition=Box.Document.Selection.EndPosition;
        if(lastPosition>SelectionStart&&coordinates.LineStarts.Contains(lastPosition))lastPosition--;
        var lastLine=coordinates.LineAtNative(lastPosition);var last=lastLine<coordinates.LineStarts.Length?coordinates.LineStarts[lastLine]-1:coordinates.Native.Length;
        var selected=coordinates.Native[first..last];Box.Document.Selection.SetRange(first,last);
        var lines=selected.Split('\r');
        Insert(string.Join(newline,lines.Select(line=>operation switch
        {"comment"=>"# "+line,"uncomment"=>System.Text.RegularExpressions.Regex.Replace(line,"^(\\s*)# ?","$1"),"indent"=>tabText+line,"outdent"=>line.StartsWith(tabText)?line[tabText.Length..]:line.TrimStart('\t'),_=>line})));
        Box.Document.Selection.SetRange(first,Box.Document.Selection.EndPosition);
    }
    public void Highlight(JsonElement tokens,bool dark)
    {
        folds.Clear();bracketPairs.Clear();var brackets=new Stack<(string kind,int offset)>();
        foreach(var token in tokens.EnumerateArray())
        {
            var kind=token.GetProperty("kind").GetString() ?? "";var offset=token.GetProperty("start").GetInt32();
            if(kind is "LCurly" or "LParen" or "LBracket" or "AtParen" or "AtCurly" or "DollarParen")brackets.Push((kind,offset+token.GetProperty("length").GetInt32()-1));
            else if(kind is "RCurly" or "RParen" or "RBracket"&&brackets.Count>0)
            {
                if(brackets.Count==0)continue;var opening=brackets.Peek();
                if((kind=="RCurly"&&opening.kind is "LCurly" or "AtCurly")||(kind=="RParen"&&opening.kind is "LParen" or "AtParen" or "DollarParen")||(kind=="RBracket"&&opening.kind=="LBracket"))
                {brackets.Pop();bracketPairs[opening.offset]=offset;bracketPairs[offset]=opening.offset;}
            }
        }
        var stack=new Stack<int>();
        foreach(var token in tokens.EnumerateArray())
        {var kind=token.GetProperty("kind").GetString();if(kind=="LCurly")stack.Push(token.GetProperty("start").GetInt32());else if(kind=="RCurly"&&stack.Count>0){var start=stack.Pop();var end=token.GetProperty("start").GetInt32()+1;if(coordinates.LineAtNative(coordinates.SourceToNative(start))!=coordinates.LineAtNative(coordinates.SourceToNative(end)))folds.Add((start,end));}}
        loading=true;Box.Document.BatchDisplayUpdates();
        try
        {
            var all=Box.Document.GetRange(0,int.MaxValue);
            all.CharacterFormat.ForegroundColor=dark?Microsoft.UI.Colors.Gainsboro:Microsoft.UI.Colors.Black;
            foreach(var token in tokens.EnumerateArray())
            {
                var kind=token.GetProperty("kind").GetString() ?? "";
                var color=kind switch
                {"Comment"=>dark?Windows.UI.Color.FromArgb(255,115,160,121):Windows.UI.Color.FromArgb(255,38,112,50),
                 "StringLiteral" or "StringExpandable" or "HereStringLiteral" or "HereStringExpandable"=>dark?Windows.UI.Color.FromArgb(255,226,182,139):Windows.UI.Color.FromArgb(255,153,55,28),
                 "Variable"=>dark?Windows.UI.Color.FromArgb(255,136,210,224):Windows.UI.Color.FromArgb(255,0,96,145),
                 "Number"=>dark?Windows.UI.Color.FromArgb(255,188,205,160):Windows.UI.Color.FromArgb(255,80,105,28),
                 "Parameter"=>dark?Windows.UI.Color.FromArgb(255,186,167,230):Windows.UI.Color.FromArgb(255,99,65,145),
                 _=>token.GetProperty("flags").GetString()!.Contains("Keyword")?(dark?Windows.UI.Color.FromArgb(255,199,159,219):Windows.UI.Color.FromArgb(255,131,36,141)):dark?Microsoft.UI.Colors.Gainsboro:Microsoft.UI.Colors.Black};
                var offset=token.GetProperty("start").GetInt32();var end=offset+token.GetProperty("length").GetInt32();
                // RichEdit positions count paragraph endings once; parser offsets count CRLF twice.
                var a=coordinates.SourceToNative(offset);var b=coordinates.SourceToNative(end);
                if(b>a)Box.Document.GetRange(a,b).CharacterFormat.ForegroundColor=color;
            }
        }
        finally{Box.Document.ClearUndoRedoHistory();Box.Document.ApplyDisplayUpdates();loading=false;UpdateGutter();}
    }
    private void ApplyHistory(string text)
    {var caret=SelectionStart;loading=true;try{Model.Text=text;coordinates=new(text);Box.Document.SetText(TextSetOptions.None,coordinates.Native);Box.Document.Selection.SetRange(Math.Min(caret,coordinates.Native.Length),Math.Min(caret,coordinates.Native.Length));Box.Document.ClearUndoRedoHistory();collapsed.Clear();}finally{loading=false;}UpdateGutter();Changed?.Invoke();}
    public void Undo()=>ApplyHistory(history.Undo(Model.Text));
    public void Redo()=>ApplyHistory(history.Redo(Model.Text));
    public void SelectSource(int start,int length){Box.Document.Selection.SetRange(coordinates.SourceToNative(start),coordinates.SourceToNative(start+length));Box.Document.Selection.ScrollIntoView(PointOptions.Start);Box.Focus(FocusState.Programmatic);}
    public void SetBreakpoints(IEnumerable<int> lines){breakpointLines.Clear();foreach(var line in lines)breakpointLines.Add(line);UpdateGutter();}
    public void SetExecutionLine(int line){executionLine=line;UpdateGutter();}
    public void ToggleFold()
    {
        var candidate=folds.Where(f=>f.start<=SourceCursor&&f.end>SourceCursor).OrderBy(f=>f.end-f.start).FirstOrDefault();if(candidate==default)return;loading=true;
        try{var range=Box.Document.GetRange(coordinates.SourceToNative(candidate.start+1),coordinates.SourceToNative(candidate.end-1));if(collapsed.Remove(candidate))range.CharacterFormat.Hidden=FormatEffect.Off;else{range.CharacterFormat.Hidden=FormatEffect.On;collapsed.Add(candidate);}}
        finally{loading=false;UpdateGutter();}
    }
    public void ExpandAll(){if(collapsed.Count==0)return;loading=true;try{Box.Document.GetRange(0,int.MaxValue).CharacterFormat.Hidden=FormatEffect.Off;collapsed.Clear();}finally{loading=false;}}
}
