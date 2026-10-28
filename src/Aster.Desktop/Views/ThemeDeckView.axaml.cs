using System;
using System.ComponentModel;
using Aster.Desktop.Audio;
using Aster.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

/// <summary>
/// The Now Playing panel: takes the window full screen while the deck is,
/// and gives it back after. Full screen, Esc goes back to the panel, Space
/// pauses, and the arrows go to the one before or after; a double click on
/// the picture goes either way.
/// </summary>
public partial class ThemeDeckView : UserControl
{
    private ThemeDeck? deck;
    private TopLevel? top;
    private bool holdsFullScreen;

    public ThemeDeckView()
    {
        InitializeComponent();
        Video.DoubleTapped += (_, _) => deck?.ToggleFullScreenCommand.Execute(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (deck is not null) deck.PropertyChanged -= DeckChanged;
        deck = DataContext as ThemeDeck;
        if (deck is not null) deck.PropertyChanged += DeckChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        top = TopLevel.GetTopLevel(this);
        // Ahead of the page's own keys, but only while full screen.
        top?.AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        top?.RemoveHandler(KeyDownEvent, KeyPressed);
        LetGoOfFullScreen();
        top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void DeckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ThemeDeck.IsFullScreen) || deck is null) return;
        if (deck.IsFullScreen && !holdsFullScreen)
        {
            holdsFullScreen = true;
            FullScreen.Hold(top as Window, enter: true);
        }
        else if (!deck.IsFullScreen)
        {
            LetGoOfFullScreen();
        }
    }

    private void LetGoOfFullScreen()
    {
        if (!holdsFullScreen) return;
        holdsFullScreen = false;
        FullScreen.Release(top as Window);
    }

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        if (deck is not { IsFullScreen: true }) return;
        switch (e.Key)
        {
            case Key.Escape or Key.BrowserBack: deck.IsFullScreen = false; break;
            case Key.Space or Key.MediaPlayPause: deck.TogglePlaying(); break;
            case Key.Left: deck.Previous(); break;
            case Key.Right or Key.MediaNextTrack: deck.Next(); break;
            default: return;
        }
        e.Handled = true;
    }
}
