using System.Collections.ObjectModel;
using System.Text;

namespace PS7Studio.Core;

public sealed record OutputLine(string Text,string Stream)
{
    public override string ToString()=>Text;
}

/// <summary>Bounded scrollback with host Write/WriteLine and cross-frame CRLF handling.</summary>
public sealed class OutputBuffer(int maxLines=5000,int maxLineLength=16384)
{
    public ObservableCollection<OutputLine> Lines {get;}=[];
    private bool openLine;
    private bool pendingCarriageReturn;
    public void Clear(){Lines.Clear();openLine=false;pendingCarriageReturn=false;}
    public void Append(string stream,string text)
    {
        var current=new StringBuilder(openLine&&Lines.Count>0?Lines[^1].Text:"");
        var dirty=false;
        void Flush(){if(dirty&&openLine){Lines[^1]=new(current.ToString(),stream);dirty=false;}}
        foreach(var ch in text)
        {
            if(pendingCarriageReturn)
            {
                pendingCarriageReturn=false;
                if(ch=='\n'){Flush();openLine=false;continue;}
                if(openLine){current.Clear();dirty=true;}
            }
            if(ch=='\r'){pendingCarriageReturn=true;continue;}
            if(!openLine)
            {
                current.Clear();Lines.Add(new("",stream));openLine=true;
                while(Lines.Count>Math.Max(1,maxLines))Lines.RemoveAt(0);
            }
            if(ch=='\n'){Flush();openLine=false;continue;}
            if(current.Length>=Math.Max(1,maxLineLength))
            {
                Flush();current.Clear();Lines.Add(new("",stream));while(Lines.Count>Math.Max(1,maxLines))Lines.RemoveAt(0);
            }
            current.Append(ch);dirty=true;
        }
        Flush();
    }
}
