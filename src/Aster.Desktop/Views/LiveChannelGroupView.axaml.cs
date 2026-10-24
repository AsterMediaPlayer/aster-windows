using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Aster.Desktop.Views;

public partial class LiveChannelGroupView : UserControl
{
    public LiveChannelGroupView()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => (this.FindAncestorOfType<ShellView>()?.DataContext as ShellViewModel)?.Back();
    }
}
