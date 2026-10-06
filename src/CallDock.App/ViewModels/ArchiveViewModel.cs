using CallDock.App.Services;
using CallDock.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace CallDock.App.ViewModels;

/// <summary>The archive: every recording by day, full-text search over titles, projects, tags, notes and what was said.</summary>
public sealed partial class ArchiveViewModel : ObservableObject
{
    private const int PageSize = 150;
    private readonly AppHost host;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public ArchiveViewModel(AppHost host)
    {
        this.host = host;
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Refresh(); };
        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); Refresh(); };
        host.Processing.Changed += () => Dispatch(OnProcessingChanged);
        host.Recorder.Changed += () => Dispatch(ScheduleRefresh);
    }

    public ObservableCollection<SessionItemViewModel> Items { get; } = [];
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial SessionItemViewModel? Selected { get; set; }
    [ObservableProperty] public partial SessionDetailViewModel? Detail { get; set; }
    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";

    partial void OnQueryChanged(string value) { searchTimer.Stop(); searchTimer.Start(); }

    partial void OnSelectedChanged(SessionItemViewModel? value)
    {
        Detail?.SaveDetails();
        if (value is null) { Detail = null; return; }
        var session = host.Archive.Get(value.Id) ?? value.Session;
        var detail = new SessionDetailViewModel(host, session);
        detail.Saved += saved => Items.FirstOrDefault(i => i.Id == saved.Id)?.Apply(saved);
        detail.Deleted += OnDeleted;
        detail.ApplyProcessing(host.Processing.State);
        Detail = detail;
    }

    public void ScheduleRefresh() { refreshTimer.Stop(); refreshTimer.Start(); }

    /// <summary>Reloads the list in place (rows are updated, added, moved and removed, never rebuilt), so the
    /// selection and the open recording stay put; the open detail is refreshed unless its fields are being edited.</summary>
    public void Refresh()
    {
        var results = host.Archive.Search(Query, limit: Math.Max(PageSize, Items.Count));
        var processing = host.Processing.State;
        var wanted = results.Select(s => s.Id).ToHashSet();
        foreach (var gone in Items.Where(i => !wanted.Contains(i.Id)).ToArray()) Items.Remove(gone);
        for (var index = 0; index < results.Count; index++)
        {
            var session = results[index];
            var status = processing.SessionId == session.Id ? Short(processing) : null;
            var at = IndexOf(session.Id);
            if (at < 0) Items.Insert(index, new SessionItemViewModel(session));
            else
            {
                if (at != index) Items.Move(at, index);
                Items[index].Apply(session, status);
            }
        }
        HasMore = results.Count >= PageSize && results.Count >= Items.Count;
        IsEmpty = Items.Count == 0;
        EmptyText = string.IsNullOrWhiteSpace(Query)
            ? "Здесь появятся ваши встречи и трансляции. Начните запись на странице «Запись»."
            : $"По запросу «{Query.Trim()}» ничего не нашлось. Поиск идёт по названию, проекту, тегам, заметкам и словам из разговора.";
        Summary = string.IsNullOrWhiteSpace(Query) ? $"Записей: {Items.Count}{(HasMore ? "+" : "")}" : $"Найдено: {Items.Count}{(HasMore ? "+" : "")}";
        if (Selected is not null && Detail is not null && host.Archive.Get(Selected.Id) is { } fresh
            && Changed(Detail.Session, fresh) && !IsEditing(Detail, fresh))
            Detail.Load(fresh);
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < Items.Count; i++) if (Items[i].Id == id) return i;
        return -1;
    }

    [RelayCommand]
    private void LoadMore()
    {
        foreach (var session in host.Archive.Search(Query, Items.Count, PageSize))
            if (Items.All(i => i.Id != session.Id)) Items.Add(new SessionItemViewModel(session));
        HasMore = Items.Count % PageSize == 0;
    }

    public void Select(string sessionId)
    {
        Query = "";
        Refresh();
        Selected = Items.FirstOrDefault(i => i.Id == sessionId);
    }

    private void OnDeleted(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        Selected = null;
        if (item is not null) Items.Remove(item);
        IsEmpty = Items.Count == 0;
    }

    private void OnProcessingChanged()
    {
        var state = host.Processing.State;
        Detail?.ApplyProcessing(state);
        if (state.Busy && Items.FirstOrDefault(i => i.Id == state.SessionId) is { } item)
            item.Apply(item.Session, Short(state));
        else ScheduleRefresh();
    }

    private static string Short(ProcessingState state) => state.Stage switch
    {
        ProcessingStage.Transcribe => $"Расшифровка {state.Fraction ?? 0:P0}",
        ProcessingStage.Mix => $"Сведение {state.Fraction ?? 0:P0}",
        ProcessingStage.Compress => "Сжатие…",
        _ => "Подготовка…"
    };

    /// <summary>Background work finished something worth showing (a transcript, a status, a new track).</summary>
    private static bool Changed(CallSession shown, CallSession stored) =>
        shown.Status != stored.Status || shown.TranscriptionStatus != stored.TranscriptionStatus
        || shown.Transcript.Count != stored.Transcript.Count || shown.AudioCompressed != stored.AudioCompressed || shown.TabsIndexed != stored.TabsIndexed
        || shown.Tracks.Count != stored.Tracks.Count || shown.Bookmarks.Count != stored.Bookmarks.Count
        || shown.Mix?.CreatedAt != stored.Mix?.CreatedAt || shown.MixError != stored.MixError
        || Math.Abs(shown.DurationSeconds - stored.DurationSeconds) > 1;

    /// <summary>Fields typed in the detail but not saved yet must not be overwritten by a refresh.</summary>
    private static bool IsEditing(SessionDetailViewModel detail, CallSession stored) =>
        detail.Title != stored.Title || detail.Project != stored.Project || detail.Tags != stored.Tags || detail.Notes != stored.Notes;

    public void Notify(string title, string message) => host.Notifier.Info(title, message);

    private static void Dispatch(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
}
