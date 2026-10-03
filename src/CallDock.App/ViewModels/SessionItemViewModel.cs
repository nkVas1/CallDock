using CallDock.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;

namespace CallDock.App.ViewModels;

public enum StatusTone { Neutral, Success, Busy, Warning, Error }

/// <summary>A row of the archive list.</summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public SessionItemViewModel(CallSession session) => Apply(session);

    public string Id { get; private set; } = "";
    public CallSession Session { get; private set; } = null!;
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial StatusTone Tone { get; set; }
    [ObservableProperty] public partial bool HasVideo { get; set; }
    [ObservableProperty] public partial string DayGroup { get; set; } = "";
    /// <summary>Sort key of the day group, newest first.</summary>
    public DateTime Day { get; private set; }

    public void Apply(CallSession session, string? processing = null)
    {
        Session = session;
        Id = session.Id;
        Title = session.Title;
        var local = session.StartedAt.ToLocalTime();
        var parts = new List<string> { local.ToString("HH:mm", Russian), session.DurationLabel };
        if (!string.IsNullOrWhiteSpace(session.Project)) parts.Add(session.Project);
        Subtitle = string.Join(" · ", parts);
        HasVideo = session.Tracks.Any(t => t.HasVideo);
        Day = local.Date;
        DayGroup = GroupName(local.Date);
        if (processing is not null) { StatusText = processing; Tone = StatusTone.Busy; return; }
        StatusText = session.StatusLabel;
        Tone = session.Status switch
        {
            SessionStatus.Recording => StatusTone.Error,
            SessionStatus.Interrupted or SessionStatus.Partial => StatusTone.Warning,
            _ => session.TranscriptionStatus switch
            {
                TranscriptionStatus.Done => session.Transcript.Count > 0 ? StatusTone.Success : StatusTone.Neutral,
                TranscriptionStatus.Failed => StatusTone.Error,
                TranscriptionStatus.Queued or TranscriptionStatus.Running => StatusTone.Busy,
                _ => StatusTone.Neutral
            }
        };
    }

    private static string GroupName(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "Сегодня";
        if (day == today.AddDays(-1)) return "Вчера";
        if (day > today.AddDays(-7)) return Capitalize(day.ToString("dddd, d MMMM", Russian));
        return day.Year == today.Year ? day.ToString("d MMMM", Russian) : day.ToString("d MMMM yyyy", Russian);
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Russian) + text[1..];
}
