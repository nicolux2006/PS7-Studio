using System.Text;
namespace PS7Studio.Core;

public sealed class TextCoordinates
{
    public string Source {get;}
    public string Native {get;}
    public int[] LineStarts {get;}
    private readonly int[] removedSource;
    private readonly int[] removedNative;
    public TextCoordinates(string source)
    {
        Source=source;var text=new StringBuilder(source.Length);var removed=new List<int>();var nativeRemoved=new List<int>();var lines=new List<int>{0};
        for(var i=0;i<source.Length;i++)
        {
            if(source[i]=='\r')
            {
                text.Append('\r');lines.Add(text.Length);
                if(i+1<source.Length&&source[i+1]=='\n'){removed.Add(++i);nativeRemoved.Add(text.Length-1);}
            }
            else if(source[i]=='\n'){text.Append('\r');lines.Add(text.Length);}
            else text.Append(source[i]);
        }
        Native=text.ToString();removedSource=removed.ToArray();removedNative=nativeRemoved.ToArray();LineStarts=lines.ToArray();
    }
    private static int CountBefore(int[] values,int offset)
    {var low=0;var high=values.Length;while(low<high){var mid=(low+high)/2;if(values[mid]<offset)low=mid+1;else high=mid;}return low;}
    public int SourceToNative(int offset){offset=Math.Clamp(offset,0,Source.Length);return offset-CountBefore(removedSource,offset);}
    public int NativeToSource(int offset){offset=Math.Clamp(offset,0,Native.Length);return offset+CountBefore(removedNative,offset);}
    public int LineAtNative(int offset)=>Math.Max(1,CountBefore(LineStarts,offset+1));
    public string ApplyNativeEdit(string native,string newline)
    {
        var start=0;while(start<Native.Length&&start<native.Length&&Native[start]==native[start])start++;
        var endBefore=Native.Length;var endAfter=native.Length;
        while(endBefore>start&&endAfter>start&&Native[endBefore-1]==native[endAfter-1]){endBefore--;endAfter--;}
        return Source[..NativeToSource(start)]+native[start..endAfter].Replace("\r",newline)+Source[NativeToSource(endBefore)..];
    }
}
