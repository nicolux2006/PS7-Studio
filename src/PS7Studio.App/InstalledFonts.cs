using System.Runtime.InteropServices;

namespace PS7Studio.App;

internal static class InstalledFonts
{
    public static string[] GetFamilies()
    {
        var families=new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var dc=GetDC(0);
        if(dc!=0)
        {
            try
            {
                var query=new LogFont{CharSet=1,FaceName=""};
                FontCallback callback=(font,_,_,_)=>
                {
                    var name=Marshal.PtrToStructure<LogFont>(font).FaceName;
                    if(!string.IsNullOrWhiteSpace(name)&&!name.StartsWith('@'))families.Add(name);
                    return 1;
                };
                EnumFontFamiliesEx(dc,ref query,callback,0,0);
                GC.KeepAlive(callback);
            }
            finally{ReleaseDC(0,dc);}
        }
        if(families.Count==0)families.UnionWith(["Consolas","Courier New","Lucida Console"]);
        return families.OrderBy(x=>x,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    [StructLayout(LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height,Width,Escapement,Orientation,Weight;
        public byte Italic,Underline,StrikeOut,CharSet,OutPrecision,ClipPrecision,Quality,PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string FaceName;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int FontCallback(nint font,nint metrics,uint type,nint data);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window,nint dc);
    [DllImport("gdi32.dll",EntryPoint="EnumFontFamiliesExW",CharSet=CharSet.Unicode)] private static extern int EnumFontFamiliesEx(nint dc,ref LogFont query,FontCallback callback,nint data,uint flags);
}
