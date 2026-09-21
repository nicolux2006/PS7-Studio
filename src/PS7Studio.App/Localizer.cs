namespace PS7Studio.App;

internal static class L
{
    public static string Language
    {
        get => PS7Studio.Localization.Localizer.Language;
        set => PS7Studio.Localization.Localizer.Language = value;
    }

    public static string T(string key) => PS7Studio.Localization.Localizer.T(key);
}
