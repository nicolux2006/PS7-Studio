namespace PS7Studio.Core;

// Presentation only: replace one exact, generated PSES invocation, never user output.
// PSES 4.7 hard-codes WriteInputToHost for DAP launches, with no protocol switch.
internal sealed class TerminalRunEcho
{
    private sealed class Replacement(string expected,string display)
    {
        public readonly string Expected=expected,Display=display;
        public readonly DateTime Expires=DateTime.UtcNow.AddSeconds(30);
        public string Pending="";
        public bool Done;
        public string Process(string chunk)
        {
            var text=Pending+chunk;Pending="";
            if(DateTime.UtcNow>Expires){Done=true;return text;}
            var index=text.IndexOf(Expected,StringComparison.Ordinal);
            if(index>=0){Done=true;return text[..index]+Display+text[(index+Expected.Length)..];}
            var keep=Math.Min(text.Length,Expected.Length-1);
            while(keep>0&&!Expected.AsSpan().StartsWith(text.AsSpan(text.Length-keep),StringComparison.Ordinal))keep--;
            Pending=text[(text.Length-keep)..];return text[..(text.Length-keep)];
        }
    }
    private readonly object gate=new();
    private readonly List<Replacement> replacements=[];
    public void Register(string variable,string source)
    {
        var display=source.Replace("\r\n","\n").Replace('\r','\n').Replace("\n","\r\n");
        lock(gate)replacements.Add(new(". {\r\n& $global:"+variable+"\r\n}\r\n",display+"\r\n"));
    }
    public string Process(string chunk)
    {
        lock(gate)
        {
            for(var i=0;i<replacements.Count;)
            {
                chunk=replacements[i].Process(chunk);
                if(replacements[i].Done)replacements.RemoveAt(i);else i++;
            }
            return chunk;
        }
    }
}
