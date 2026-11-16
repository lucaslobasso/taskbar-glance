// Hover popup above the strip: now, what is coming later today, and the forecast at the configured hours for the
// rest of today and tomorrow. Click-through, never activates.
using System.Runtime.InteropServices;
using static TaskbarGlance.Native;

namespace TaskbarGlance;

static unsafe class Tooltip
{
    enum Kind { Header, Row, Note, Warn }
    /// <summary>Row cells: day, time, temperature, chance of rain. Badge: 1 = rain, 2 = thunder (Warn lines).</summary>
    record struct Line(Kind Kind, string[] Cells, Sky? Icon = null, bool Night = false, int Badge = 0, bool Wet = false);

    static nint hwnd;
    static List<Line> lines = [];
    static int showing = -1;
    static int anchorX, anchorTop;
    static uint dpi = 96;
    static int[] colW = [];
    static int lineH, pad, gap, iconW, lead, width, height;
    static bool hasIcons;
    public static bool Visible { get; private set; }

    static void EnsureWindow()
    {
        if (hwnd != 0) return;
        var inst = GetModuleHandleW(0);
        var cls = (char*)Marshal.StringToHGlobalUni("TaskbarGlanceTip");
        var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = inst, lpszClassName = cls, hCursor = LoadCursorW(0, 32512) };
        RegisterClassExW(&wc);
        hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT, "TaskbarGlanceTip", null, WS_POPUP, 0, 0, 1, 1, Strip.Hwnd, 0, inst, 0);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, &round, 4);
    }

    public static void Show(int kind, int centerX, int stripTop, uint d)
    {
        EnsureWindow();
        showing = kind; anchorX = centerX; anchorTop = stripTop; dpi = d;
        Build();
        Place();
        SetTimer(Strip.Hwnd, Program.T_TIP, 60_000, 0); // keeps "updated n min ago" true while it stays open
    }

    public static void RefreshIfOpen()
    {
        if (!Visible) return;
        Build();
        if (lines.Count == 0) { Hide(); return; }
        Place();
    }

    public static void Hide()
    {
        if (!Visible) return;
        ShowWindow(hwnd, SW_HIDE);
        Visible = false;
        KillTimer(Strip.Hwnd, Program.T_TIP);
        Program.ScheduleTrim();
    }

    static void Build()
    {
        lines = [];
        if (showing != Strip.WeatherKind) return;
        if (Settings.City.Length == 0)
        {
            lines.Add(new Line(Kind.Header, ["No city set"]));
            lines.Add(new Line(Kind.Note, ["Click to open glance.json, fill in \"city\" and save."]));
            return;
        }
        if (Weather.NotFound)
        {
            lines.Add(new Line(Kind.Header, [$"City \"{Settings.City}\" not found"]));
            lines.Add(new Line(Kind.Note, ["Click to open glance.json and fix \"city\" (try \"Name, Country\")."]));
            return;
        }
        string place = Settings.Place.Length > 0 ? Settings.Place : Settings.City;
        if (Weather.Current() is not { } c)
        {
            lines.Add(new Line(Kind.Header, [place]));
            lines.Add(new Line(Kind.Note, [Weather.Failures > 0 ? "Can't reach the weather service; trying again soon."
                : Weather.HasData ? "The saved forecast is too old; waiting for an update." : "Loading..."]));
            return;
        }
        lines.Add(new Line(Kind.Header, [place, $"{Weather.Temp(c.Temp)} {Weather.Describe(c.Code)}"], Weather.SkyOf(c.Code), !c.Day)); // only the city is bold
        if (Weather.Later() is { } w)
        {
            string text = w is { Rain: { } r, Thunder: { } t } ? $"Rain from {Weather.Clock(r)}, thunderstorm from {Weather.Clock(t)}"
                : w.Thunder is { } t2 ? $"Thunderstorm from {Weather.Clock(t2)}" : $"Rain from {Weather.Clock(w.Rain!.Value)}";
            lines.Add(new Line(Kind.Warn, [text], Badge: w.Thunder != null ? 2 : 1));
        }
        string last = "";
        foreach (var (day, h) in Weather.Slots())
        {
            lines.Add(new Line(Kind.Row, [day == last ? "" : day, Weather.Clock(h.Time), Weather.Temp(h.Temp), $"{h.Pop}%"],
                Weather.SkyOf(h.Code), !h.Day, Wet: h.Pop >= Settings.RainWarnPercent));
            last = day;
        }
        lines.Add(new Line(Kind.Note, [Weather.AgeText() + (Weather.Failures > 0 ? " · can't reach the weather service" : "")]));
    }

    static (nint normal, nint bold) Fonts()
    {
        float s = dpi / 96f;
        int h = -(int)MathF.Round(12 * s);
        return (CreateFontW(h, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"), CreateFontW(h, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"));
    }

    /// <summary>Column widths and the popup's size for the current lines.</summary>
    static void Measure()
    {
        float s = dpi / 96f;
        pad = (int)(12 * s); gap = (int)(12 * s);
        var (fn, fb) = Fonts();
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        var old = SelectObject(dc, fn);

        colW = new int[4];
        int textH = 0;
        bool rows = false;
        hasIcons = false;
        foreach (var l in lines)
        {
            SelectObject(dc, l.Kind == Kind.Header ? fb : fn);
            for (int c = 0; c < l.Cells.Length; c++)
            {
                GetTextExtentPoint32W(dc, l.Cells[c], l.Cells[c].Length, out var sz);
                textH = Math.Max(textH, sz.Cy);
                if (l.Kind == Kind.Row && c < colW.Length) colW[c] = Math.Max(colW[c], sz.Cx);
            }
            if (l.Kind == Kind.Row) { rows = true; hasIcons |= l.Icon != null; }
        }
        lineH = Math.Max(textH + (int)(6 * s), (int)(23 * s));
        iconW = lineH; lead = iconW + (int)(4 * s);

        int spanW = 0;
        foreach (var l in lines)
        {
            if (l.Kind == Kind.Row) continue;
            SelectObject(dc, l.Kind == Kind.Header ? fb : fn);
            GetTextExtentPoint32W(dc, l.Cells[0], l.Cells[0].Length, out var sz);
            int tw = sz.Cx;
            if (l.Kind == Kind.Header && l.Cells.Length > 1)
            {
                SelectObject(dc, fn);
                GetTextExtentPoint32W(dc, l.Cells[1], l.Cells[1].Length, out var sz2);
                tw += (int)(7 * s) + sz2.Cx;
            }
            spanW = Math.Max(spanW, Math.Min(tw, (int)(520 * s)) + (l.Icon != null || l.Badge != 0 ? lead : 0));
        }
        SelectObject(dc, old); DeleteDC(dc); DeleteObject(fn); DeleteObject(fb);

        int rowsW = rows ? colW.Sum() + 3 * gap + (hasIcons ? iconW + gap : 0) : 0;
        width = pad * 2 + Math.Max(spanW, rowsW);
        height = pad * 2 + lines.Count * lineH;
    }

    static void Place()
    {
        Measure();
        float s = dpi / 96f;
        var mon = MonitorFromWindow(Strip.Hwnd, 2);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(mon, &mi);
        int x = Math.Clamp(anchorX - width / 2, mi.rcMonitor.Left + (int)(4 * s), mi.rcMonitor.Right - width - (int)(4 * s));
        int y = anchorTop - height - (int)(8 * s);
        if (y < mi.rcMonitor.Top) y = anchorTop + (int)(56 * s); // taskbar at the top (not standard on Windows 11)

        uint border = Strip.LightTheme() ? Rgb(0xD0, 0xD0, 0xD0) : Rgb(0x48, 0x48, 0x48);
        DwmSetWindowAttribute(hwnd, 34, &border, 4);
        SetWindowPos(hwnd, -1, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        InvalidateRect(hwnd, null, 0);
        Visible = true;
    }

    /// <summary>Draws the popup into a bitmap, then onto the window, or into a file for --render.</summary>
    static void Paint(nint dc, string? saveTo = null)
    {
        bool light = Strip.LightTheme();
        uint bgRgb = light ? 0xF9F9F9u : 0x2B2B2Bu;
        uint bg = Rgb((int)(bgRgb >> 16), (int)((bgRgb >> 8) & 0xFF), (int)(bgRgb & 0xFF));
        uint fg = light ? Rgb(0x1A, 0x1A, 0x1A) : Rgb(0xF2, 0xF2, 0xF2);
        uint dim = light ? Rgb(0x5C, 0x5C, 0x5C) : Rgb(0xA8, 0xA8, 0xA8);
        uint wetRgb = Icons.RainColor(light);
        uint wet = Rgb((int)(wetRgb >> 16), (int)((wetRgb >> 8) & 0xFF), (int)(wetRgb & 0xFF));
        float s = dpi / 96f;

        var mem = CreateCompatibleDC(dc);
        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bmp = CreateDIBSection(dc, &bmi, 0, out var bitsPtr, 0, 0);
        var px = (uint*)bitsPtr;
        var oldBmp = SelectObject(mem, bmp);
        var rc = new RECT { Right = width, Bottom = height };
        var brush = CreateSolidBrush(bg);
        FillRect(mem, &rc, brush);
        DeleteObject(brush);

        var (fn, fb) = Fonts();
        var oldFont = SelectObject(mem, fn);
        SetBkMode(mem, TRANSPARENT);
        const uint fmt = DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX | DT_VCENTER;
        var marks = new List<(Line line, float x, float y)>(); // icons and badges go on after the text
        int y = pad;
        foreach (var l in lines)
        {
            if (l.Kind == Kind.Row)
            {
                int x = pad;
                SelectObject(mem, fn);
                void Cell(int c, uint color, uint align)
                {
                    var cr = new RECT { Left = x, Top = y, Right = x + colW[c], Bottom = y + lineH };
                    SetTextColor(mem, color);
                    DrawTextW(mem, l.Cells[c], -1, &cr, fmt | align);
                    x += colW[c] + gap;
                }
                Cell(0, fg, DT_LEFT);
                Cell(1, fg, DT_LEFT);
                if (hasIcons)
                {
                    if (l.Icon != null) marks.Add((l, x + iconW / 2f, y + lineH / 2f));
                    x += iconW + gap;
                }
                Cell(2, fg, DT_RIGHT);
                Cell(3, l.Wet ? wet : dim, DT_RIGHT);
            }
            else
            {
                var r = new RECT { Left = pad, Top = y, Right = width - pad, Bottom = y + lineH };
                if (l.Icon != null || l.Badge != 0)
                {
                    marks.Add((l, pad + iconW / 2f, y + lineH / 2f));
                    r.Left += lead;
                }
                SelectObject(mem, l.Kind == Kind.Header ? fb : fn);
                SetTextColor(mem, l.Kind == Kind.Note ? dim : fg);
                DrawTextW(mem, l.Cells[0], -1, &r, fmt);
                if (l.Kind == Kind.Header && l.Cells.Length > 1)
                {
                    GetTextExtentPoint32W(mem, l.Cells[0], l.Cells[0].Length, out var w0);
                    r.Left += w0.Cx + (int)(7 * s);
                    SelectObject(mem, fn); // the temperature and the sky in the normal weight
                    DrawTextW(mem, l.Cells[1], -1, &r, fmt);
                }
            }
            y += lineH;
        }
        GdiFlush();
        Gfx.Target(px, width, height, bgRgb);
        foreach (var (l, mx, my) in marks)
        {
            if (l.Icon is { } sky) Icons.Draw(sky, l.Night, mx, my, lineH - (int)(3 * s), light);
            else Icons.Badge(mx, my, 13 * s, l.Badge == 2, light);
        }

        if (saveTo != null) Strip.Dump(saveTo, px, width, height, opaque: true);
        else Native.BitBlt(dc, 0, 0, width, height, mem, 0, 0, 0x00CC0020);
        SelectObject(mem, oldFont); SelectObject(mem, oldBmp);
        DeleteObject(fn); DeleteObject(fb); DeleteObject(bmp); DeleteDC(mem);
    }

    /// <summary>--render: the weather popup for the loaded (made-up) data, drawn into a file with no window.</summary>
    public static int RenderToFile(string path, uint renderDpi)
    {
        dpi = renderDpi; showing = Strip.WeatherKind;
        Build();
        if (lines.Count == 0) return 1;
        Measure();
        var screen = GetDC(0);
        Paint(screen, path);
        ReleaseDC(0, screen);
        return 0;
    }

    /// <summary>The weather popup's lines in words (tests).</summary>
    public static string Describe()
    {
        showing = Strip.WeatherKind;
        Build();
        return string.Join("\n", lines.Select(l => $"{l.Kind}: {string.Join(" | ", l.Cells)}" +
            (l.Icon is { } s ? $" [{s}{(l.Night ? " night" : " day")}]" : "") + (l.Badge != 0 ? $" [badge {(l.Badge == 2 ? "thunder" : "rain")}]" : "")));
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint msg, nint w, nint l)
    {
        try
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
                case WM_NCHITTEST: return -1; // HTTRANSPARENT
                case WM_ERASEBKGND: return 1;
                case WM_PAINT:
                {
                    byte* ps = stackalloc byte[80];
                    var dc = BeginPaint(h, ps);
                    Paint(dc);
                    EndPaint(h, ps);
                    return 0;
                }
            }
        }
        catch { }
        return DefWindowProcW(h, msg, w, l);
    }
}
