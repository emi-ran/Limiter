using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace Limiter;

public sealed class Localization : INotifyPropertyChanged
{
    public static Localization Current { get; } = new();
    private readonly Dictionary<string, Dictionary<string, string>> _strings;
    private readonly string _systemLanguage = CultureInfo.CurrentUICulture.Name;
    public string Language { get; private set; } = "en";
    private Localization()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Limiter.Strings.json")!;
        _strings = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
    }
    public static string ResolveLanguage(string preference, string systemLanguage)
        => preference is "tr" or "en" ? preference : systemLanguage.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";
    public void SetLanguage(string preference)
    {
        Language = ResolveLanguage(preference, _systemLanguage);
        var culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
    public string this[string key] => _strings.TryGetValue(key, out var entry) ? entry[Language] : key;
    public static string T(string key, params object[] args)
        => args.Length == 0 ? Current[key] : string.Format(CultureInfo.CurrentCulture, Current[key], args);
    public event PropertyChangedEventHandler? PropertyChanged;
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Localization.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
