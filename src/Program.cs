// Taskbar Glance: the weather (and later the next calendar event) docked in the Windows 11 taskbar.
// One UI thread, event-driven: it sleeps in MsgWaitForMultipleObjectsEx until its folder changes, the mouse touches
// the strip, or a one-shot timer fires. Network: one small request per refresh, on a thread that lives for that request.
using System.Globalization;
using static TaskbarGlance.Native;

namespace TaskbarGlance;

static unsafe class Program
{
    public const nuint T_DEBOUNCE = 1, T_REFRESH = 2, T_HOVER = 4, T_TIP = 5, T_REDOCK = 6, T_TRIM = 8, T_FSCHECK = 9, T_FSCHECK2 = 10;

    static nint watch;

    static int Main(string[] args)
    {
        if (args is ["--render", _, _, ..]) { SetProcessDpiAwarenessContext(-4); return Render(args); } // pictures and tests, no window
        // a copy pointed at another folder (GLANCE_DIR) may run next to the installed one
        CreateMutexW(0, 0, @"Local\TaskbarGlance." + (Environment.GetEnvironmentVariable("GLANCE_DIR") is { Length: > 0 } ? "Custom" : "Singleton"));
        if (GetLastError() == 183 /* ERROR_ALREADY_EXISTS */) return 0;
        SetProcessDpiAwarenessContext(-4); // per-monitor v2 (also in the manifest)

        Settings.Load();
        Weather.LoadCache();
        Strip.Create();
        Strip.Fullscreen = Strip.IsFullscreen();
        Strip.Refresh(force: true);
        Log.Write($"start: hwnd={Strip.Hwnd} city=\"{Settings.City}\" data={Weather.HasData} fullscreen={Strip.Fullscreen}");
        ArmWatch();
        Weather.Schedule();
        ScheduleTrim();
        if (Environment.GetEnvironmentVariable("GLANCE_DEBUG_TIP") is { Length: > 0 } tip) Strip.DebugTip(tip);
        if (Environment.GetEnvironmentVariable("GLANCE_DEBUG_OPEN") == "settings") Shell.OpenFile(Paths.SettingsFile);

        while (true)
        {
            nint h = watch;
            uint n = h != 0 ? 1u : 0u;
            uint r = MsgWaitForMultipleObjectsEx(n, &h, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (r < n)
            {
                if (FindNextChangeNotification(watch) == 0) ArmWatch(); // folder vanished: re-arm
                SetTimer(Strip.Hwnd, T_DEBOUNCE, 300, 0);
            }
            else if (r == n)
            {
                while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE) != 0)
                {
                    if (msg.Message == WM_QUIT) { if (watch != 0) FindCloseChangeNotification(watch); return 0; }
                    TranslateMessage(msg);
                    DispatchMessageW(msg);
                }
            }
            else ArmWatch(); // WAIT_FAILED: the handle went bad
        }
    }

    /// <summary>Watches our own folder, so an edit of glance.json applies as soon as it is saved.</summary>
    static void ArmWatch()
    {
        if (watch != 0) FindCloseChangeNotification(watch);
        Directory.CreateDirectory(Paths.App);
        var h = FindFirstChangeNotificationW(Paths.App, 0, FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE);
        watch = h == -1 ? 0 : h;
    }

    /// <summary>Reloads whatever changed and redraws if needed.</summary>
    public static void Reload()
    {
        bool settings = Settings.Load();
        bool changed = settings;
        if (Weather.Offline) changed |= Weather.LoadCache();
        if (settings) Weather.Schedule(); // a new city or refresh time
        if (changed) { Strip.Refresh(force: true); Tooltip.RefreshIfOpen(); }
        ScheduleTrim();
    }

    /// <summary>A few seconds after activity: collect garbage and hand unused pages back to Windows.</summary>
    public static void ScheduleTrim() => SetTimer(Strip.Hwnd, T_TRIM, 3_000, 0);

    public static void OnTimer(nuint id)
    {
        switch (id)
        {
            case T_DEBOUNCE: KillTimer(Strip.Hwnd, T_DEBOUNCE); Reload(); break;
            case T_REFRESH: Weather.Fetch(); break; // stops the timer; the answer sets the next one
            case T_HOVER: Strip.ShowHoverTip(); break;
            case T_TIP: Tooltip.RefreshIfOpen(); break;
            case T_FSCHECK: KillTimer(Strip.Hwnd, T_FSCHECK); Strip.RecheckFullscreen(); break;
            case T_FSCHECK2: KillTimer(Strip.Hwnd, T_FSCHECK2); Strip.RecheckFullscreen(false); break;
            case T_REDOCK: KillTimer(Strip.Hwnd, T_REDOCK); Strip.Refresh(); break;
            case T_TRIM:
                KillTimer(Strip.Hwnd, T_TRIM);
                if (Tooltip.Visible) break;
                GC.Collect();
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
                break;
        }
    }

    /// <summary>Hidden helper for the README pictures and the tests: draws or describes the strip and its popup for
    /// made-up data, with no window, no settings file and no network.
    /// TaskbarGlance.exe --render strip|tip|text out [data=forecast.json] [now=2026-11-16T15:10] [age=12] [city=Name]
    /// [nocity] [notfound] [unit=F] [light] [warn=50] [hours=8,14,20] [dpi=144] [height=48]</summary>
    static int Render(string[] args)
    {
        var set = args.Skip(3).Select(a => a.Split('=', 2)).ToDictionary(a => a[0], a => a.Length > 1 ? a[1] : "", StringComparer.OrdinalIgnoreCase);
        string Get(string k, string d) => set.TryGetValue(k, out var v) ? v : d;
        var inv = CultureInfo.InvariantCulture;

        Settings.City = set.ContainsKey("nocity") ? "" : Get("city", "Buenos Aires");
        Settings.Place = Settings.ResolvedFor = Settings.City;
        Settings.Fahrenheit = Get("unit", "C").Equals("F", StringComparison.OrdinalIgnoreCase);
        Settings.RainWarnPercent = int.Parse(Get("warn", "50"), inv);
        Settings.ForecastHours = Get("hours", "8,14,20").Split(',').Select(h => int.Parse(h, inv)).ToArray();
        Strip.LightOverride = set.ContainsKey("light");
        Weather.Pretend(notFound: set.ContainsKey("notfound"));
        Weather.NowOverride = DateTime.Parse(Get("now", "2026-01-01T12:00"), inv);
        Weather.AgeOverride = TimeSpan.FromMinutes(double.Parse(Get("age", "5"), inv));
        if (Get("data", "") is { Length: > 0 } data && !Weather.Parse(File.ReadAllBytes(data))) return 2;

        uint dpi = uint.Parse(Get("dpi", "144"), inv);
        switch (args[1])
        {
            case "strip": return Strip.RenderToFile(args[2], dpi, int.Parse(Get("height", "48"), inv));
            case "tip": return Tooltip.RenderToFile(args[2], dpi);
            case "text": File.WriteAllText(args[2], $"strip: {Strip.Describe()}\n{Tooltip.Describe()}\n"); return 0;
            default: return 3;
        }
    }
}
