// One blocking HTTP GET through WinHTTP (Windows' own TLS and proxy settings). Called from a worker thread only.
// HttpClient would add several MB to the exe and its own thread pool; this adds nothing.
using static TaskbarGlance.Native;

namespace TaskbarGlance;

static unsafe class Net
{
    /// <summary>Returns the body of a 200 answer, or null on any failure.</summary>
    public static byte[]? Get(string url, out int status)
    {
        status = 0;
        if (!Split(url, out bool tls, out var host, out ushort port, out var path)) return null;
        nint s = 0, c = 0, r = 0;
        try
        {
            s = WinHttpOpen("TaskbarGlance", 4 /* automatic proxy */, 0, 0, 0);
            if (s == 0) return null;
            WinHttpSetTimeouts(s, 8000, 8000, 8000, 12000);
            c = WinHttpConnect(s, host, port, 0);
            if (c == 0) return null;
            r = WinHttpOpenRequest(c, "GET", path, null, null, 0, tls ? 0x00800000u /* WINHTTP_FLAG_SECURE */ : 0);
            if (r == 0) return null;
            uint gzip = 3; // gzip | deflate
            WinHttpSetOption(r, 118 /* WINHTTP_OPTION_DECOMPRESSION */, &gzip, 4);
            if (WinHttpSendRequest(r, 0, 0, 0, 0, 0, 0) == 0 || WinHttpReceiveResponse(r, 0) == 0) return null;
            uint code = 0, len = 4;
            WinHttpQueryHeaders(r, 19 | 0x20000000 /* status code, as a number */, 0, &code, ref len, 0);
            status = (int)code;
            if (code != 200) return null;
            using var ms = new MemoryStream();
            byte* buf = stackalloc byte[8192];
            while (true)
            {
                if (WinHttpReadData(r, buf, 8192, out uint n) == 0) return null;
                if (n == 0) break;
                ms.Write(new ReadOnlySpan<byte>(buf, (int)n));
                if (ms.Length > 4 * 1024 * 1024) return null; // nothing we ask for is this big
            }
            return ms.ToArray();
        }
        catch { return null; }
        finally
        {
            if (r != 0) WinHttpCloseHandle(r);
            if (c != 0) WinHttpCloseHandle(c);
            if (s != 0) WinHttpCloseHandle(s);
        }
    }

    static bool Split(string url, out bool tls, out string host, out ushort port, out string path)
    {
        tls = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        host = path = ""; port = 0;
        if (!tls && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = url[(tls ? 8 : 7)..];
        int slash = rest.IndexOfAny(['/', '?']);
        host = slash < 0 ? rest : rest[..slash];
        path = slash < 0 ? "/" : rest[slash] == '?' ? "/" + rest[slash..] : rest[slash..];
        port = (ushort)(tls ? 443 : 80);
        int colon = host.LastIndexOf(':');
        if (colon > 0 && ushort.TryParse(host[(colon + 1)..], out var p)) { port = p; host = host[..colon]; }
        return host.Length > 0;
    }
}
