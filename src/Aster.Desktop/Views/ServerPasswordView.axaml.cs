using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

public partial class ServerPasswordView : UserControl
{
    public ServerPasswordView()
    {
        InitializeComponent();
    }

    /// <summary>Ready for typing as it shows.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PasswordBox.Focus();
    }
}
