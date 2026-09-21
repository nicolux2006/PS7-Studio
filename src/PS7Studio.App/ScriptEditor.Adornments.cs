using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;

namespace PS7Studio.App;

internal sealed partial class ScriptEditor
{
    private readonly Canvas adornments=new(){IsHitTestVisible=false};
    private readonly Dictionary<int,int> bracketPairs=[];
    private bool showWhitespace;
    private void DrawLineAdornments(int line,int start,Windows.Foundation.Rect rect)
    {
        if(line==CurrentLine)
        {
            var highlight=new Border{Width=Math.Max(0,Box.ActualWidth-18),Height=Math.Max(rect.Height,Box.FontSize*1.2),Background=new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue),Opacity=.09};
            Canvas.SetTop(highlight,rect.Y+Box.Padding.Top);adornments.Children.Add(highlight);
        }
        if(!showWhitespace)return;
        var end=line<coordinates.LineStarts.Length?coordinates.LineStarts[line]-1:coordinates.Native.Length;
        for(var i=start;i<Math.Min(end,start+1500);i++)
        {
            if(coordinates.Native[i] is not (' ' or '\t'))continue;
            Box.Document.GetRange(i,i+1).GetRect(PointOptions.ClientCoordinates,out var position,out _);
            if(position.X<0||position.X>Box.ActualWidth)continue;
            var marker=new TextBlock{Text=coordinates.Native[i]=='\t'?"→":"·",FontFamily=Box.FontFamily,FontSize=Box.FontSize,Opacity=.35};
            Canvas.SetLeft(marker,position.X+Box.Padding.Left);Canvas.SetTop(marker,position.Y+Box.Padding.Top);adornments.Children.Add(marker);
            if(adornments.Children.Count>2500)break;
        }
    }
    private void DrawBracketMatch()
    {
        var source=SourceCursor;
        if(!bracketPairs.ContainsKey(source))source--;
        if(!bracketPairs.TryGetValue(source,out var other))return;
        foreach(var offset in new[]{source,other})
        {
            var native=coordinates.SourceToNative(offset);
            Box.Document.GetRange(native,native+1).GetRect(PointOptions.ClientCoordinates,out var rect,out _);
            if(rect.Y<0||rect.Y>Box.ActualHeight)continue;
            var border=new Border{Width=Math.Max(7,rect.Width),Height=Math.Max(12,rect.Height),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue)};
            Canvas.SetLeft(border,rect.X+Box.Padding.Left);Canvas.SetTop(border,rect.Y+Box.Padding.Top);adornments.Children.Add(border);
        }
    }
}
