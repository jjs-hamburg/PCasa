using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace PCasa.App.Services;

/// <summary>
/// Single owner of user-visible translations. German is the invariant source
/// catalog; the English catalog is embedded and versioned with the app. No UI
/// subsystem may maintain its own language switch or independent dictionary.
/// Missing keys remain readable in German rather than becoming blank.
/// </summary>
public sealed class UiLanguage : INotifyPropertyChanged
{
    public static UiLanguage Current { get; } = new();

    private static readonly IReadOnlyDictionary<string, string> English =
        LoadEnglish();
    private string _code = "de";
    private int _revision;

    public string Code => _code;
    public int Revision => _revision;
    public event PropertyChangedEventHandler? PropertyChanged;

    public static string T(string germanSource) => Current.Translate(germanSource);

    public static string Format(string germanTemplate, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, T(germanTemplate), values);

    public string Translate(string germanSource)
    {
        if (_code == "en" && English.TryGetValue(germanSource, out var translated))
            return translated;
        return germanSource;
    }

    public static bool IsSupported(string? code) => code is "de" or "en";

    public void Select(string? code)
    {
        var normalized = code?.ToLowerInvariant();
        if (!IsSupported(normalized)) normalized = "de";
        if (normalized == _code) return;
        _code = normalized!;
        // The WPF binding used by every localizable XAML property depends
        // on this ONE notification; also works for already open dialogs.
        _revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
    }

    internal static IReadOnlyDictionary<string, string> Translations => English;

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        var resource = typeof(UiLanguage).Assembly.GetManifestResourceStream(
            "PCasa.App.Resources.english.json")
            ?? throw new InvalidOperationException("English UI resource missing.");
        using (resource)
            return JsonSerializer.Deserialize<Dictionary<string, string>>(resource)
                ?? throw new InvalidOperationException("English UI resource empty.");
    }
}

/// <summary>
/// Dynamic WPF localization, bound to UiLanguage.Revision. One markup extension
/// works for MenuItem.Header, TextBlock.Text, Button.Content, ToolTip, Title etc.
/// The source string is the stable German display key used by UiLanguage.T.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding(nameof(UiLanguage.Revision))
        {
            Source = UiLanguage.Current,
            Converter = Translator.Instance,
            ConverterParameter = Key,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
    }

    private sealed class Translator : IValueConverter
    {
        public static Translator Instance { get; } = new();
        public object Convert(object value, Type targetType, object parameter,
            CultureInfo culture) => UiLanguage.T((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter,
            CultureInfo culture) => Binding.DoNothing;
    }
}
