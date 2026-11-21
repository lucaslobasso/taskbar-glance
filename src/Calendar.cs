// The calendar segment: the next event from one or more iCal addresses (Google Calendar's "secret address in iCal
// format"; no account, no OAuth). Each refresh downloads the calendars on a thread that lives for that refresh, keeps
// only the occurrences of the coming weeks and saves them, so a restart shows them at once.
// Times are the PC's local time (the weather, in contrast, uses the city's).
using System.Globalization;
using System.Text.Json;
using static TaskbarGlance.Native;

namespace TaskbarGlance;

/// <summary>An occurrence in the PC's local time. All day: Start is the day, End the day after the last one.</summary>
record struct Event(DateTime Start, DateTime End, bool AllDay, string Title);
record struct AgendaRow(DateTime Day, string Time, string Title);

static class Calendar
{
    public const string DefaultUrl = "https://calendar.google.com/calendar/r";
    const int BackDays = 32, AheadDays = 62;     // kept around today: the month grid looks back, the agenda ahead

    public static bool HasData { get; private set; }
    public static int Failures { get; private set; }    // refreshes in a row with a calendar that could not be read

    // --render: a fixed "now", a fixed zone and week start, so pictures and tests do not depend on the PC
    public static DateTime? NowOverride;
    public static TimeSpan? ZoneOverride;
    public static DayOfWeek? WeekStartOverride;

    static readonly CultureInfo inv = CultureInfo.InvariantCulture;
    static readonly Dictionary<string, List<Occ>> byCalendar = [];  // key: Id(address), or the file name when pretending
    static Event[] events = [];                                     // all calendars, local time, by start
    static DateTime fetchedUtc, cacheStamp;
    static string offlineSig = "";
    static bool pretend;                                            // data from .ics files (GLANCE_OFFLINE, --render)
    static bool busy;
    static volatile Result? result;

    sealed class Result { public string[] Urls = []; public List<Occ>?[] Lists = []; public int[] Status = []; }

    /// <summary>The segment is shown only once there is a calendar to show.</summary>
    public static bool Configured => Settings.ShowCalendar && (pretend || Settings.Calendars.Length > 0);
    public static DateTime Now => NowOverride ?? DateTime.Now;
    public static TimeSpan Age => NowOverride != null ? Weather.AgeOverride : DateTime.UtcNow - fetchedUtc;

    // ---- local time ---------------------------------------------------------------------------------------------

