using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Aster.Core.Jellyfin;
using Aster.Desktop.ViewModels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.Audio;

/// <summary>
/// Pauses the song or book after a while, or as the song or chapter playing
/// ends: from Now Playing's clock button. Its place is kept as with any
/// pause. Moving on by hand (Next, Previous, a chapter picked) doesn't count
/// as the end. Stopping, or choosing Off, ends it. Not in the Apple app.
/// </summary>
public sealed partial class SleepTimer : ObservableObject
{
    public static readonly IReadOnlyList<int> MinuteChoices = [15, 30, 45, 60];

    /// <summary>A song or chapter changing this soon after a move by hand isn't its end.</summary>
    private static readonly TimeSpan HandMoveGrace = TimeSpan.FromSeconds(3);

    private readonly AudioSession audio;
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>When the minutes run out.</summary>
    private DateTimeOffset ends;

    /// <summary>The song or chapter playing as "End of …" was set, or since the last move by hand.</summary>
    private object? watching;

    private DateTimeOffset movedByHand;

    public SleepTimer(AudioSession audio)
    {
        this.audio = audio;
        tick.Tick += (_, _) => Tick();
        audio.Music.PropertyChanged += PlayerChanged;
        audio.Books.PropertyChanged += PlayerChanged;
    }

    /// <summary>Minutes chosen (until <see cref="ends"/>), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSet), nameof(Label), nameof(Tip), nameof(Choices))]
    public partial int? Minutes { get; private set; }

    /// <summary>"End of Song" or "End of Chapter" chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSet), nameof(Label), nameof(Tip), nameof(Choices))]
    public partial bool AtEnd { get; private set; }

    public bool IsSet => Minutes is not null || AtEnd;

    private bool IsBook => audio.Books.Book is not null;

    private string EndTitle => IsBook ? "End of Chapter" : "End of Song";

    /// <summary>What the button says while set: the time left, or what it waits for.</summary>
    public string Label => Minutes is not null
        ? MusicFormat.Clock(Math.Max(0, (ends - DateTimeOffset.UtcNow).TotalSeconds))
        : AtEnd ? EndTitle : "";

    public string Tip => IsSet ? $"Sleep timer: pauses {(AtEnd ? $"at the {EndTitle.ToLowerInvariant()}" : $"in {Label}")}" : "Sleep timer";

    /// <summary>The button's menu, what's chosen ticked.</summary>
    public IReadOnlyList<SpeedChoice> Choices =>
    [
        new("Off", !IsSet, new RelayCommand(Cancel)),
        .. MinuteChoices.Select(m => new SpeedChoice($"{m} Minutes", Minutes == m, new RelayCommand(() => SetMinutes(m)))),
        new(EndTitle, AtEnd, new RelayCommand(SetAtEnd)),
    ];

    public void SetMinutes(int minutes)
    {
        AtEnd = false;
        ends = DateTimeOffset.UtcNow.AddMinutes(minutes);
        Minutes = minutes;
        tick.Start();
    }

    public void SetAtEnd()
    {
        Minutes = null;
        tick.Stop();
        watching = Playing();
        AtEnd = true;
    }

    public void Cancel()
    {
        tick.Stop();
        Minutes = null;
        AtEnd = false;
        watching = null;
    }

    /// <summary>Next, Previous or a chapter picked: the song or chapter that follows is the one to wait out.</summary>
    public void MovedByHand() => movedByHand = DateTimeOffset.UtcNow;

    private void Tick()
    {
        if (Minutes is null) return;
        if (DateTimeOffset.UtcNow >= ends) Fire();
        else
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Tip));
        }
    }

    /// <summary>What's playing, to tell when it changes: the song's place in the queue, or the book's chapter.</summary>
    private object? Playing() => IsBook ? audio.Books.CurrentChapter : (audio.Music.Index, audio.Music.Current?.Id);

    private void PlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsSet) return;
        // Stopped: nothing left to pause.
        if (!audio.IsLoaded)
        {
            Cancel();
            return;
        }
        if (!AtEnd) return;
        var now = Playing();
        if (Equals(now, watching)) return;
        watching = now;
        if (DateTimeOffset.UtcNow - movedByHand > HandMoveGrace) Fire();
    }

    private void Fire()
    {
        Cancel();
        audio.Pause();
    }
}
