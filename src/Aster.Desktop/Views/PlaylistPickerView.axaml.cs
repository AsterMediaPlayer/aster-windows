using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

public partial class PlaylistPickerView : UserControl
{
    public PlaylistPickerView()
    {
        InitializeComponent();
    }

    /// <summary>Ready for a new playlist's name as it shows.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameBox.Focus();
    }
}
