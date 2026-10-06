using CallDock.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace CallDock.App.ViewModels;

/// <summary>One source card on the recording console: what it is, what it is called, how loud it is now.</summary>
public sealed partial class SourceItemViewModel : ObservableObject
{
    public SourceItemViewModel(SourceSpec spec, bool enabled = true)
    {
        Spec = spec;
        label = spec.DisplayName;
        Enabled = enabled;
    }

    public SourceSpec Spec { get; private set; }
    public string Device => Spec.Name;
    public bool IsAudio => Spec.HasAudio;
    public bool IsVideo => Spec.Kind is SourceKind.Screen or SourceKind.Window or SourceKind.Stream or SourceKind.BrowserTab;

    public string KindLabel => Spec.Kind switch
    {
        SourceKind.Microphone => "Микрофон",
        SourceKind.SystemAudio => "Системный звук",
        SourceKind.Application => "Звук приложения",
        SourceKind.Screen => "Экран",
        SourceKind.Window => "Окно",
        SourceKind.BrowserTab => "Вкладка Chrome",
        _ => "Поток по ссылке"
    };

    public SymbolRegular Icon => Spec.Kind switch
    {
        SourceKind.Microphone => SymbolRegular.Mic24,
        SourceKind.SystemAudio => SymbolRegular.Speaker224,
        SourceKind.Application => SymbolRegular.AppGeneric24,
        SourceKind.Screen => SymbolRegular.Desktop24,
        SourceKind.Window => SymbolRegular.Window24,
        SourceKind.BrowserTab => SymbolRegular.Globe24,
        _ => SymbolRegular.Live24
    };

    /// <summary>«Микрофон · Realtek Audio»; a tab is named by its title already, so it is only «Вкладка Chrome».</summary>
    public string Details => string.IsNullOrWhiteSpace(Device) || Spec.Kind == SourceKind.BrowserTab ? KindLabel : $"{KindLabel} · {Device}";

    private string label;
    /// <summary>The speaker name used for the track and in the transcript: «Я», «Собеседники», «Главная сцена».</summary>
    public string Label
    {
        get => label;
        set
        {
            var clean = value.Trim();
            if (!SetProperty(ref label, value)) return;
            Spec = Spec with { Label = clean.Length == 0 || clean == Spec.Name ? null : clean };
        }
    }

    [ObservableProperty] public partial bool Enabled { get; set; }
    /// <summary>The label and the remove button: not for a source that is being recorded (its track is named already).</summary>
    [ObservableProperty] public partial bool Editable { get; set; } = true;
    /// <summary>The switch: every source but a Chrome tab, which the extension controls.</summary>
    public bool CanToggle => Spec.Kind != SourceKind.BrowserTab;

    /// <summary>The person switched the source on or off: before a recording that chooses it, during one it joins or leaves.</summary>
    public event Action<SourceItemViewModel>? Toggled;
    private bool quiet;

    /// <summary>Sets the switch without raising <see cref="Toggled"/>: to undo a change that could not be carried out.</summary>
    public void SetEnabledQuietly(bool value)
    {
        quiet = true;
        try { Enabled = value; }
        finally { quiet = false; }
    }
    /// <summary>The level as a share of the meter (−60…0 dBFS mapped to 0…1).</summary>
    [ObservableProperty] public partial double Level { get; set; }
    [ObservableProperty] public partial double PeakHold { get; set; }
    [ObservableProperty] public partial string State { get; set; } = "Готов";
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial bool IsLive { get; set; }

    private DateTime peakAt;

    /// <summary>Applies a fresh peak (0…1 linear) from a recording or a sound check.</summary>
    public void Update(float peak, string? error, bool live, string? videoState = null)
    {
        IsAbsent = false;
        IsLive = live;
        HasError = error is not null;
        var level = peak > 0 ? Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1) : 0;
        Level = level;
        if (level >= PeakHold || DateTime.UtcNow - peakAt > TimeSpan.FromSeconds(1.5)) { PeakHold = level; peakAt = DateTime.UtcNow; }
        State = error ?? videoState ?? (!live ? "Готов"
            : peak >= 0.99 ? "Перегрузка — уменьшите громкость"
            : peak > 0.001 ? $"Сигнал · {20 * Math.Log10(peak):F0} дБ"
            : "Тишина");
    }

    /// <summary>A recording is going on without this source.</summary>
    public bool IsAbsent { get; private set; }

    public void Idle() => Rest(Enabled ? "Готов" : "Не записывается", absent: false);

    public void Absent() { if (!IsAbsent) Rest("Не участвует в этой записи", absent: true); }

    private void Rest(string state, bool absent)
    {
        IsAbsent = absent;
        IsLive = false;
        HasError = false;
        Level = 0;
        PeakHold = 0;
        State = state;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!IsLive) State = value ? "Готов" : "Не записывается";
        if (!quiet) Toggled?.Invoke(this);
    }
}
