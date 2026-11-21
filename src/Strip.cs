// The strip: a per-pixel-alpha layered window owned by the taskbar (so it stays above it), docked next to the tray
// or at the taskbar's left edge. Drawn only when its content, position, DPI or theme changes.
using System.Runtime.InteropServices;
using static TaskbarGlance.Native;

namespace TaskbarGlance;

static unsafe class Strip
{
    public const uint WM_FETCHED = WM_APP + 1;   // posted by the weather's fetch thread
    public const uint WM_CALFETCHED = WM_APP + 2; // posted by the calendar's fetch thread
    public const uint WM_UPDATE = WM_APP + 3;    // posted by the update check or download
    public const int WeatherKind = 0, CalendarKind = 1; // segment kinds

    public static nint Hwnd;
    public static uint TaskbarCreatedMsg;
    static uint showMsg; // "TaskbarGlance.Show": posted by a second launch (Start menu shortcut) to bring this strip back
    static uint movedMsg;                        // "TaskbarGlance.Moved": tells the Claude strip to re-dock
    public static bool Fullscreen;
    public static bool? LightOverride;           // --render

    record struct Region(int X0, int X1, int Kind);
    /// <summary>One segment: optional icon, text, optional badge (1 = rain later today, 2 = thunder later today).
    /// The calendar is text only.</summary>
    record struct Item(int Kind, Sky? Icon, bool Night, string Text, int Badge);
    static Region[] regions = [];

    static nint taskbar, tray, hookLoc, hookFg, hookFgLoc, fgWin;
    static uint dpi = 96;
    static bool tracking, visible;
    static int hover = -1;
    static string lastSig = "";
    static int stripX, stripY, stripW, stripH;

