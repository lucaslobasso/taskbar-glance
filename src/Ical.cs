// Reads iCalendar (.ics) data, the format behind a Google Calendar "secret address": events, their time zones and
// their repeat rules, expanded into the single occurrences that fall inside a window of days.
// Own code on purpose. Time zones come from the file's VTIMEZONE blocks: the app is built with invariant
// globalization, where .NET cannot look up names such as "America/New_York" on Windows. A zone the file only names
// is asked from Windows instead (see SystemZoneFor).
// Supported rules: FREQ=DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY, BYMONTH, BYSETPOS
// and WKST, plus EXDATE, RDATE and changed or cancelled single occurrences (RECURRENCE-ID).
using System.Globalization;
using System.Text;

namespace TaskbarGlance;

/// <summary>One occurrence of an event. With a time: Start and End in UTC. All day: dates, End being the day after
/// the last one.</summary>
record struct Occ(DateTime Start, DateTime End, bool AllDay, string Title);

static class Ical
{
    static readonly CultureInfo inv = CultureInfo.InvariantCulture;

    // ---- time zones ---------------------------------------------------------------------------------------------

    abstract class Zone { public abstract DateTime ToUtc(DateTime wall); }
    sealed class UtcZone : Zone { public override DateTime ToUtc(DateTime wall) => wall; }
    /// <summary>The PC's own zone: times written without any zone.</summary>
    sealed class LocalZone : Zone { public override DateTime ToUtc(DateTime wall) => Calendar.LocalToUtc(wall); }

