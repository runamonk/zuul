using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Zuul;

public partial class App
{
    internal static readonly string AppVersion =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev").Split('+')[0];
    private const string LatestReleaseUrl = "https://api.github.com/repos/runamonk/zuul/releases/latest";
    private const string UpdaterName = "ZuulUpdater.exe";
    private static readonly string UpdateFolder = Path.Combine(Path.GetTempPath(), "Zuul-update");
    private string? _updateZip;
    private bool _checkingForUpdates;

    private void InitializeUpdates()
    {
        // Left over from the last update. The updater may still be exiting, so this is best effort.
        try { if (Directory.Exists(UpdateFolder)) Directory.Delete(UpdateFolder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _ = CheckForUpdatesOnStartup();
    }

    private async void CheckForUpdates_Click()
    {
        try { await CheckForUpdates(); }
        catch (Exception e) { Tell(e.Message); }
    }

    // Quiet: only speaks up when there is an update. Dev builds never check.
    private async Task CheckForUpdatesOnStartup()
    {
        var updates = _settings.Updates ?? new();
        if (!updates.CheckOnStartup || !Version.TryParse(AppVersion, out _)) return;
        if (updates.LastCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(updates.EveryDays)) return;
        // Starting with Windows, the network may not be up yet.
        await Task.Delay(TimeSpan.FromSeconds(15));
        try { await CheckForUpdates(quiet: true); }
        catch (Exception e) { Trace.TraceWarning("Startup update check failed: " + e.Message); }
    }

    private async Task CheckForUpdates(bool quiet = false)
    {
        if (_updateZip != null) { await RestartToUpdate(); return; }
        if (!Version.TryParse(AppVersion, out var current))
        {
            Tell($"This is a development build ({AppVersion}), so it doesn't update itself.");
            return;
        }
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        if (!quiet) Toast.ShowMessage("Checking for updates…");
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Zuul/" + AppVersion);
            using var check = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var release = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseUrl, check.Token));
            RememberUpdateCheck();
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out var latest))
                throw new IOException($"The latest release has an unexpected version ({tag}).");
            if (latest <= current)
            {
                if (!quiet) Tell($"You're on the latest version ({AppVersion}).");
                return;
            }
            var asset = release.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString()?.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) == true);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new IOException($"Version {latest} has no Windows download.");
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new IOException($"Version {latest} has no checksum, so it can't be verified.");
            _toast?.Dismiss();
            if (!await Confirm("Update Zuul", $"Version {latest} is available. You have {AppVersion}.", "Download")) return;
            // Asked for now, so problems from here on are shown even when the check itself was a quiet one.
            try
            {
                var updater = Path.Combine(AppContext.BaseDirectory, UpdaterName);
                if (!File.Exists(updater))
                    throw new IOException($"{UpdaterName} is missing, so this copy can't update itself. Download version {latest} from GitHub instead.");
                Directory.CreateDirectory(UpdateFolder);
                var zip = Path.Combine(UpdateFolder, $"Zuul-{latest}.zip");
                await Download(http, asset.GetProperty("browser_download_url").GetString()!, zip, digest["sha256:".Length..], latest);
                // The updater runs from temp so nothing in the app folder is in use while it installs.
                File.Copy(updater, Path.Combine(UpdateFolder, UpdaterName), true);
                _updateZip = zip;
            }
            catch (Exception e)
            {
                Tell(e is OperationCanceledException ? "The download took too long. Try again later." : "Zuul couldn't be updated. " + e.Message);
                return;
            }
            _toast?.Dismiss();
            await RestartToUpdate();
        }
        catch (OperationCanceledException) { throw new IOException("GitHub didn't answer in time. Try again later."); }
        finally { _checkingForUpdates = false; }
    }

    private void RememberUpdateCheck()
    {
        _settings = _settings with { Updates = (_settings.Updates ?? new()) with { LastCheckUtc = DateTime.UtcNow } };
        try { _settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.TraceWarning("Could not remember the update check: " + e.Message); }
    }

    // Progress shows in the toast; without a size from GitHub there's no bar, just the line.
    private async Task Download(HttpClient http, string url, string zip, string sha256, Version version)
    {
        Toast.ShowMessage($"Downloading Zuul {version}…");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = File.Create(zip))
            {
                var buffer = new byte[81920];
                long received = 0;
                var shown = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (total <= 0) continue;
                    var percent = (int)(received * 100 / total);
                    if (percent == shown) continue;
                    shown = percent;
                    Toast.ShowMessage($"Downloading Zuul {version}… {percent}%", percent);
                }
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The download didn't match its checksum, so it was thrown away. Try again later.");
        }
        catch
        {
            try { File.Delete(zip); } catch (IOException) { }
            throw;
        }
    }

    private async Task RestartToUpdate()
    {
        if (await Confirm("Restart Zuul", "The update is ready. Restart Zuul now to install it? Choose Later to install it the next time Zuul quits.", "Restart", "Later"))
            Quit();
    }

    // Called by Quit, once nothing else needs the app folder.
    private void StartUpdater()
    {
        if (_updateZip == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(UpdateFolder, UpdaterName))
            {
                ArgumentList = { "--pid", Environment.ProcessId.ToString(), "--zip", _updateZip,
                    "--app", Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), "--from", AppVersion }
            });
        }
        catch (Exception e) { Trace.TraceWarning("Could not start the updater: " + e.Message); }
    }

    // Update news goes in a toast, not the launcher, which would vanish the moment anything else got focus.
    private ToastWindow? _toast;
    private ToastWindow Toast => _toast ??= new ToastWindow();

    private void Tell(string text) => Toast.ShowMessage(text, hideAfter: TimeSpan.FromSeconds(6));

    // No window to own it, so it sits on top instead.
    private async Task<bool> Confirm(string title, string description, string action, string cancelText = "Cancel")
    {
        var dialog = new Window
        {
            Title = title, Width = 450, SizeToContent = SizeToContent.Height, CanResize = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, FontSize = 13
        };
        var accept = new Button { Content = action, IsDefault = true, Classes = { "action", "primary" } };
        var cancel = new Button { Content = cancelText, IsCancel = true, Classes = { "action", "quiet" } };
        var result = false;
        accept.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            dialog.Close();
        }, RoutingStrategies.Tunnel);
        dialog.Content = new StackPanel { Margin = new Thickness(26), Spacing = 20, Children =
        {
            new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
        }};
        dialog.Opened += (_, _) => { AppThemes.ApplyTitleBar(dialog, _themes.Resolve(_settings)); accept.Focus(); };
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        dialog.Activate();
        await closed.Task;
        return result;
    }
}
