using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

/// <summary>Edit Channels: a text row (a channel's name, a new group) saves on Enter or as it's left.</summary>
public partial class EditChannelsView : UserControl
{
    public EditChannelsView()
    {
        InitializeComponent();
    }

    private void TextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: EditTextRow row }) return;
        e.Handled = true;
        row.Commit();
    }

    private void TextLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: EditTextRow row }) row.Commit();
    }
}
