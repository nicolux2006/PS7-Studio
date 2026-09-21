namespace PS7Studio.Core;

/// <summary>Text-only differential history; visual formatting never enters the undo stack.</summary>
public sealed class EditHistory
{
    private sealed record Change(int Start,string Before,string After);
    private readonly List<Change> undo=[];
    private readonly Stack<Change> redo=[];
    private long retainedCharacters;
    public bool CanUndo=>undo.Count>0;
    public bool CanRedo=>redo.Count>0;
    public void Record(string before,string after)
    {
        if(before==after)return;
        var start=0;while(start<before.Length&&start<after.Length&&before[start]==after[start])start++;
        var endBefore=before.Length;var endAfter=after.Length;
        while(endBefore>start&&endAfter>start&&before[endBefore-1]==after[endAfter-1]){endBefore--;endAfter--;}
        var change=new Change(start,before[start..endBefore],after[start..endAfter]);
        undo.Add(change);redo.Clear();retainedCharacters+=change.Before.Length+change.After.Length;
        while(undo.Count>1&&(undo.Count>500||retainedCharacters>8*1024*1024)){var removed=undo[0];undo.RemoveAt(0);retainedCharacters-=removed.Before.Length+removed.After.Length;}
    }
    public string Undo(string text)
    {
        if(!CanUndo)return text;var change=undo[^1];undo.RemoveAt(undo.Count-1);redo.Push(change);
        return text[..change.Start]+change.Before+text[(change.Start+change.After.Length)..];
    }
    public string Redo(string text)
    {
        if(!CanRedo)return text;var change=redo.Pop();undo.Add(change);
        return text[..change.Start]+change.After+text[(change.Start+change.Before.Length)..];
    }
    public void Clear(){undo.Clear();redo.Clear();retainedCharacters=0;}
}
