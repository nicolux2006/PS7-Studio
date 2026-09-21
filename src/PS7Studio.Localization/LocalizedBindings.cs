namespace PS7Studio.Localization;

/// <summary>Window-owned bindings; changing language preserves documents and live sessions.</summary>
public sealed class LocalizedBindings
{
    private readonly List<(string key,Action<string> set)> bindings=[];
    public void Add(string key,Action<string> set)
    {
        bindings.Add((key,set));set(Localizer.T(key));
    }
    public void Apply(string language)
    {
        Localizer.Language=language;
        foreach(var (key,set) in bindings)set(Localizer.T(key));
    }
}
