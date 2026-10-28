using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

public partial class WelcomeView : UserControl
{
    private const double Gap = 16;
    private UniformGrid? pluginGrid;

    public WelcomeView()
    {
        InitializeComponent();
    }

    private void PluginGridLoaded(object? sender, RoutedEventArgs e)
    {
        pluginGrid = sender as UniformGrid;
        Arrange(Bounds.Width);
    }

    /// <summary>Cards side by side, as many as fit; one above the other in a narrow window.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Arrange(e.NewSize.Width);
    }

    private void Arrange(double width)
    {
        var room = Math.Min(width, 1400 + 96) - 96 + Gap;
        var columns = Columns(room, 300, 3);
        FeatureGrid.Columns = columns;
        if (pluginGrid is not null) pluginGrid.Columns = columns;
        NoteGrid.Columns = Columns(room, 420, 2);
    }

    private static int Columns(double room, double minimum, int most) => Math.Clamp((int)(room / (minimum + Gap)), 1, most);
}