    public static void Create()
    {
        var inst = GetModuleHandleW(0);
        var cls = (char*)Marshal.StringToHGlobalUni("TaskbarGlanceStrip");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = inst,
            hCursor = LoadCursorW(0, 32512), lpszClassName = cls,
        };
        var atom = RegisterClassExW(&wc);
        if (atom == 0) Log.Write($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
        Hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST, "TaskbarGlanceStrip", "Taskbar Glance", WS_POPUP, 0, 0, 1, 1, 0, 0, inst, 0);
        if (Hwnd == 0) Log.Write($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        TaskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        showMsg = RegisterWindowMessageW("TaskbarGlance.Show");
        movedMsg = RegisterWindowMessageW("TaskbarGlance.Moved");
        hookFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnWinEvent, 0, 0, WINEVENT_OUTOFCONTEXT | 0x2 /* SKIPOWNPROCESS */);
        AttachTaskbar();
        TrackForeground(GetForegroundWindow());
    }

    /// <summary>Starts over from the taskbar: after a display change, an Explorer restart or a second launch.</summary>
    public static void Reattach()
    {
        taskbar = 0; AttachTaskbar();
        Fullscreen = IsFullscreen();
        Refresh(force: true);
    }

    /// <summary>Back to the top of the topmost band, above the taskbar (no redraw).</summary>
    static void Raise()
    {
        if (visible) SetWindowPos(Hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Finds the taskbar and tray, makes the taskbar our owner and follows its tray's size changes.</summary>
    static bool AttachTaskbar()
    {
        var tb = FindWindowW("Shell_TrayWnd", null);
        if (tb == 0) { taskbar = tray = 0; return false; }
        if (tb != taskbar)
        {
            taskbar = tb;
            SetWindowLongPtrW(Hwnd, GWLP_HWNDPARENT, taskbar); // owned by the taskbar = always drawn above it
            if (hookLoc != 0) UnhookWinEvent(hookLoc);
            GetWindowThreadProcessId(taskbar, out var pid);
            hookLoc = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, &OnWinEvent, pid, 0, WINEVENT_OUTOFCONTEXT);
        }
        else if (GetWindow(Hwnd, 4 /* GW_OWNER */) != taskbar) SetWindowLongPtrW(Hwnd, GWLP_HWNDPARENT, taskbar); // owner was dropped: re-own
        tray = FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        return true;
    }

    [UnmanagedCallersOnly]
    static void OnWinEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev == EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject != 0 || hwnd == 0) return;
            if (hwnd == tray || hwnd == taskbar) SetTimer(Hwnd, Program.T_REDOCK, 120, 0);
            else if (hwnd == fgWin) SetTimer(Hwnd, Program.T_FSCHECK, 250, 0); // foreground window resized/fullscreened (e.g. Alt+Enter)
        }
        else if (ev == EVENT_SYSTEM_FOREGROUND)
        {
            TrackForeground(hwnd);
            var fs = IsFullscreen();
            if (fs != Fullscreen) { Fullscreen = fs; Refresh(); }
            else Raise();
            SetTimer(Hwnd, Program.T_REDOCK, 400, 0); // again once the Start menu or taskbar animation has finished
        }
    }

    /// <summary>Watches location changes of the foreground window's process only, so entering/leaving fullscreen in the
    /// same window (no foreground event) is noticed without a system-wide hook or polling.</summary>
    static void TrackForeground(nint hwnd)
    {
        if (hwnd == fgWin) return;
        fgWin = hwnd;
        if (hookFgLoc != 0) { UnhookWinEvent(hookFgLoc); hookFgLoc = 0; }
        if (hwnd == 0) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0) hookFgLoc = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, &OnWinEvent, pid, 0, WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Re-evaluates fullscreen after the foreground window changed size. Always forces a redraw and re-asserts the
    /// topmost position: a fullscreen window can push the strip below the taskbar even if our flag never flipped,
    /// and the shell state can lag, so a second check follows shortly after.</summary>
    public static void RecheckFullscreen(bool followUp = true)
    {
        Fullscreen = IsFullscreen();
        Refresh(force: true);
        if (followUp) SetTimer(Hwnd, Program.T_FSCHECK2, 700, 0);
    }

    public static bool IsFullscreen()
    {
        if (SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4) return true; // busy, D3D fullscreen, presentation
        // the shell state can lag or miss borderless fullscreen: compare the window with its monitor
        var fg = GetForegroundWindow();
        if (fg == 0 || fg == Hwnd || fg == taskbar) return false;
        char* cls = stackalloc char[64];
        int n = GetClassNameW(fg, cls, 64);
        var name = new string(cls, 0, n);
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (GetWindowRect(fg, out var r) == 0) return false;
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        var mon = MonitorFromWindow(fg, 2);
        if (mon == 0 || GetMonitorInfoW(mon, &mi) == 0) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // ---- content -----------------------------------------------------------------------------------------------

    /// <summary>Segments left to right.</summary>
    static List<Item> BuildItems()
    {
        var items = new List<Item>();
        if (Settings.ShowWeather)
        {
            if (Settings.City.Length == 0) items.Add(new Item(WeatherKind, Sky.PartlyCloudy, false, "Set city", 0));
            else if (Weather.NotFound) items.Add(new Item(WeatherKind, null, false, "City not found", 0));
            else if (Weather.Current() is { } c)
            {
                var w = Weather.Later();
                items.Add(new Item(WeatherKind, Weather.SkyOf(c.Code), !c.Day, Weather.Temp(c.Temp), w?.Thunder != null ? 2 : w != null ? 1 : 0));
            }
            else items.Add(new Item(WeatherKind, Sky.Cloudy, false, "–", 0)); // no reading yet, or it is too old to show
        }
        if (Calendar.Configured) items.Add(new Item(CalendarKind, null, false, Calendar.StripText(), 0));
        return items;
    }

    public static bool LightTheme() => LightOverride ?? RegDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 1;

    /// <summary>Re-reads position and content; redraws only if something visible changed.</summary>
    public static void Refresh(bool force = false)
    {
        AttachTaskbar(); // two lookups; also re-finds a replaced taskbar and re-owns the strip
        bool light = LightTheme();
        var items = BuildItems();
        if (taskbar == 0 || Fullscreen || items.Count == 0) { Hide(); return; }

        GetWindowRect(taskbar, out var tb);
        RECT tr = default;
        if (tray != 0) GetWindowRect(tray, out tr); else tr.Left = tb.Right;
        dpi = GetDpiForWindow(taskbar);
        if (dpi == 0) dpi = 96;

        var sig = $"{tb.Left},{tb.Top},{tb.Right},{tb.Bottom},{tr.Left},{dpi},{light},{Settings.OffsetX},{Settings.Left}|" +
                  string.Join(";", items.Select(i => $"{i.Kind}:{i.Icon}:{i.Night}:{i.Text}:{i.Badge}"));
        if (!force && sig == lastSig && visible) { Raise(); return; }
        lastSig = sig;

        Render(items, tb, tr, light);
        if (!visible) { ShowWindow(Hwnd, SW_SHOWNA); visible = true; NotifyNeighbors(); } // the Claude strip only docks past a visible strip
        SetWindowPos(Hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); // above the (topmost) taskbar
    }

    public static void Hide()
    {
        if (visible) { ShowWindow(Hwnd, SW_HIDE); visible = false; NotifyNeighbors(); }
        lastSig = "";
        Tooltip.Hide();
    }

    /// <summary>Our strip sits at the anchored edge and the Claude Taskbar strip (if it runs) steps outward past it.
    /// It finds our window by class name; this message tells it when our size or visibility changed.</summary>
    static void NotifyNeighbors()
    {
        var claude = FindWindowW("ClaudeTaskbarStrip", null);
        if (claude != 0 && movedMsg != 0) PostMessageW(claude, movedMsg, 0, 0);
    }

    // ---- drawing -----------------------------------------------------------------------------------------------

    static void Render(List<Item> items, RECT tb, RECT tr, bool light, string? saveTo = null)
    {
        float s = dpi / 96f;
        int H = tb.Height;
        int pad = R(8 * s), iconD = R(22 * s), gap = R(5 * s), groupGap = R(14 * s), badgeD = R(13 * s), badgeGap = R(5 * s);

        var font = CreateFontW(-R(12 * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4 /* ANTIALIASED_QUALITY: grayscale coverage */, 0, "Segoe UI");
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        var oldFont = SelectObject(dc, font);

        // layout, left to right
        var widths = new int[items.Count];
        var textW = new int[items.Count];
        int textH = 0, W = pad;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            GetTextExtentPoint32W(dc, it.Text, it.Text.Length, out var sz);
            textW[i] = sz.Cx; textH = Math.Max(textH, sz.Cy);
            widths[i] = (it.Icon != null ? iconD + (it.Text.Length > 0 ? gap : 0) : 0) + sz.Cx + (it.Badge != 0 ? badgeGap + badgeD : 0);
            W += widths[i] + (i < items.Count - 1 ? groupGap : 0);
        }
        W += pad;

        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(dc, &bmi, 0, out var bitsPtr, 0, 0);
        var oldBmp = SelectObject(dc, bmp);
        var px = (uint*)bitsPtr;

        // 1. all text as white-on-black coverage; remember where each label sits
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, 0xFFFFFF);
        var regs = new Region[items.Count];
        var spans = new (int x0, int x1)[items.Count];
        var starts = new int[items.Count];
        int x = pad, ty = (H - textH) / 2, cy = H / 2;
        uint fg = light ? 0x1A1A1Au : 0xFFFFFFu;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            starts[i] = x;
            int tx = x + (it.Icon != null ? iconD + (it.Text.Length > 0 ? gap : 0) : 0);
            spans[i] = (tx, tx + textW[i]);
            TextOutW(dc, tx, ty, it.Text, it.Text.Length);
            int x0 = x;
            x += widths[i];
            int g = i < items.Count - 1 ? groupGap : 0;
            regs[i] = new Region(x0 - (i == 0 ? pad : groupGap / 2), x + (i == items.Count - 1 ? pad : g / 2), it.Kind);
            x += g;
        }
        GdiFlush();
        var cov = new byte[W * H];
        for (int i = 0; i < W * H; i++)
        {
            uint c = px[i];
            cov[i] = (byte)Math.Max((int)(c & 0xFF), Math.Max((int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF)));
            px[i] = 0x01000000u; // alpha 1 keeps the whole strip clickable
        }

        // 2. icons and badges
        Gfx.Target(px, W, H);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Icon is { } sky) Icons.Draw(sky, it.Night, starts[i] + iconD / 2f, cy, iconD, light);
            if (it.Badge != 0) Icons.Badge(spans[i].x1 + badgeGap + badgeD / 2f, cy, badgeD, it.Badge == 2, light);
        }

        // 3. text. Segoe UI leaves more room above the digits than below, so every label is shifted until its ink
        // is centered on the icons' center line.
        foreach (var (a, b) in spans)
        {
            if (b <= a) continue;
            int lo = H, hi = -1;
            for (int yy = 0; yy < H; yy++)
                for (int xx = Math.Max(0, a - 1); xx < Math.Min(W, b + 1); xx++)
                    if (cov[yy * W + xx] != 0) { lo = Math.Min(lo, yy); hi = Math.Max(hi, yy); }
            if (hi < 0) continue;
            int dy = (int)MathF.Round(cy - (lo + hi + 1) / 2f);
            for (int yy = 0; yy < H; yy++)
            {
                int sy = yy - dy;
                if ((uint)sy >= (uint)H) continue;
                for (int xx = Math.Max(0, a - 1); xx < Math.Min(W, b + 1); xx++)
                {
                    int c = cov[sy * W + xx];
                    if (c != 0) Gfx.Over(xx, yy, fg, c / 255f);
                }
            }
        }

        if (saveTo != null) Dump(saveTo, px, W, H, opaque: false);
        else
        {
            stripW = W; stripH = H;
            // right: just left of the tray. left: from the taskbar's left edge (with centered icons that side is empty;
            // use offsetX to clear the Widgets button or another strip)
            stripX = Settings.Left ? tb.Left + R(8 * s) + R(Settings.OffsetX * s) : tr.Left - W - R(Settings.OffsetX * s) - R(2 * s);
            stripY = tb.Top;
            var dst = new POINT { X = stripX, Y = stripY };
            var size = new SIZE { Cx = W, Cy = H };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (UpdateLayeredWindow(Hwnd, 0, &dst, &size, dc, &src, 0, &blend, ULW_ALPHA) == 0) Log.Write($"UpdateLayeredWindow failed: {Marshal.GetLastPInvokeError()}");
            if (Log.Debug) Log.Write($"render: {W}x{H} at {stripX},{stripY} items={items.Count} taskbar={tb.Left},{tb.Top},{tb.Right},{tb.Bottom} tray={tr.Left} dpi={dpi}");
            regions = regs;
            NotifyNeighbors();
        }

        SelectObject(dc, oldBmp); SelectObject(dc, oldFont);
        DeleteObject(bmp); DeleteObject(font); DeleteDC(dc);
    }

    /// <summary>--render: raw BGRA after an 8-byte header (width, height); docs/render.py makes the PNGs. The strip is
    /// premultiplied with real alpha, the popup is opaque.</summary>
    public static void Dump(string path, uint* px, int w, int h, bool opaque)
    {
        if (opaque) for (int i = 0; i < w * h; i++) px[i] |= 0xFF000000u;
        using var f = File.Create(path);
        f.Write(BitConverter.GetBytes(w)); f.Write(BitConverter.GetBytes(h));
        f.Write(new ReadOnlySpan<byte>(px, w * h * 4));
    }

    /// <summary>Draws the strip for the loaded (made-up) data into a file, with no window.</summary>
    public static int RenderToFile(string path, uint renderDpi, int height)
    {
        dpi = renderDpi;
        var items = BuildItems();
        if (items.Count == 0) return 1;
        int h = R(height * dpi / 96f);
        Render(items, new RECT { Left = 0, Top = 0, Right = 2000, Bottom = h }, new RECT { Left = 2000 }, LightTheme(), saveTo: path);
        return 0;
    }

    /// <summary>The strip's content in words (tests).</summary>
    public static string Describe() =>
        string.Join(" | ", BuildItems().Select(i => $"{(i.Icon is { } s ? s + (i.Night ? " night" : " day") : "no icon")}, \"{i.Text}\", badge {i.Badge switch { 1 => "rain", 2 => "thunder", _ => "none" }}"));

    static int R(float v) => (int)MathF.Round(v);

    // ---- input -------------------------------------------------------------------------------------------------

    static int RegionAt(int x)
    {
        for (int i = 0; i < regions.Length; i++) if (x >= regions[i].X0 && x < regions[i].X1) return i;
        return -1;
    }

    /// <summary>Debug aid (GLANCE_DEBUG_TIP=weather or calendar): open that segment's popup without the mouse.</summary>
    public static void DebugTip(string key)
    {
        hover = Array.FindIndex(regions, r => r.Kind == (key == "weather" ? WeatherKind : key == "calendar" ? CalendarKind : -1));
        ShowHoverTip();
    }

    public static void ShowHoverTip()
    {
        KillTimer(Hwnd, Program.T_HOVER);
        if (hover < 0 || hover >= regions.Length || !visible) return;
        var r = regions[hover];
        Tooltip.Show(r.Kind, stripX + (r.X0 + r.X1) / 2, stripY, dpi);
    }

    static void Click(int x)
    {
        int r = RegionAt(x);
        int kind = r >= 0 ? regions[r].Kind : regions.Length > 0 ? regions[0].Kind : -1;
        if (kind == WeatherKind) Shell.OpenWeather();
        else if (kind == CalendarKind) Shell.OpenCalendar();
    }

    static void Menu()
    {
        const int Open = 1, Refresh_ = 2, OpenCal = 3, SetUpCal = 4, CheckUpdate = 5, Celsius = 10, Fahrenheit = 11, Auto = 40, Edit = 50, PosLeft = 60, PosRight = 61, Exit = 99;
        var pos = CreatePopupMenu();
        AppendMenuW(pos, Settings.Left ? MF_CHECKED : 0, PosLeft, "Left side of the taskbar");
        AppendMenuW(pos, Settings.Left ? 0 : MF_CHECKED, PosRight, "Right side, next to the tray");
        var units = CreatePopupMenu();
        AppendMenuW(units, Settings.Fahrenheit ? 0 : MF_CHECKED, Celsius, "Celsius (°C)");
        AppendMenuW(units, Settings.Fahrenheit ? MF_CHECKED : 0, Fahrenheit, "Fahrenheit (°F)");
        var m = CreatePopupMenu();
        if (Settings.ShowWeather) AppendMenuW(m, 0, Open, "Open forecast");
        if (Calendar.Configured) AppendMenuW(m, 0, OpenCal, "Open calendar");
        else if (Settings.ShowCalendar) AppendMenuW(m, 0, SetUpCal, "Add a calendar (how to)");
        AppendMenuW(m, Weather.Offline ? MF_GRAYED : 0, Refresh_, "Refresh now");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, MF_POPUP, (nuint)units, "Temperature");
        AppendMenuW(m, MF_POPUP, (nuint)pos, "Position");
        AppendMenuW(m, Shell.Autostart ? MF_CHECKED : 0, Auto, "Start with Windows");
        AppendMenuW(m, 0, Edit, "Open settings file");
        AppendMenuW(m, MF_SEPARATOR, 0, null);
        AppendMenuW(m, Update.Busy || Weather.Offline ? MF_GRAYED : 0, CheckUpdate, Update.Busy ? "Checking for updates..." : $"Check for updates (you have {Update.Current})");
        AppendMenuW(m, 0, Exit, "Exit");
        GetCursorPos(out var pt);
        SetForegroundWindow(Hwnd);
        int cmd = TrackPopupMenu(m, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, 0, Hwnd, 0);
        PostMessageW(Hwnd, WM_NULL, 0, 0);
        DestroyMenu(m); // also destroys the submenus
        switch (cmd)
        {
            case Open: Shell.OpenWeather(); break;
            case Refresh_: Weather.Fetch(); Calendar.Fetch(); break;
            case OpenCal: Shell.OpenCalendar(); break;
            case CheckUpdate: Update.Check(); break;
            case SetUpCal: Shell.OpenUrl("https://github.com/lucaslobasso/taskbar-glance#calendar"); break;
            case Celsius: Settings.Fahrenheit = false; Settings.Save(); Refresh(); break;
            case Fahrenheit: Settings.Fahrenheit = true; Settings.Save(); Refresh(); break;
            case PosLeft: Settings.Left = true; Settings.Save(); Refresh(force: true); break;
            case PosRight: Settings.Left = false; Settings.Save(); Refresh(force: true); break;
            case Auto: Shell.Autostart = !Shell.Autostart; break;
            case Edit: Shell.OpenFile(Paths.SettingsFile); break;
            case Exit: DestroyWindow(Hwnd); break;
        }
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint msg, nint w, nint l)
    {
        try
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
                case WM_MOUSEMOVE:
                {
                    if (!tracking)
                    {
                        var t = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = h };
                        TrackMouseEvent(&t); tracking = true;
                    }
                    int r = RegionAt((short)(l & 0xFFFF));
                    if (r != hover)
                    {
                        hover = r;
                        if (Tooltip.Visible) ShowHoverTip(); // already showing: switch at once
                        else if (r >= 0) SetTimer(h, Program.T_HOVER, 300, 0);
                    }
                    return 0;
                }
                case WM_MOUSELEAVE:
                    tracking = false; hover = -1; KillTimer(h, Program.T_HOVER); Tooltip.Hide();
                    return 0;
                case WM_LBUTTONUP: Tooltip.Hide(); Click((short)(l & 0xFFFF)); return 0;
                case WM_RBUTTONUP: Tooltip.Hide(); Menu(); return 0;
                case WM_TIMER: Program.OnTimer((nuint)w); return 0;
                case WM_FETCHED:
                    Weather.OnFetched();
                    Refresh();
                    Tooltip.RefreshIfOpen();
                    Program.ScheduleTrim();
                    return 0;
                case WM_CALFETCHED:
                    Calendar.OnFetched();
                    Refresh();
                    Tooltip.RefreshIfOpen();
                    Program.ScheduleTrim();
                    return 0;
                case WM_UPDATE: Update.OnDone(); return 0;
                case WM_POWERBROADCAST:
                    if (w is 0x12 or 0x7) { Weather.OnResume(); Calendar.OnResume(); Refresh(); } // resumed from sleep
                    return 1;
                case WM_TIMECHANGE: // the PC's clock or time zone was changed
                    TimeZoneInfo.ClearCachedData();
                    Calendar.OnResume();
                    Refresh();
                    return 0;
                case WM_DISPLAYCHANGE: // e.g. "second screen only": the taskbar moves to another monitor a moment later
                    Refresh(force: true); SetTimer(h, Program.T_SETTLE, 1500, 0); return 0;
                case WM_DPICHANGED:
                case WM_SETTINGCHANGE:
                    Refresh(force: true); return 0;
                case WM_DESTROY:
                    visible = false; NotifyNeighbors();
                    if (hookLoc != 0) UnhookWinEvent(hookLoc);
                    if (hookFg != 0) UnhookWinEvent(hookFg);
                    if (hookFgLoc != 0) UnhookWinEvent(hookFgLoc);
                    PostQuitMessage(0);
                    return 0;
            }
            if (msg == showMsg && msg != 0) { Reattach(); return 0; }
            if (msg == TaskbarCreatedMsg && msg != 0) // Explorer restarted
            {
                Reattach();
                return 0;
            }
        }
        catch { }
        return DefWindowProcW(h, msg, w, l);
    }
}
