using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;

namespace Aster.Desktop.Controls;

public static class Converters
{
    /// <summary>A page dot: bright for the page showing, faint for the rest.</summary>
    public static readonly IValueConverter DotOpacity =
        new FuncValueConverter<bool, double>(current => current ? 0.95 : 0.35);

    /// <summary>Bold for the current one (a lyric line being sung), regular for the rest.</summary>
    /// <summary>True dims to 60%: "Watching" beside a channel's row, rather than an action.</summary>
    public static readonly IValueConverter DimWhen = new FuncValueConverter<bool, double>(dim => dim ? 0.6 : 1);

    /// <summary>True hides by opacity, keeping the place: an empty spot in the channel row.</summary>
    public static readonly IValueConverter HiddenWhen = new FuncValueConverter<bool, double>(hidden => hidden ? 0 : 1);

    public static readonly IValueConverter Bold =
        new FuncValueConverter<bool, Avalonia.Media.FontWeight>(current => current ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);

    /// <summary>Text in capitals, in the reader's language: a guide's group name over its first channel.</summary>
    public static readonly IValueConverter Upper = new FuncValueConverter<string?, string?>(text => text?.ToUpper(System.Globalization.CultureInfo.CurrentCulture));

    /// <summary>A switch's state on a settings row.</summary>
    public static readonly IValueConverter OnOff = new FuncValueConverter<bool, string>(on => on ? "On" : "Off");

    /// <summary>A disc menu's Back button: a step back, or closing the menu from its first screen.</summary>
    public static readonly IValueConverter BackTip = new FuncValueConverter<bool, string>(canGoBack => canGoBack ? "Back" : "Close");

    /// <summary>The template for a kind of card: <c>PosterCard</c>, <c>SquareCard</c> and so on, from Templates.axaml.</summary>
    public static readonly IValueConverter CardTemplate =
        new FuncValueConverter<CardStyle, IDataTemplate?>(style =>
            Application.Current?.TryGetResource($"{style}Card", null, out var template) == true ? template as IDataTemplate : null);
}
