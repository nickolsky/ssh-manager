using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace SshManager.Localization;

/// <summary>Bindable string table: <c>{Binding [Key], Source={x:Static l:Loc.Instance}}</c>.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Loc() => L.LanguageChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => L.Get(key);
}

/// <summary><c>{l:Tr Main.Connect}</c> — a translated string that follows language switches.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