    /// <summary>A zone Windows knows by that name (Outlook writes Windows names).</summary>
    sealed class SystemZone(TimeZoneInfo tz) : Zone
    {
        public override DateTime ToUtc(DateTime wall)
        {
            wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(wall)) wall = wall.AddHours(1); // inside the hour skipped in spring
            return TimeZoneInfo.ConvertTimeToUtc(wall, tz);
        }
    }

    /// <summary>One STANDARD or DAYLIGHT block: from Start on (and again every year, with a rule) the offset is To.</summary>
    sealed class Observance
    {
        public int From, To;               // minutes east of UTC
        public DateTime Start; public bool HasStart;
        public Rule? Rule;
        public List<DateTime> Extra = [];  // RDATE

        /// <summary>The last time this block took effect at or before the given wall time.</summary>
        public DateTime? LastOnset(DateTime wall, List<DateTime> buf)
        {
            if (wall < Start) return null;
            var best = Start;
            foreach (var d in Extra) if (d <= wall && d > best) best = d;
            if (Rule is { Freq: Rule.Yearly } r)
            {
                Span<int> years = [wall.Year, wall.Year - 1, r.Until?.Year ?? 0, (r.Until?.Year ?? 1) - 1];
                foreach (int y in years)
                {
                    if (y < Start.Year || (y - Start.Year) % r.Interval != 0) continue;
                    if (!r.Period(Start.Date, (y - Start.Year) / r.Interval, buf, out _)) continue;
                    foreach (var d in buf)
                    {
                        var t = d + Start.TimeOfDay;
                        if (t > wall || t <= best) continue;
                        if (r.Until is { } u && (r.UntilUtc ? t.AddMinutes(-From) : t) > u) continue;
                        best = t;
                    }
                }
            }
            return best;
        }
    }

    /// <summary>A zone described by the file itself (VTIMEZONE).</summary>
    sealed class FileZone : Zone
    {
        public List<Observance> Blocks = [];
        readonly List<DateTime> buf = [];

        public override DateTime ToUtc(DateTime wall)
        {
            if (Blocks.Count == 0) return wall;
            DateTime best = DateTime.MinValue; int offset = 0; bool found = false;
            Observance first = Blocks[0];
            foreach (var b in Blocks)
            {
                if (b.Start < first.Start) first = b;
                if (b.LastOnset(wall, buf) is { } t && (!found || t > best)) { best = t; offset = b.To; found = true; }
            }
            return wall.AddMinutes(-(found ? offset : first.From));
        }
    }

    /// <summary>A zone the file names but does not describe. Windows names work as they are; for the usual
    /// "Area/City" names Windows' own ICU gives the Windows name (.NET cannot here: invariant globalization).</summary>
    static unsafe Zone? SystemZoneFor(string id)
    {
        try { return new SystemZone(TimeZoneInfo.FindSystemTimeZoneById(id)); } catch { }
        try
        {
            char* buf = stackalloc char[128];
            int n = Native.ucal_getWindowsTimeZoneID(id, id.Length, buf, 128, out int status);
            if (status <= 0 && n is > 0 and < 128) return new SystemZone(TimeZoneInfo.FindSystemTimeZoneById(new string(buf, 0, n)));
        }
        catch { } // no icu.dll (before Windows 10 1903), or a name nobody knows
        return null;
    }

    // ---- repeat rules -------------------------------------------------------------------------------------------

    public sealed class Rule
    {
        public const int Daily = 0, Weekly = 1, Monthly = 2, Yearly = 3;
        public int Freq, Interval = 1, Count;
        public DateTime? Until; public bool UntilUtc, UntilDate;
        public DayOfWeek Wkst = DayOfWeek.Monday;
        public List<(int Ord, DayOfWeek Day)> ByDay = [];
        public List<int> ByMonthDay = [], ByMonth = [], BySetPos = [];

        /// <summary>Null for a rule this reader does not expand (hourly and faster): the event then counts once.</summary>
        public static Rule? Parse(string text)
        {
            var r = new Rule { Freq = -1 };
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string v = part[(eq + 1)..].Trim().ToUpperInvariant();
                switch (part[..eq].ToUpperInvariant())
                {
                    case "FREQ": r.Freq = v switch { "DAILY" => Daily, "WEEKLY" => Weekly, "MONTHLY" => Monthly, "YEARLY" => Yearly, _ => -1 }; break;
                    case "INTERVAL": if (int.TryParse(v, NumberStyles.None, inv, out int n) && n > 0) r.Interval = n; break;
                    case "COUNT": if (int.TryParse(v, NumberStyles.None, inv, out int c) && c > 0) r.Count = c; break;
                    case "UNTIL": if (ParseWall(v, out var u, out bool date)) { r.Until = u; r.UntilDate = date; r.UntilUtc = v.EndsWith('Z'); } break;
                    case "WKST": if (Day(v) is { } w) r.Wkst = w; break;
                    case "BYDAY":
                        foreach (var item in v.Split(',', StringSplitOptions.TrimEntries))
                        {
                            if (item.Length < 2 || Day(item[^2..]) is not { } wd) continue;
                            int ord = 0;
                            if (item.Length > 2 && !int.TryParse(item[..^2], NumberStyles.AllowLeadingSign, inv, out ord)) continue;
                            r.ByDay.Add((ord, wd));
                        }
                        break;
                    case "BYMONTHDAY": Ints(v, r.ByMonthDay, -31, 31); break;
                    case "BYMONTH": Ints(v, r.ByMonth, 1, 12); break;
                    case "BYSETPOS": Ints(v, r.BySetPos, -366, 366); break;
                }
            }
            return r.Freq >= 0 ? r : null;

            static void Ints(string v, List<int> into, int min, int max)
            {
                foreach (var item in v.Split(',', StringSplitOptions.TrimEntries))
                    if (int.TryParse(item, NumberStyles.AllowLeadingSign, inv, out int n) && n != 0 && n >= min && n <= max) into.Add(n);
            }
        }

        static DayOfWeek? Day(string two) => two switch
        {
            "SU" => DayOfWeek.Sunday, "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, _ => null,
        };

        DateTime WeekStart(DateTime d) => d.AddDays(-(((int)d.DayOfWeek - (int)Wkst + 7) % 7));
        bool MonthOk(DateTime d) => ByMonth.Count == 0 || ByMonth.Contains(d.Month);
        bool DayOk(DateTime d) { if (ByDay.Count == 0) return true; foreach (var (_, wd) in ByDay) if (wd == d.DayOfWeek) return true; return false; }
        bool MonthDayOk(DateTime d)
        {
            if (ByMonthDay.Count == 0) return true;
            int dim = DateTime.DaysInMonth(d.Year, d.Month);
            foreach (int md in ByMonthDay) if ((md > 0 ? md : dim + 1 + md) == d.Day) return true;
            return false;
        }

        /// <summary>How many whole periods (of Interval days, weeks, months or years) lie between the start and a date.</summary>
        public long PeriodsBetween(DateTime start, DateTime date) => Freq switch
        {
            Daily => (date - start).Days / Interval,
            Weekly => (date - WeekStart(start)).Days / (7 * Interval),
            Monthly => ((date.Year - start.Year) * 12 + date.Month - start.Month) / Interval,
            _ => (date.Year - start.Year) / Interval,
        };

        /// <summary>The dates the rule gives in its n-th period counted from the start's own (n = 0), in order.
        /// False when that period is beyond what a date can hold.</summary>
        public bool Period(DateTime start, long n, List<DateTime> buf, out DateTime periodStart)
        {
            buf.Clear(); periodStart = default;
            long k = n * Interval;
            if (k < 0) return false;
            switch (Freq)
            {
                case Daily:
                {
                    if (k > (DateTime.MaxValue - start).Days - 800) return false;
                    var d = periodStart = start.AddDays(k);
                    if (MonthOk(d) && MonthDayOk(d) && DayOk(d)) buf.Add(d);
                    break;
                }
                case Weekly:
                {
                    if (7 * k > (DateTime.MaxValue - start).Days - 800) return false;
                    var ws = periodStart = WeekStart(start).AddDays(7 * k);
                    if (ByDay.Count == 0) Add(ws.AddDays(((int)start.DayOfWeek - (int)Wkst + 7) % 7));
                    else foreach (var (_, wd) in ByDay) Add(ws.AddDays(((int)wd - (int)Wkst + 7) % 7));
                    void Add(DateTime d) { if (MonthOk(d)) buf.Add(d); }
                    break;
                }
                case Monthly:
                {
                    long m = (long)start.Year * 12 + start.Month - 1 + k;
                    int y = (int)(m / 12), mo = (int)(m % 12) + 1;
                    if (y > 9990) return false;
                    periodStart = new DateTime(y, mo, 1);
                    if (MonthOk(periodStart)) MonthDates(y, mo, start.Day, buf);
                    break;
                }
                default:
                {
                    long y = start.Year + k;
                    if (y > 9990) return false;
                    periodStart = new DateTime((int)y, 1, 1);
                    if (ByMonth.Count == 0 && ByDay.Count > 0 && ByMonthDay.Count == 0)
                        foreach (var (ord, wd) in ByDay) Weekdays(periodStart, periodStart.AddYears(1), ord, wd, buf); // "the 20th Monday of the year"
                    else if (ByMonth.Count > 0) foreach (int mo in ByMonth) MonthDates((int)y, mo, start.Day, buf);
                    else MonthDates((int)y, start.Month, start.Day, buf);
                    break;
                }
            }
            buf.Sort();
            for (int i = buf.Count - 1; i > 0; i--) if (buf[i] == buf[i - 1]) buf.RemoveAt(i);
            if (BySetPos.Count > 0 && buf.Count > 0)
            {
                var pick = new List<DateTime>();
                foreach (int p in BySetPos)
                {
                    int i = p > 0 ? p - 1 : buf.Count + p;
                    if (i >= 0 && i < buf.Count && !pick.Contains(buf[i])) pick.Add(buf[i]);
                }
                pick.Sort();
                buf.Clear(); buf.AddRange(pick);
            }
            return true;
        }

        void MonthDates(int y, int mo, int startDay, List<DateTime> buf)
        {
            int dim = DateTime.DaysInMonth(y, mo);
            var first = new DateTime(y, mo, 1);
            if (ByMonthDay.Count > 0)
            {
                foreach (int md in ByMonthDay)
                {
                    int d = md > 0 ? md : dim + 1 + md;
                    if (d < 1 || d > dim) continue;
                    var date = new DateTime(y, mo, d);
                    if (DayOk(date)) buf.Add(date);
                }
            }
            else if (ByDay.Count > 0) foreach (var (ord, wd) in ByDay) Weekdays(first, first.AddMonths(1), ord, wd, buf);
            else if (startDay <= dim) buf.Add(new DateTime(y, mo, startDay)); // "the 31st" skips the shorter months
        }

        /// <summary>Every such weekday in [from, to), or only the ord-th one (negative: counted from the end).</summary>
        static void Weekdays(DateTime from, DateTime to, int ord, DayOfWeek wd, List<DateTime> buf)
        {
            var firstOne = from.AddDays(((int)wd - (int)from.DayOfWeek + 7) % 7);
            var last = to.AddDays(-1);
            var lastOne = last.AddDays(-(((int)last.DayOfWeek - (int)wd + 7) % 7));
            if (ord == 0) { for (var d = firstOne; d < to; d = d.AddDays(7)) buf.Add(d); }
            else if (ord > 0) { var d = firstOne.AddDays(7 * (ord - 1)); if (d < to) buf.Add(d); }
            else { var d = lastOne.AddDays(7 * (ord + 1)); if (d >= from) buf.Add(d); }
        }
    }

    // ---- reading ------------------------------------------------------------------------------------------------

    record struct RawTime(string Value, string? Tzid);

    sealed class Ev
    {
        public string? Uid, Summary, Duration, Rrule;
        public RawTime? Start, End, RecurId;
        public List<RawTime> ExDates = [], RDates = [];
        public bool Cancelled;
    }

    /// <summary>Every occurrence that touches [fromUtc, toUtc). Null when the data is not an iCalendar file.</summary>
    public static List<Occ>? Expand(byte[] data, DateTime fromUtc, DateTime toUtc)
    {
        var events = new List<Ev>();
        var zones = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        Ev? ev = null; FileZone? fz = null; string? fzId = null; Observance? ob = null;
        bool calendar = false;

        foreach (var line in Lines(Encoding.UTF8.GetString(data)))
        {
            if (!Split(line, out var name, out var pars, out var value)) continue;
            if (name == "BEGIN")
            {
                string c = value.Trim().ToUpperInvariant();
                stack.Add(c);
                if (c == "VCALENDAR") calendar = true;
                else if (c == "VEVENT") ev = new Ev();
                else if (c == "VTIMEZONE") { fz = new FileZone(); fzId = null; }
                else if (fz != null && c is "STANDARD" or "DAYLIGHT") ob = new Observance();
                continue;
            }
            if (name == "END")
            {
                string c = value.Trim().ToUpperInvariant();
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                if (c == "VEVENT") { if (ev != null) events.Add(ev); ev = null; }
                else if (c == "VTIMEZONE") { if (fz != null && fzId != null) zones[fzId] = fz; fz = null; }
                else if (c is "STANDARD" or "DAYLIGHT") { if (ob is { HasStart: true } && fz != null) fz.Blocks.Add(ob); ob = null; }
                continue;
            }
            if (stack.Count == 0) continue;
            string top = stack[^1]; // a VALARM inside an event has its own SUMMARY and DESCRIPTION: only direct properties count
            if (top == "VEVENT" && ev != null)
            {
                switch (name)
                {
                    case "UID": ev.Uid = value.Trim(); break;
                    case "SUMMARY": ev.Summary = Unescape(value); break;
                    case "DTSTART": ev.Start = new RawTime(value.Trim(), Param(pars, "TZID")); break;
                    case "DTEND": ev.End = new RawTime(value.Trim(), Param(pars, "TZID")); break;
                    case "DURATION": ev.Duration = value; break;
                    case "RRULE": ev.Rrule = value; break;
                    case "RECURRENCE-ID": ev.RecurId = new RawTime(value.Trim(), Param(pars, "TZID")); break;
                    case "STATUS": ev.Cancelled = value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                    case "EXDATE":
                    case "RDATE":
                    {
                        string? tzid = Param(pars, "TZID");
                        foreach (var v in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (!v.Contains('/')) (name == "EXDATE" ? ev.ExDates : ev.RDates).Add(new RawTime(v, tzid)); // a period ("a/b") is not supported
                        break;
                    }
                }
            }
            else if (top == "VTIMEZONE" && fz != null) { if (name == "TZID") fzId = value.Trim(); }
            else if (ob != null && top is "STANDARD" or "DAYLIGHT")
            {
                switch (name)
                {
                    case "TZOFFSETFROM": ob.From = Offset(value); break;
                    case "TZOFFSETTO": ob.To = Offset(value); break;
                    case "DTSTART": if (ParseWall(value.Trim(), out var w, out _)) { ob.Start = w; ob.HasStart = true; } break;
                    case "RRULE": ob.Rule = Rule.Parse(value); break;
                    case "RDATE":
                        foreach (var v in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (ParseWall(v, out var extra, out _)) ob.Extra.Add(extra);
                        break;
                }
            }
        }
        if (!calendar) return null;

        Zone utc = new UtcZone(), local = new LocalZone();
        Zone ZoneFor(string id)
        {
            if (zones.TryGetValue(id, out var z)) return z;
            return zones[id] = SystemZoneFor(id) ?? local; // unknown name and no VTIMEZONE for it: read the time as the PC's own
        }
        bool Resolve(RawTime t, out DateTime wall, out Zone zone, out bool isDate)
        {
            zone = local;
            if (!ParseWall(t.Value, out wall, out isDate)) return false;
            if (isDate) return true;
            if (t.Value.EndsWith('Z') || t.Value.EndsWith('z')) zone = utc;
            else if (t.Tzid is { Length: > 0 } id) zone = ZoneFor(id);
            return true;
        }

        // single occurrences that were moved, renamed or cancelled: the repeating event leaves those out
        var changed = new Dictionary<string, HashSet<long>>();
        foreach (var e in events)
        {
            if (e.RecurId is not { } rid || e.Uid == null || !Resolve(rid, out var w, out var z, out bool date)) continue;
            if (!changed.TryGetValue(e.Uid, out var set)) changed[e.Uid] = set = [];
            set.Add(date ? w.Ticks : z.ToUtc(w).Ticks);
        }

        var result = new List<Occ>();
        var fromDate = fromUtc.Date.AddDays(-1); var toDate = toUtc.Date.AddDays(2); // all-day events have no zone: a day of slack
        foreach (var e in events)
        {
            if (e.Start is not { } rs || !Resolve(rs, out var start, out var zone, out bool allDay)) continue;
            TimeSpan dur;
            if (e.End is { } re && Resolve(re, out var endWall, out var endZone, out bool endDate) && endDate == allDay)
                dur = allDay ? endWall - start : endZone.ToUtc(endWall) - zone.ToUtc(start);
            else if (e.Duration != null) dur = ParseDuration(e.Duration);
            else dur = allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
            if (allDay && dur < TimeSpan.FromDays(1)) dur = TimeSpan.FromDays(1);
            if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;
            string title = e.Summary is { Length: > 0 } s ? s : "(No title)";

            void EmitUtc(DateTime sUtc) { var end = sUtc + dur; if (end >= fromUtc && sUtc < toUtc) result.Add(new Occ(sUtc, end, false, title)); }
            void EmitDay(DateTime day) { var end = day + dur; if (end > fromDate && day < toDate) result.Add(new Occ(day, end, true, title)); }
            void Emit(DateTime wall) { if (allDay) EmitDay(wall.Date); else EmitUtc(zone.ToUtc(wall)); }

            if (e.RecurId != null) { if (!e.Cancelled) Emit(start); continue; }
            if (e.Cancelled) continue;

            HashSet<long>? skip = null, skipDays = null;
            if (e.Uid != null && changed.TryGetValue(e.Uid, out var ch)) skip = new HashSet<long>(ch);
            foreach (var ex in e.ExDates)
            {
                if (!Resolve(ex, out var w, out var z, out bool date)) continue;
                if (allDay) (skip ??= []).Add(w.Date.Ticks);
                else if (date) (skipDays ??= []).Add(w.Ticks);
                else (skip ??= []).Add(z.ToUtc(w).Ticks);
            }
            bool Skipped(DateTime wall) =>
                (skip != null && skip.Contains(allDay ? wall.Date.Ticks : zone.ToUtc(wall).Ticks)) || (skipDays != null && skipDays.Contains(wall.Date.Ticks));

            var rule = e.Rrule != null ? Rule.Parse(e.Rrule) : null;
            if (rule == null) { if (!Skipped(start)) Emit(start); }
            else Recur(rule, start, allDay, zone, dur, fromUtc, toUtc, w => { if (!Skipped(w)) Emit(w); });
            foreach (var rd in e.RDates)
            {
                if (!Resolve(rd, out var w, out var z, out bool date)) continue;
                if (allDay) { if (w.Date != start.Date) EmitDay(w.Date); }
                else if (!date) { var u = z.ToUtc(w); if (u != zone.ToUtc(start)) EmitUtc(u); }
            }
        }
        return result;
    }

    /// <summary>Walks the rule period by period from the event's start (or from just before the window when no COUNT
    /// has to be kept) and reports each start, as a wall time in the event's own zone.</summary>
    static void Recur(Rule r, DateTime start, bool allDay, Zone zone, TimeSpan dur, DateTime fromUtc, DateTime toUtc, Action<DateTime> emit)
    {
        var d0 = start.Date; var tod = start.TimeOfDay;
        var lo = fromUtc.AddDays(-1) - dur; var hi = toUtc.AddDays(1); // wall times: a day of slack covers any zone
        long n = 0;
        if (r.Count == 0 && lo > start) n = Math.Max(0, r.PeriodsBetween(d0, lo.Date) - 1);
        int made = 0;
        var buf = new List<DateTime>();
        for (int guard = 0; guard < 100_000; guard++, n++)
        {
            if (!r.Period(d0, n, buf, out var periodStart) || periodStart > hi) return;
            foreach (var d in buf)
            {
                var wall = d + tod;
                if (wall < start) continue;
                if (r.Count > 0 && ++made > r.Count) return;
                if (r.Until is { } u && (r.UntilDate ? wall.Date > u.Date : (r.UntilUtc && !allDay ? zone.ToUtc(wall) : wall) > u)) return;
                if (wall > hi) return;
                if (wall >= lo) emit(wall);
            }
        }
    }

    // ---- text ---------------------------------------------------------------------------------------------------

    /// <summary>The file's lines with continuation lines (those starting with a space or tab) joined back.</summary>
    static IEnumerable<string> Lines(string text)
    {
        var sb = new StringBuilder();
        bool any = false;
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0, n = text.Length;
        while (i < n)
        {
            int e = text.IndexOf('\n', i);
            if (e < 0) e = n;
            int len = e - i;
            if (len > 0 && text[e - 1] == '\r') len--;
            if (len > 0 && text[i] is ' ' or '\t') sb.Append(text, i + 1, len - 1);
            else
            {
                if (any) yield return sb.ToString();
                sb.Clear(); sb.Append(text, i, len); any = true;
            }
            i = e + 1;
        }
        if (any) yield return sb.ToString();
    }

    /// <summary>NAME;PARAM=x;PARAM="y":value. A colon or semicolon inside quotes does not count.</summary>
    static bool Split(string line, out string name, out string pars, out string value)
    {
        name = pars = value = "";
        int semi = -1; bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c == ';' && semi < 0) semi = i;
            else if (c == ':')
            {
                name = line[..(semi >= 0 ? semi : i)].Trim().ToUpperInvariant();
                if (name is not ("BEGIN" or "END" or "UID" or "SUMMARY" or "DTSTART" or "DTEND" or "DURATION" or "RRULE" or "RECURRENCE-ID"
                    or "STATUS" or "EXDATE" or "RDATE" or "TZID" or "TZOFFSETFROM" or "TZOFFSETTO")) return false; // descriptions, attendees...: not copied
                pars = semi >= 0 ? line[semi..i] : "";
                value = line[(i + 1)..];
                return true;
            }
        }
        return false;
    }

    static string? Param(string pars, string key)
    {
        foreach (var part in pars.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.AsSpan(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return part[(eq + 1)..].Trim().Trim('"');
        }
        return null;
    }

    /// <summary>20261116 (a date) or 20261116T153000[Z] (a time on the clock of whatever zone it belongs to).</summary>
    public static bool ParseWall(string v, out DateTime wall, out bool isDate)
    {
        wall = default; isDate = false;
        if (v.Length < 8 || !Num(v, 0, 4, out int y) || !Num(v, 4, 2, out int mo) || !Num(v, 6, 2, out int d)) return false;
        if (y < 1 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return false;
        if (v.Length == 8) { wall = new DateTime(y, mo, d); isDate = true; return true; }
        if (v.Length < 15 || v[8] is not ('T' or 't') || !Num(v, 9, 2, out int h) || !Num(v, 11, 2, out int mi) || !Num(v, 13, 2, out int s)) return false;
        if (h > 23 || mi > 59) return false;
        wall = new DateTime(y, mo, d, h, mi, Math.Min(s, 59));
        return true;

        static bool Num(string v, int at, int len, out int n) => int.TryParse(v.AsSpan(at, len), NumberStyles.None, inv, out n);
    }

    /// <summary>+0100 or -0330 as minutes east of UTC.</summary>
    static int Offset(string v)
    {
        v = v.Trim();
        if (v.Length < 5 || !int.TryParse(v.AsSpan(1, 2), NumberStyles.None, inv, out int h) || !int.TryParse(v.AsSpan(3, 2), NumberStyles.None, inv, out int m)) return 0;
        return (v[0] == '-' ? -1 : 1) * (h * 60 + m);
    }

    /// <summary>P1D, PT1H30M, P2W...</summary>
    static TimeSpan ParseDuration(string v)
    {
        bool neg = false, time = false; long num = 0; var t = TimeSpan.Zero;
        foreach (char ch in v.Trim().ToUpperInvariant())
        {
            if (ch == '-') neg = true;
            else if (ch == 'T') time = true;
            else if (ch is >= '0' and <= '9') num = Math.Min(num * 10 + (ch - '0'), 100_000);
            else
            {
                t += ch switch { 'W' => TimeSpan.FromDays(7 * num), 'D' => TimeSpan.FromDays(num), 'H' => TimeSpan.FromHours(num),
                    'M' when time => TimeSpan.FromMinutes(num), 'S' => TimeSpan.FromSeconds(num), _ => TimeSpan.Zero };
                num = 0;
            }
        }
        return neg ? TimeSpan.Zero : t;
    }

    /// <summary>A title on one line: \, \; \\ unescaped, line breaks turned into spaces.</summary>
    static string Unescape(string v)
    {
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            if (c == '\\' && i + 1 < v.Length)
            {
                char n = v[++i];
                sb.Append(n is 'n' or 'N' ? ' ' : n);
            }
            else sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }
}
