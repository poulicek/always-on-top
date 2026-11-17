using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AlwaysOnTop;

// Self-update from GitHub Releases.
// GET /repos/{Repository}/releases/latest returns the newest release that is neither a draft nor a pre-release. The
// release workflow publishes every version tag as a PRE-release, so nothing reaches installed copies until the release
// is promoted on GitHub ("Set as the latest release"). One small HTTPS request a minute after start and every 12 hours.
// Install: download the zip, verify its SHA-256 (the asset's "digest" field, else the .sha256 asset next to it), extract
// the exe next to the running one, rename the running exe to .old (Windows allows renaming a running image), move the
// new one into place, then the app exits and starts the new exe with --after-update <pid>; that instance waits for the
// old process to end and deletes the .old file.
internal sealed class Updater : IDisposable
{
    public const string Repository = "poulicek/always-on-top";
    public const string AssetName = "AlwaysOnTop-win-x64.zip";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1), Interval = TimeSpan.FromHours(12);

    private readonly System.Windows.Forms.Timer _timer = new();
    private bool _busy;

    public bool Enabled { get; set; }
    public event Action<string>? RestartRequested;          // new exe in place: exit and start it
    public event Action<string, ToolTipIcon>? Notify;       // result of a check the user asked for

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public Updater(bool enabled)
    {
        Enabled = enabled;
        _timer.Interval = (int)FirstCheck.TotalMilliseconds;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = (int)Interval.TotalMilliseconds;
            if (Enabled) await CheckAsync(manual: false);
        };
        _timer.Start();
    }

    // Removes the previous exe left behind by an update (it could not be deleted while it was running).
    public static void CleanUp()
    {
        try { File.Delete(ExePath + ".old"); } catch { /* still running or locked: next start */ }
        try { File.Delete(ExePath + ".new"); } catch { }
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AlwaysOnTop-Updater/{Current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { Done(manual, "No release has been published yet."); return; }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) throw new InvalidDataException($"unexpected tag '{tag}'");
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= Current) { Done(manual, $"You have the latest version ({Current})."); return; }

            string? url = null, digest = null, hashUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var download = asset.GetProperty("browser_download_url").GetString();
                if (name == AssetName)
                {
                    url = download;
                    if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String) digest = d.GetString();
                }
                else if (name == AssetName + ".sha256") hashUrl = download;
            }
            if (url == null) throw new InvalidDataException($"release {tag} has no {AssetName}");

            var expected = digest is { } dg && dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? dg[7..]
                         : hashUrl != null ? (await http.GetStringAsync(hashUrl)).Trim().Split(' ', '\t')[0]
                         : throw new InvalidDataException($"release {tag} has no checksum");

            Log.Write($"update: {Current} -> {latest}, downloading");
            var zip = await http.GetByteArrayAsync(url);
            var actual = Convert.ToHexString(SHA256.HashData(zip));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("checksum mismatch - update discarded");

            var exe = ExePath;
            var fresh = exe + ".new";
            using (var archive = new ZipArchive(new MemoryStream(zip)))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase))
                            ?? archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("no exe in the update");
                entry.ExtractToFile(fresh, overwrite: true);
            }

            File.Move(exe, exe + ".old", overwrite: true);
            try { File.Move(fresh, exe); }
            catch { File.Move(exe + ".old", exe); throw; }        // put the running version back

            Log.Write($"update: {latest} installed, restarting");
            RestartRequested?.Invoke(exe);
        }
        catch (Exception ex)
        {
            Log.Write("update failed: " + ex.Message);
            if (manual) Notify?.Invoke("Update failed: " + ex.Message, ToolTipIcon.Warning);
        }
        finally { _busy = false; }
    }

    private void Done(bool manual, string message)
    {
        Log.Write("update: " + message);
        if (manual) Notify?.Invoke(message, ToolTipIcon.Info);
    }

    // Started by the old instance just before it exits: wait until it is gone so the single-instance check passes.
    public static void WaitForPrevious(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var pid)) return;
        try { using var old = Process.GetProcessById(pid); old.WaitForExit(15000); }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex) { Log.Write("update: wait for previous failed: " + ex.Message); }
    }

    public void Dispose() => _timer.Dispose();
}
