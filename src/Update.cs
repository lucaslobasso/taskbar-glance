// "Check for updates" in the right-click menu. Nothing here runs unless that entry is clicked: there is no background
// check. One request asks GitHub for the latest release; if it is newer and the user says yes, the exe and its
// published SHA-256 are downloaded, compared, and the running exe is swapped for the new one and restarted.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static TaskbarGlance.Native;

namespace TaskbarGlance;

static class Update
{
    const string Title = "Taskbar Glance", ExeName = "TaskbarGlance.exe";
    const string LatestUrl = "https://api.github.com/repos/lucaslobasso/taskbar-glance/releases/latest";

    /// <summary>GLANCE_UPDATE_URL: another "latest release" address (tests serve a made-up release from this PC).</summary>
    static readonly string latest = Environment.GetEnvironmentVariable("GLANCE_UPDATE_URL") is { Length: > 0 } u ? u : LatestUrl;

    public static readonly Version Current = typeof(Update).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);
    public static bool Busy { get; private set; }

    sealed class Result
    {
        public bool Install;                 // the answer to the download, not to the check
        public string? Error;
        public Version? Version; public string Tag = "";
        public string? ExeUrl, SumUrl;
    }
    static volatile Result? result;

    /// <summary>At startup: the previous exe, left behind by an update (it cannot delete itself while it runs).</summary>
    public static void Cleanup()
    {
        if (clean) return;
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
            clean = true;
        }
        catch { } // still in use: tried again at the next trim
    }
    static bool clean;

    /// <summary>Asks for the latest release on a thread that lives for that request.</summary>
    public static void Check()
    {
        if (Busy) return;
        Busy = true;
        nint hwnd = Strip.Hwnd;
        new Thread(() =>
        {
            var res = new Result();
            try
            {
                var body = Net.Get(latest, out int status);
                if (body == null) res.Error = $"http {status}";
                else
                {
                    using var doc = JsonDocument.Parse(body);
                    var r = doc.RootElement;
                    res.Tag = Json.Str(r, "tag_name") ?? "";
                    string number = res.Tag.TrimStart('v', 'V');
                    int cut = number.IndexOfAny(['-', '+']);
                    if (!Version.TryParse(cut < 0 ? number : number[..cut], out res.Version)) res.Error = "no version in the answer";
                    if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        foreach (var a in assets.EnumerateArray())
                        {
                            string? name = Json.Str(a, "name"), url = Json.Str(a, "browser_download_url");
                            if (name == ExeName) res.ExeUrl = url;
                            else if (name == ExeName + ".sha256") res.SumUrl = url;
                        }
                }
            }
            catch (Exception e) { res.Error = e.GetType().Name; }
            result = res;
            PostMessageW(hwnd, Strip.WM_UPDATE, 0, 0);
        }, 256 * 1024) { IsBackground = true }.Start();
    }

    /// <summary>UI thread: the check or the download finished.</summary>
    public static void OnDone()
    {
        var res = result; result = null;
        if (res == null) return;
        if (res.Install) { Busy = false; Finish(res); return; }

        if (res.Error != null || res.Version == null)
        {
            Busy = false;
            Log.Write($"update check failed: {res.Error}");
            Say("Could not check for updates. Try again later.", MB_ICONWARNING);
            return;
        }
        if (res.Version <= Current)
        {
            Busy = false;
            Say($"You have the latest version ({Current}).", MB_ICONINFORMATION);
            return;
        }
        if (res.ExeUrl == null || res.SumUrl == null)
        {
            Busy = false;
            Log.Write($"update: release {res.Tag} has no {ExeName} with a .sha256");
            Say($"Version {res.Version} is out, but its download is not ready yet. Try again in a few minutes.", MB_ICONWARNING);
            return;
        }
        if (MessageBoxW(0, $"Taskbar Glance {res.Version} is available (you have {Current}).\n\nInstall it now? The strip restarts by itself.", Title,
                MB_YESNO | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST) != IDYES) { Busy = false; return; }
        Download(res);
    }

    /// <summary>Downloads the exe and its SHA-256, checks one against the other and puts the new exe in place.</summary>
    static void Download(Result found)
    {
        nint hwnd = Strip.Hwnd;
        new Thread(() =>
        {
            var res = new Result { Install = true, Version = found.Version, Tag = found.Tag };
            try
            {
                var sum = Net.Get(found.SumUrl!, out int s1);
                var exe = sum == null ? null : Net.Get(found.ExeUrl!, out s1, 32 * 1024 * 1024);
                if (sum == null || exe == null) res.Error = $"download failed (http {s1})";
                else
                {
                    string want = Encoding.UTF8.GetString(sum).Trim('﻿', ' ', '\r', '\n', '\t').Split(' ', '\t', '\r', '\n')[0];
                    string got = Convert.ToHexString(SHA256.HashData(exe));
                    if (want.Length != 64 || !want.Equals(got, StringComparison.OrdinalIgnoreCase)) res.Error = "the download does not match its SHA-256";
                    else if (exe.Length < 100_000 || exe[0] != 'M' || exe[1] != 'Z') res.Error = "the download is not a program";
                    else Swap(exe);
                }
            }
            catch (Exception e) { res.Error = $"{e.GetType().Name}: {e.Message}"; }
            result = res;
            PostMessageW(hwnd, Strip.WM_UPDATE, 0, 0);
        }, 256 * 1024) { IsBackground = true }.Start();
    }

    /// <summary>Windows lets a running exe be renamed, so: ours becomes .old, the new one takes its name. If the second
    /// step fails, ours goes back.</summary>
    static void Swap(byte[] bytes)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no path");
        string fresh = exe + ".new", old = exe + ".old";
        File.WriteAllBytes(fresh, bytes);
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch { File.Move(old, exe); throw; }
    }

    static void Finish(Result res)
    {
        if (res.Error != null)
        {
            Log.Write($"update to {res.Tag} failed: {res.Error}");
            Say($"The update could not be installed ({res.Error}). Nothing was changed.", MB_ICONWARNING);
            return;
        }
        Log.Write($"updated {Current} -> {res.Version}; restarting");
        Program.ReleaseInstance(); // or the new copy would see us and leave
        ShellExecuteW(0, "open", Environment.ProcessPath!, null, null, 1);
        DestroyWindow(Strip.Hwnd);
    }

    static void Say(string text, uint icon) => MessageBoxW(0, text, Title, icon | MB_SETFOREGROUND | MB_TOPMOST);
}
