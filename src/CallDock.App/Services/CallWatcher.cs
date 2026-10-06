using CallDock.App.ViewModels;
using CallDock.App.Views;
using CallDock.Core;
using System.Windows;

namespace CallDock.App.Services;

/// <summary>
/// What CallDock does about calls (setting «Звонки»): offer to record when an application starts using the microphone,
/// or start recording at once. A recording begun this way belongs to that call: when the application lets go of the
/// microphone, CallDock offers to finish — and finishes by itself a minute later, unless the person keeps it going.
/// Recordings started by hand are never stopped for them.
/// </summary>
public sealed class CallWatcher : IDisposable
{
    public const string Off = "off", Ask = "ask", Auto = "auto";

    private readonly AppHost host;
    private readonly RecorderViewModel recorder;
    private CallDetector? detector;
    /// <summary>The call the current recording was started for.</summary>
    private MicrophoneUser? call;
    /// <summary>Applications the person said «Не сейчас» to: not asked again until they let go of the microphone.</summary>
    private readonly HashSet<string> declined = [];
    private CallPrompt? prompt;
    private string? promptFor;

    public CallWatcher(AppHost host, RecorderViewModel recorder)
    {
        this.host = host;
        this.recorder = recorder;
        host.Recorder.Changed += () => Dispatch(() => { if (!host.Recorder.IsRecording) call = null; });
        Apply();
    }

    /// <summary>Follows the setting: the detector runs only when CallDock should react to calls.</summary>
    public void Apply()
    {
        if (host.Settings.CallDetection == Off) { detector?.Dispose(); detector = null; Withdraw(); return; }
        if (detector is not null) return;
        detector = new CallDetector();
        detector.Started += user => Dispatch(() => OnStarted(user));
        detector.Stopped += user => Dispatch(() => OnStopped(user));
    }

    private async void OnStarted(MicrophoneUser user)
    {
        if (call?.Id == user.Id && promptFor == user.Id) { Withdraw(); return; } // the call goes on: no need to finish
        if (host.Recorder.IsRecording || declined.Contains(user.Id) || prompt is not null) return;
        Log.Info($"Microphone in use by {user.Name}");
        if (host.Settings.CallDetection == Auto)
        {
            await StartAsync(user);
            if (call is not null) host.Notifier.Info("Запись звонка началась", $"{user.Name} использует микрофон. Запись завершится, когда звонок закончится.");
            return;
        }
        Show(user.Id, $"{user.Name} использует микрофон", "Похоже, начался звонок. Записать его? Источники — как на пульте CallDock.",
            "Записать", "Не сейчас", TimeSpan.FromSeconds(30), acceptOnTimeout: false, "",
            async () => await StartAsync(user), () => declined.Add(user.Id));
    }

    private void OnStopped(MicrophoneUser user)
    {
        declined.Remove(user.Id);
        if (promptFor == user.Id && call?.Id != user.Id) Withdraw(); // asked to record a call that is already over
        if (call?.Id != user.Id || !host.Recorder.IsRecording) return;
        Log.Info($"Microphone released by {user.Name}");
        Show(user.Id, $"{user.Name} больше не использует микрофон", "Похоже, звонок закончился.",
            "Завершить запись", "Продолжить", TimeSpan.FromSeconds(60), acceptOnTimeout: true, "Запись завершится сама через {0} с.",
            async () => await recorder.StopAsync(), () => call = null);
    }

    private async Task StartAsync(MicrophoneUser user)
    {
        if (host.Recorder.IsRecording) return;
        if (string.IsNullOrWhiteSpace(recorder.Title)) recorder.Title = $"Звонок: {user.Name} · {DateTime.Now:dd.MM HH:mm}";
        await recorder.StartAsync();
        if (host.Recorder.IsRecording) call = user;
    }

    private void Show(string forId, string title, string message, string accept, string decline, TimeSpan timeout, bool acceptOnTimeout,
        string countdown, Func<Task> onAccept, Action onDecline)
    {
        Withdraw();
        prompt = new CallPrompt(title, message, accept, decline, timeout, acceptOnTimeout, countdown);
        promptFor = forId;
        prompt.Accepted += async () => { Forget(); await onAccept(); };
        prompt.Declined += () => { Forget(); onDecline(); };
        prompt.Show(); // on top of every window, the call's own included: no second notice is needed
    }

    private void Forget() { prompt = null; promptFor = null; }

    private void Withdraw()
    {
        prompt?.Withdraw();
        Forget();
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        detector?.Dispose();
        Withdraw();
    }
}
