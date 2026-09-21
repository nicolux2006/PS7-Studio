using System.Globalization;
using System.Resources;

namespace PS7Studio.Localization;

public static class Localizer
{
    private static readonly ResourceManager Resources = new("PS7Studio.Localization.Strings", typeof(Localizer).Assembly);
    private static string language = "en";

    public static string Language
    {
        get => language;
        set => language = value;
    }

    public static string T(string key)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.GetCultureInfo("en");
        }

        return Resources.GetString(key, culture) ?? key;
    }
}
