using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace ALMARFELD.SubFlow.App.Services;

/// <summary>
/// UI localization (interface language only — unrelated to subtitle translation). Strings live in
/// Resources/Strings.resx; add Strings.&lt;culture&gt;.resx to support another interface language.
/// Missing keys fall back to English automatically.
/// </summary>
public static class L
{
    private static readonly ResourceManager Resources = new("ALMARFELD.SubFlow.App.Resources.Strings", typeof(L).Assembly);

    /// <summary>Interface languages shipped with the app (culture name, display name).</summary>
    public static readonly IReadOnlyList<(string Culture, string Name)> Available =
    [
        ("en", "English"),
        ("ro", "Română"),
    ];

    public static void SetCulture(string culture)
    {
        try
        {
            var ci = CultureInfo.GetCultureInfo(culture);
            CultureInfo.DefaultThreadCurrentUICulture = ci;
            CultureInfo.CurrentUICulture = ci;
        }
        catch (CultureNotFoundException) { /* keep the system default */ }
    }

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}

/// <summary>XAML: Text="{l:Loc Nav_Dashboard}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => L.Get(Key);
}
