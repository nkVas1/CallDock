using CallDock.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CallDock.App.Services;

public sealed class BrowserBridge(SessionController controller) : IAsyncDisposable
{
    private WebApplication? app;
    public const int Port = 47831;
    /// <summary>Tabs recorded at once. Each is a separate VP8 + Opus encode in Chrome; four is safe on an ordinary PC.</summary>
    public const int MaxTabs = 4;
    public bool IsRunning => app is not null;
    public string Token { get; } = LoadToken();
    private static string LoadToken()
    {
        var path = AppPaths.BrowserTokenFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var value = File.ReadAllText(path).Trim();
            if (value.Length == 64 && value.All(Uri.IsHexDigit)) return value;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(path, token);
        return token;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, Port); k.Limits.MaxRequestBodySize = 16 * 1024 * 1024; });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0)
            {
                if (origin != ExtensionOrigin) { context.Response.StatusCode = 403; return; }
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
            if (context.Request.Method == "OPTIONS") { context.Response.StatusCode = 204; return; }
            var provided = context.Request.Headers.Authorization.ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes("Bearer " + Token)))
            { context.Response.StatusCode = 401; return; }
            try { await next(context); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or ArgumentException)
            {
                if (!context.Response.HasStarted) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
            }
        });
        // "extension" is the version of the extension files shipped with this CallDock: Chrome keeps running an unpacked
        // extension's old code until it is reloaded, and the popup compares the two to ask for that reload.
        app.MapGet("/health", () => new
        {
            app = AppInfo.Name, version = AppInfo.Version, extension = ExtensionVersion,
            recording = controller.Current is not null, paused = controller.IsPaused
        });
        // «paused» in the answers below is the state of the whole recording: the extension pauses and resumes its tabs by it.
        app.MapPost("/tabs/start", async (TabStart request) =>
        {
            var title = request.Title.Trim();
            if (title.Length is 0 or > 240) throw new ArgumentException("Название вкладки должно содержать 1–240 символов.");
            var recording = await controller.AddBrowserAsync(title, request.Video, request.WithSources, request.CanPause);
            return Results.Json(new { id = recording.Track.Id, paused = controller.IsPaused });
        });
        app.MapPost("/recording/pause", async () => { await controller.PauseAsync(); return Results.Json(new { paused = controller.IsPaused }); });
        app.MapPost("/recording/resume", async () => { await controller.ResumeAsync(); return Results.Json(new { paused = controller.IsPaused }); });
        // Pause and resume reach the tabs the moment they happen: one JSON line per change, for as long as the extension
        // listens. The pings repeat the state, so a broken stream only delays a pause by a ping.
        app.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            using var changed = new SemaphoreSlim(0);
            void Wake() { try { if (changed.CurrentCount == 0) changed.Release(); } catch (ObjectDisposedException) { } }
            controller.Changed += Wake;
            try
            {
                bool? sent = null;
                while (!ended.IsCancellationRequested)
                {
                    var paused = controller.IsPaused;
                    if (paused != sent)
                    {
                        await context.Response.WriteAsync(paused ? "{\"paused\":true}\n" : "{\"paused\":false}\n", ended.Token);
                        await context.Response.Body.FlushAsync(ended.Token);
                        sent = paused;
                    }
                    await changed.WaitAsync(ended.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // the extension went away
            finally { controller.Changed -= Wake; }
        });
        app.MapPost("/tabs/{id}/started", async (string id, TabStarted started) =>
        {
            if (Find(id) is { } track) await controller.TabStartedAsync(track, started.At);
            return Results.Ok();
        });
        app.MapPost("/tabs/{id}/chunk/{sequence:long}", async (string id, long sequence, HttpContext context) =>
        {
            var track = Find(id) ?? throw new InvalidOperationException("Запись вкладки не найдена.");
            await track.AppendAsync(sequence, context.Request.Body, context.RequestAborted);
            return Results.Json(new { accepted = sequence, stop = track.StopRequested, paused = controller.IsPaused });
        });
        app.MapPost("/tabs/{id}/ping", (string id, TabMeter meter) =>
        {
            var track = Find(id);
            track?.UpdateMeter(meter.Peak);
            return Results.Json(new { stop = track is null || track.StopRequested || track.Closed, paused = controller.IsPaused });
        });
        app.MapPost("/tabs/{id}/finish", async (string id, TabFinish finish) =>
        {
            if (Find(id) is { } track)
            {
                await track.FinishAsync(finish.Error is { Length: > 500 } ? finish.Error[..500] : finish.Error);
                controller.TabFinished();
            }
            return Results.Ok();
        });
        try { await app.StartAsync(); }
        catch
        {
            await app.DisposeAsync();
            app = null;
            throw;
        }
        Log.Info($"Chrome bridge listening on 127.0.0.1:{Port}");
    }

    private static readonly string? ExtensionVersion = ReadExtensionVersion();

    private static string? ReadExtensionVersion()
    {
        try
        {
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "extension", "manifest.json")));
            return manifest.RootElement.GetProperty("version").GetString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException) { return null; }
    }

    private BrowserRecording? Find(string id) => controller.Recordings.OfType<BrowserRecording>().FirstOrDefault(x => x.Track.Id == id);
    public async ValueTask DisposeAsync() { if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; } }
    /// <summary>The CallDock extension's own ID: its manifest carries a fixed public key, so the ID is the same on every computer
    /// and in every Chromium browser. Other extensions and web pages are turned away even before the pairing code is checked.</summary>
    public const string ExtensionId = "annnbeocomannganbpakmlleibjjkfie";
    private const string ExtensionOrigin = "chrome-extension://" + ExtensionId;
    /// <summary>A tab asks to record; <paramref name="Video"/> is false when only its sound is recorded,
    /// <paramref name="WithSources"/> asks a new recording to start the sources switched on in CallDock as well, and
    /// <paramref name="CanPause"/> says the extension pauses its tabs with the recording (1.2 and later).</summary>
    public sealed record TabStart(string Title, bool Video = true, bool WithSources = false, bool CanPause = false);
    /// <summary>When the tab's recorder really started, by the wall clock (Unix milliseconds).</summary>
    public sealed record TabStarted(long At);
    public sealed record TabMeter(float Peak);
    public sealed record TabFinish(string? Error);
}