    public static DateTime LocalToUtc(DateTime wall)
    {
        if (ZoneOverride is { } o) return wall - o;
        var tz = TimeZoneInfo.Local;
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(wall)) wall = wall.AddHours(1); // inside the hour skipped in spring
        return TimeZoneInfo.ConvertTimeToUtc(wall, tz);
    }

    static DateTime ToLocal(DateTime utc) => ZoneOverride is { } o ? utc + o
        : DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local), DateTimeKind.Unspecified);

    /// <summary>First column of the month grid: Windows' own setting (Settings > Time & language > Region).</summary>
    public static DayOfWeek WeekStart
    {
        get
        {
            if (WeekStartOverride is { } w) return w;
            // LOCALE_IFIRSTDAYOFWEEK as a number: 0 = Monday ... 6 = Sunday
            return GetLocaleInfoEx(null, 0x100C | 0x20000000, out int first, 2) > 0 && first is >= 0 and <= 6 ? (DayOfWeek)((first + 1) % 7) : DayOfWeek.Monday;
        }
    }

    // ---- reading the data ---------------------------------------------------------------------------------------

    /// <summary>Merges the calendars that are (still) in the settings into one list in local time.</summary>
    static void Rebuild()
    {
        var list = new List<Event>();
        bool any = false;
        foreach (var (id, occs) in byCalendar)
        {
            if (!pretend && !Array.Exists(Settings.Calendars, u => Id(u) == id)) continue;
            any = true;
            foreach (var o in occs)
                list.Add(o.AllDay ? new Event(o.Start, o.End, true, o.Title) : new Event(ToLocal(o.Start), ToLocal(o.End), false, o.Title));
        }
        list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.AllDay != b.AllDay ? (a.AllDay ? -1 : 1)
            : a.End != b.End ? a.End.CompareTo(b.End) : string.CompareOrdinal(a.Title, b.Title));
        for (int i = list.Count - 1; i > 0; i--) if (list[i] == list[i - 1]) list.RemoveAt(i); // the same event in two calendars
        events = list.ToArray();
        HasData = any;
    }

    /// <summary>What the strip shows. Today comes first: the next event still to start today, else the one going on
    /// now, else today's all-day event; after that, the first event of the coming days.</summary>
    public static (Event Event, bool Ongoing)? Next()
    {
        var now = Now; var today = now.Date; var last = today.AddDays(Settings.CalendarDays);
        Event? ongoing = null, allDayToday = null, later = null;
        foreach (var e in events)
        {
            if (e.AllDay)
            {
                if (e.Start <= today && e.End > today) allDayToday ??= e;
                else if (e.Start > today && e.Start <= last) later ??= e;
            }
            else if (e.Start > now)
            {
                if (e.Start.Date == today) return (e, false);
                if (e.Start.Date <= last) later ??= e;
            }
            else if (e.End > now) ongoing = e; // of several, the one that started last
        }
        if (ongoing is { } o) return (o, true);
        if (allDayToday is { } a) return (a, false);
        return later is { } l ? (l, false) : null;
    }

    /// <summary>The icon instead of the next event: with "calendarStyle": "icon", always, or with
    /// "calendarIconFreeDays": N only while nothing is on today or the next N - 1 days (an ongoing event counts).
    /// With "calendarCutoffDay" (either style) also when the next event is after the next occurrence of that weekday
    /// (today counts). The icon wins if either rule says icon. Looks no further than "calendarDays".</summary>
    public static bool ShowsIcon()
    {
        bool free = Settings.CalendarIcon && Settings.CalendarIconFreeDays > 0;
        if (Settings.CalendarIcon && !free) return true;
        if (!free && Settings.CalendarCutoffDay is null) return false;
        if (!HasData) return Settings.CalendarIcon;
        if (Next() is not { } n) return true;
        if (n.Ongoing) return false;
        var today = Now.Date; var day = n.Event.Start.Date < today ? today : n.Event.Start.Date;
        if (free && (day - today).Days >= Settings.CalendarIconFreeDays) return true;
        return Settings.CalendarCutoffDay is { } c && (day - today).Days > ((int)c - (int)today.DayOfWeek + 7) % 7;
    }

    /// <summary>The segment's text. An event of today has no legend ("15:30 · Dentist"); otherwise it says when:
    /// "Tomorrow", the weekday while it is still this week ("Thu 10:00 · Standup"), the date after that
    /// ("Nov 26 · Thanksgiving"). "Now · Standup" while it is going on.</summary>
    public static string StripText()
    {
        if (!HasData) return Failures > 0 ? "–" : "…";
        if (Next() is not { } n) return "No events";
        var e = n.Event; var today = Now.Date;
        string title = Short(e.Title);
        if (n.Ongoing) return $"Now · {title}";
        var day = e.Start.Date < today ? today : e.Start.Date;
        int d = (day - today).Days;
        var nextWeek = today.AddDays(7 - ((int)today.DayOfWeek - (int)WeekStart + 7) % 7);
        string legend = d == 0 ? "" : d == 1 ? "Tomorrow" : day < nextWeek ? day.ToString("ddd", inv) : day.ToString("MMM d", inv);
        string when = e.AllDay ? legend : legend.Length > 0 ? $"{legend} {Weather.Clock(e.Start)}" : Weather.Clock(e.Start);
        return when.Length > 0 ? $"{when} · {title}" : title; // today's all-day event: just its name
    }

    static string Short(string title)
    {
        int max = Settings.CalendarTitleLength;
        return title.Length <= max ? title : title[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>The popup's list: what is left of today, then the coming days. more = rows that did not fit.</summary>
    public static List<AgendaRow> Agenda(out int more)
    {
        var now = Now; var today = now.Date; var last = today.AddDays(Settings.CalendarDays);
        var rows = new List<(DateTime Day, bool AllDay, DateTime Start, AgendaRow Row)>();
        foreach (var e in events)
        {
            if (e.AllDay ? e.End <= today : e.End <= now && e.Start <= now) continue; // over
            var day = e.Start.Date < today ? today : e.Start.Date;
            if (day > last) continue;
            string time;
            if (e.AllDay) time = "All day";
            else if (e.Start.Date < today) time = e.End.Date == today ? $"until {Weather.Clock(e.End)}" : "Ongoing";
            else if (e.End > e.Start && e.End <= e.Start.Date.AddDays(1)) time = $"{Weather.Clock(e.Start)} – {Weather.Clock(e.End)}";
            else time = Weather.Clock(e.Start);
            rows.Add((day, e.AllDay, e.Start, new AgendaRow(day, time, e.Title)));
        }
        rows.Sort((a, b) => a.Day != b.Day ? a.Day.CompareTo(b.Day) : a.AllDay != b.AllDay ? (a.AllDay ? -1 : 1) : a.Start.CompareTo(b.Start));
        int max = Settings.CalendarRows;
        more = Math.Max(0, rows.Count - max);
        var list = new List<AgendaRow>();
        for (int i = 0; i < rows.Count && i < max; i++) list.Add(rows[i].Row);
        return list;
    }

    public static string DayLabel(DateTime day)
    {
        int d = (day - Now.Date).Days;
        return d == 0 ? "Today" : d == 1 ? "Tomorrow" : day.ToString("ddd d MMM", inv);
    }

    /// <summary>For the dots of the month grid.</summary>
    public static bool HasEventOn(DateTime day)
    {
        var next = day.AddDays(1);
        foreach (var e in events)
        {
            if (e.Start >= next) break;
            if (e.AllDay ? e.End > day : e.End > day || e.Start >= day) return true;
        }
        return false;
    }

    /// <summary>One timer for the next moment the strip's text changes by itself: an event of today starts or ends,
    /// or the day turns. No polling in between.</summary>
    public static void ScheduleTick()
    {
        if (!Configured || NowOverride != null) { KillTimer(Strip.Hwnd, Program.T_CALTICK); return; }
        var now = Now; var next = now.Date.AddDays(1);
        foreach (var e in events)
        {
            if (e.AllDay) continue;
            if (e.Start > now && e.Start < next) next = e.Start;
            if (e.End > now && e.End < next) next = e.End;
        }
        SetTimer(Strip.Hwnd, Program.T_CALTICK, (uint)Math.Clamp((next - now).TotalMilliseconds + 1000, 1000, 86_500_000), 0);
    }

    // ---- saved copy ---------------------------------------------------------------------------------------------

    /// <summary>The address is a secret, so the saved file and the log only ever carry this fingerprint of it.</summary>
    static string Id(string url)
    {
        ulong h = 14695981039346656037;
        foreach (char c in url) { h ^= c; h *= 1099511628211; }
        return h.ToString("x16", inv);
    }

    static void SaveCache()
    {
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteStartArray("calendars");
                foreach (var url in Settings.Calendars)
                {
                    if (!byCalendar.TryGetValue(Id(url), out var occs)) continue;
                    w.WriteStartObject();
                    w.WriteString("id", Id(url));
                    w.WriteStartArray("events");
                    foreach (var o in occs)
                    {
                        string f = o.AllDay ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss'Z'";
                        w.WriteStartObject();
                        w.WriteString("s", o.Start.ToString(f, inv));
                        w.WriteString("e", o.End.ToString(f, inv));
                        w.WriteString("t", o.Title);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            var tmp = Paths.CalendarFile + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, Paths.CalendarFile, true);
            cacheStamp = File.GetLastWriteTimeUtc(Paths.CalendarFile);
        }
        catch { }
    }

    /// <summary>At startup: the saved occurrences. With GLANCE_OFFLINE=1: the .ics files in the folder instead, read
    /// again whenever one changes. Returns true if it loaded (new) data.</summary>
    public static bool LoadCache()
    {
        if (Weather.Offline) return LoadFiles();
        try
        {
            if (!File.Exists(Paths.CalendarFile)) return false;
            var t = File.GetLastWriteTimeUtc(Paths.CalendarFile);
            if (t == cacheStamp) return false;
            cacheStamp = t;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Paths.CalendarFile));
            byCalendar.Clear();
            foreach (var c in doc.RootElement.GetProperty("calendars").EnumerateArray())
            {
                if (Json.Str(c, "id") is not { } id) continue;
                var occs = new List<Occ>();
                foreach (var e in c.GetProperty("events").EnumerateArray())
                {
                    if (Json.Str(e, "s") is not { } s || Json.Str(e, "e") is not { } en) continue;
                    bool allDay = s.Length == 10;
                    string f = allDay ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss'Z'";
                    if (DateTime.TryParseExact(s, f, inv, DateTimeStyles.None, out var start) && DateTime.TryParseExact(en, f, inv, DateTimeStyles.None, out var end))
                        occs.Add(new Occ(start, end, allDay, Json.Str(e, "t") ?? ""));
                }
                byCalendar[id] = occs;
            }
            fetchedUtc = t;
            Rebuild();
            return true;
        }
        catch { return false; }
    }

    static bool LoadFiles()
    {
        try
        {
            var files = Directory.Exists(Paths.App) ? Directory.GetFiles(Paths.App, "*.ics") : [];
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            string sig = string.Join("|", files.Select(f => $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}"));
            if (sig == offlineSig) return false;
            offlineSig = sig;
            fetchedUtc = files.Length > 0 ? files.Max(f => File.GetLastWriteTimeUtc(f)) : default;
            return Pretend(files);
        }
        catch { return false; }
    }

    /// <summary>Takes the events from .ics files instead of the network (GLANCE_OFFLINE, --render). False if one of
    /// them is not a calendar.</summary>
    public static bool Pretend(string[] files)
    {
        var mid = NowOverride ?? DateTime.UtcNow;
        byCalendar.Clear();
        pretend = files.Length > 0;
        bool ok = true;
        foreach (var f in files)
        {
            if (Ical.Expand(File.ReadAllBytes(f), mid.AddDays(-BackDays), mid.AddDays(AheadDays)) is { } occs) byCalendar[f] = occs;
            else ok = false;
        }
        Rebuild();
        return ok;
    }

    /// <summary>--render: a calendar that is set but has no data yet (failed = it could not be reached).</summary>
    public static void PretendEmpty(bool failed)
    {
        pretend = true; Failures = failed ? 1 : 0;
        Rebuild();
    }

    // ---- fetching -----------------------------------------------------------------------------------------------

    /// <summary>Decides when the next refresh happens: now if a calendar has never been read or the data is old,
    /// otherwise one timer. Also called when the settings change.</summary>
    public static void Schedule(bool settingsChanged = false)
    {
        if (Weather.Offline) { ScheduleTick(); return; }
        if (settingsChanged) Failures = 0;
        Rebuild(); // a calendar may have been added or removed
        ScheduleTick();
        if (!Configured) { KillTimer(Strip.Hwnd, Program.T_CALREFRESH); return; }
        bool missing = Array.Exists(Settings.Calendars, u => !byCalendar.ContainsKey(Id(u)));
        var left = TimeSpan.FromMinutes(Settings.CalendarRefreshMinutes) - Age;
        if ((missing && Failures == 0) || left <= TimeSpan.Zero) Fetch();
        else if (Failures == 0) SetTimer(Strip.Hwnd, Program.T_CALREFRESH, (uint)Math.Max(1000, left.TotalMilliseconds), 0);
        // after a failure the retry timer is already running
    }

    /// <summary>After sleep (or a change of the PC's clock or zone) the timers and "today" are out of date.</summary>
    public static void OnResume()
    {
        if (!Configured) return;
        Rebuild();
        ScheduleTick();
        if (Weather.Offline) return;
        var left = TimeSpan.FromMinutes(Settings.CalendarRefreshMinutes) - Age;
        // wait a moment for the network first
        SetTimer(Strip.Hwnd, Program.T_CALREFRESH, left <= TimeSpan.Zero || Failures > 0 ? 9000 : (uint)Math.Max(1000, left.TotalMilliseconds), 0);
    }

    /// <summary>Downloads every calendar on one short-lived thread and expands it there; the UI thread is told by a
    /// window message when it is done.</summary>
    public static void Fetch()
    {
        KillTimer(Strip.Hwnd, Program.T_CALREFRESH);
        if (Weather.Offline || busy || !Settings.ShowCalendar || Settings.Calendars.Length == 0) return;
        busy = true;
        var urls = Settings.Calendars;
        nint hwnd = Strip.Hwnd;
        var from = DateTime.UtcNow.AddDays(-BackDays); var to = DateTime.UtcNow.AddDays(AheadDays);
        var th = new Thread(() =>
        {
            var res = new Result { Urls = urls, Lists = new List<Occ>?[urls.Length], Status = new int[urls.Length] };
            for (int i = 0; i < urls.Length; i++)
            {
                try
                {
                    var body = Net.Get(Address(urls[i]), out res.Status[i], 32 * 1024 * 1024);
                    if (body != null && (res.Lists[i] = Ical.Expand(body, from, to)) == null) res.Status[i] = -1;
                }
                catch (Exception e) { Log.Write($"calendar {i + 1}: {e.GetType().Name}"); } // never the message: it could quote the address
            }
            result = res;
            PostMessageW(hwnd, Strip.WM_CALFETCHED, 0, 0);
        }, 512 * 1024) { IsBackground = true };
        th.Start();
    }

    /// <summary>Google shows the address as https://; some apps hand it out as webcal://, which is the same thing.</summary>
    static string Address(string url) => url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[9..] : url;

    static readonly uint[] retryMs = [60_000, 300_000, 900_000];

    /// <summary>UI thread: takes the worker's answer, saves it and sets the next timer. A calendar that failed keeps
    /// what it had.</summary>
    public static void OnFetched()
    {
        var res = result; result = null; busy = false;
        if (res == null) return;
        if (!Enumerable.SequenceEqual(res.Urls, Settings.Calendars)) { Schedule(settingsChanged: true); return; } // the list changed meanwhile

        int failed = 0;
        for (int i = 0; i < res.Urls.Length; i++)
        {
            if (res.Lists[i] is { } list) byCalendar[Id(res.Urls[i])] = list;
            else
            {
                failed++;
                if (Failures == 0 || Log.Debug)
                    Log.Write(res.Status[i] == -1 ? $"calendar {i + 1}: the address did not return an iCal file" : $"calendar {i + 1}: fetch failed (http {res.Status[i]}); retrying");
            }
        }
        foreach (var id in byCalendar.Keys.ToArray()) // calendars taken out of the settings
            if (!Array.Exists(res.Urls, u => Id(u) == id)) byCalendar.Remove(id);
        if (failed < res.Urls.Length) { fetchedUtc = DateTime.UtcNow; SaveCache(); }
        Rebuild();
        if (failed == 0)
        {
            Failures = 0;
            if (Log.Debug) Log.Write($"calendar: {events.Length} events from {res.Urls.Length} calendar(s)");
            SetTimer(Strip.Hwnd, Program.T_CALREFRESH, (uint)Settings.CalendarRefreshMinutes * 60_000, 0);
        }
        else
        {
            Failures++;
            SetTimer(Strip.Hwnd, Program.T_CALREFRESH, retryMs[Math.Min(Failures, retryMs.Length) - 1], 0);
        }
        ScheduleTick();
    }
}
