// Win32 interop. Only what the app uses; all blittable so Native AOT needs no runtime marshalling tricks.
using System.Runtime.InteropServices;

namespace TaskbarGlance;

[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
[StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] struct SIZE { public int Cx, Cy; }
[StructLayout(LayoutKind.Sequential)] struct MSG { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public POINT Pt; }

[StructLayout(LayoutKind.Sequential)]
unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

[StructLayout(LayoutKind.Sequential)]
struct BITMAPINFOHEADER
{
    public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
}

[StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
[StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

static unsafe class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20;
    public const uint WM_DESTROY = 0x2, WM_PAINT = 0xF, WM_CLOSE = 0x10, WM_QUIT = 0x12, WM_SETTINGCHANGE = 0x1A, WM_MOUSEACTIVATE = 0x21,
        WM_DISPLAYCHANGE = 0x7E, WM_NCHITTEST = 0x84, WM_TIMER = 0x113, WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205,
        WM_MOUSELEAVE = 0x2A3, WM_DPICHANGED = 0x2E0, WM_APP = 0x8000, WM_USER = 0x400, WM_NULL = 0, WM_ERASEBKGND = 0x14, WM_POWERBROADCAST = 0x218;
    public const int MA_NOACTIVATE = 3;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_RESTORE = 9, SW_SHOWNA = 8;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const int GWLP_HWNDPARENT = -8;
    public const uint ULW_ALPHA = 2;
    public const uint TME_LEAVE = 2;
    public const uint QS_ALLINPUT = 0x4FF, MWMO_INPUTAVAILABLE = 0x4, INFINITE = 0xFFFFFFFF, WAIT_OBJECT_0 = 0;
    public const uint PM_REMOVE = 1;
    public const uint FILE_NOTIFY_CHANGE_FILE_NAME = 0x1, FILE_NOTIFY_CHANGE_DIR_NAME = 0x2, FILE_NOTIFY_CHANGE_LAST_WRITE = 0x10;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_GRAYED = 0x1;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;
    public const uint DT_SINGLELINE = 0x20, DT_END_ELLIPSIS = 0x8000, DT_NOPREFIX = 0x800, DT_VCENTER = 0x4, DT_LEFT = 0, DT_RIGHT = 0x2, DT_CALCRECT = 0x400;
    public const int TRANSPARENT = 1;
    public const uint RRF_RT_REG_DWORD = 0x10, RRF_RT_REG_SZ = 0x2;
    public const uint REG_SZ = 1;
    public static readonly nint HKEY_CURRENT_USER = unchecked((nint)(int)0x80000001);

    // user32
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateWindowExW(uint exStyle, string cls, string? name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int DestroyWindow(nint h);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int ShowWindow(nint h, int cmd);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int IsWindow(nint h);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint SetWindowLongPtrW(nint h, int idx, nint v);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? cls, string? name);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern nint FindWindowExW(nint parent, nint after, string? cls, string? name);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int SetProcessDpiAwarenessContext(nint ctx);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] public static extern int UpdateLayeredWindow(nint h, nint hdcDst, POINT* ptDst, SIZE* size, nint hdcSrc, POINT* ptSrc, uint key, BLENDFUNCTION* blend, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint GetDC(nint h);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int ReleaseDC(nint h, nint dc);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint BeginPaint(nint h, byte* ps);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int EndPaint(nint h, byte* ps);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int InvalidateRect(nint h, RECT* r, int erase);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nuint SetTimer(nint h, nuint id, uint ms, nint proc);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int KillTimer(nint h, nuint id);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int TrackMouseEvent(TRACKMOUSEEVENT* t);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int GetCursorPos(out POINT p);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int PeekMessageW(out MSG m, nint h, uint min, uint max, uint remove);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int TranslateMessage(in MSG m);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint DispatchMessageW(in MSG m);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int PostMessageW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint ms, uint wake, uint flags);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint SetWinEventHook(uint min, uint max, nint mod, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int UnhookWinEvent(nint hook);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint CreatePopupMenu();
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern int AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint h, nint rect);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int DestroyMenu(nint menu);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int SetForegroundWindow(nint h);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int GetClassNameW(nint h, char* buf, int max);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int GetMonitorInfoW(nint mon, MONITORINFO* mi);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern nint LoadCursorW(nint inst, nint id);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern int DrawTextW(nint dc, string text, int len, RECT* r, uint fmt);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int FillRect(nint dc, RECT* r, nint brush);

    // gdi32
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern int TextOutW(nint dc, int x, int y, string s, int len);
    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern int GetTextExtentPoint32W(nint dc, string s, int len, out SIZE size);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern int GdiFlush();
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    // kernel32
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern nint FindFirstChangeNotificationW(string path, int subtree, uint filter);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern int FindNextChangeNotification(nint h);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern int FindCloseChangeNotification(nint h);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern int CloseHandle(nint h);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern nint GetModuleHandleW(nint name);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern int SetProcessWorkingSetSize(nint proc, nint min, nint max);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern nint CreateMutexW(nint attrs, int owner, string name);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern uint GetLastError();

    // winhttp
    [DllImport("winhttp.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint WinHttpOpen(string agent, uint accessType, nint proxy, nint bypass, uint flags);
    [DllImport("winhttp.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint WinHttpConnect(nint session, string host, ushort port, uint reserved);
    [DllImport("winhttp.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint WinHttpOpenRequest(nint connect, string verb, string path, string? version, string? referrer, nint accept, uint flags);
    [DllImport("winhttp.dll", ExactSpelling = true)] public static extern int WinHttpSetTimeouts(nint h, int resolve, int connect, int send, int receive);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] public static extern int WinHttpSendRequest(nint req, nint headers, uint headersLen, nint optional, uint optionalLen, uint totalLen, nuint context);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] public static extern int WinHttpReceiveResponse(nint req, nint reserved);
    [DllImport("winhttp.dll", ExactSpelling = true)] public static extern int WinHttpQueryHeaders(nint req, uint level, nint name, void* buffer, ref uint size, nint index);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] public static extern int WinHttpReadData(nint req, void* buffer, uint toRead, out uint read);
    [DllImport("winhttp.dll", ExactSpelling = true)] public static extern int WinHttpCloseHandle(nint h);

    // shell32 / dwmapi / advapi32
    [DllImport("shell32.dll", ExactSpelling = true)] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern nint ShellExecuteW(nint h, string? verb, string file, string? args, string? dir, int show);
    [DllImport("dwmapi.dll", ExactSpelling = true)] public static extern int DwmSetWindowAttribute(nint h, uint attr, void* value, uint size);
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern int RegGetValueW(nint key, string sub, string? value, uint flags, out uint type, void* data, ref uint size);
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern int RegSetKeyValueW(nint key, string sub, string value, uint type, string data, uint size);
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] public static extern int RegDeleteKeyValueW(nint key, string sub, string value);

    public static uint Rgb(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16)); // COLORREF

    public static int RegDword(string sub, string value, int fallback)
    {
        uint data = 0, size = 4;
        return RegGetValueW(HKEY_CURRENT_USER, sub, value, RRF_RT_REG_DWORD, out _, &data, ref size) == 0 ? (int)data : fallback;
    }

    public static string? RegString(string sub, string value)
    {
        char* buf = stackalloc char[1024]; uint size = 2048;
        return RegGetValueW(HKEY_CURRENT_USER, sub, value, RRF_RT_REG_SZ, out _, buf, ref size) == 0 ? new string(buf) : null;
    }
}
