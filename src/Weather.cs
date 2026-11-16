// Weather from Open-Meteo (free, no key): the current reading plus two days of hourly forecast in one request.
// Everything is kept in °C and in the city's own local time; °F is only a display conversion.
using System.Globalization;
using System.Text.Json;
using static TaskbarGlance.Native;

namespace TaskbarGlance;

enum Sky { Clear, PartlyCloudy, Cloudy, Fog, Rain, Snow, Thunder }

record struct Hour(DateTime Time, double Temp, int Code, int Pop, bool Day);
record struct Reading(double Temp, int Code, bool Day);
/// <summary>Rain and/or thunder announced for later today (the city's day), each with the hour it starts.</summary>
record struct Warning(DateTime? Rain, DateTime? Thunder);

static class Weather
{
    public static bool HasData { get; private set; }
    public static bool NotFound { get; private set; }   // the city name matched nothing
    public static int Failures { get; private set; }    // failed fetches in a row
    public static Hour[] Hours { get; private set; } = [];

    /// <summary>GLANCE_OFFLINE=1: never fetch; weather.json is read (and re-read when it changes). For tests.</summary>
    public static readonly bool Offline = Environment.GetEnvironmentVariable("GLANCE_OFFLINE") == "1";

    // --render: a fixed "now" (city time) and data age, so pictures and tests do not depend on the clock
    public static DateTime? NowOverride;
    public static TimeSpan AgeOverride;

    static DateTime fetchedUtc, cacheStamp;
    static int offsetSec;
    static double curTemp; static int curCode; static bool curDay;
    static bool busy;
    static volatile Result? result;

    sealed class Result
    {
        public string City = ""; public bool Geocoded, NoMatch;
        public string Place = ""; public double Lat, Lon;
        public byte[]? Forecast; public int Status;
    }

    public static DateTime LocalNow => NowOverride ?? DateTime.UtcNow.AddSeconds(offsetSec);
    public static TimeSpan Age => NowOverride != null ? AgeOverride : DateTime.UtcNow - fetchedUtc;

    // ---- reading the data ---------------------------------------------------------------------------------------

    /// <summary>The current weather: the last reading while it is fresh, otherwise the forecast for this hour, otherwise
    /// nothing (better no number than a wrong one).</summary>
    public static Reading? Current()
    {
        if (!HasData) return null;
        if (Age < TimeSpan.FromMinutes(90)) return new Reading(curTemp, curCode, curDay);
        var now = LocalNow;
        var slot = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        foreach (var h in Hours) if (h.Time == slot) return new Reading(h.Temp, h.Code, h.Day);
        return null;
    }

    /// <summary>Rain or thunder in the hours still to come today. Nothing if it is already doing that.</summary>
    public static Warning? Later()
    {
        if (Current() is not { } c) return null;
        var sky = SkyOf(c.Code);
        if (sky == Sky.Thunder) return null;
        var now = LocalNow;
        DateTime? rain = null, thunder = null;
        foreach (var h in Hours)
        {
            if (h.Time <= now || h.Time.Date != now.Date) continue;
            var s = SkyOf(h.Code);
            if (s == Sky.Thunder) thunder ??= h.Time;
            else if (sky is not (Sky.Rain or Sky.Snow) && s != Sky.Snow && (s == Sky.Rain || h.Pop >= Settings.RainWarnPercent)) rain ??= h.Time;
        }
        if (rain != null && thunder != null && thunder <= rain) rain = null; // the storm comes first: one message
        return rain == null && thunder == null ? null : new Warning(rain, thunder);
    }

    /// <summary>The forecast rows of the popup: today's configured hours still ahead, then all of tomorrow's.</summary>
    public static List<(string Day, Hour Hour)> Slots()
    {
        var list = new List<(string, Hour)>();
        var now = LocalNow;
        foreach (var h in Hours)
        {
            if (h.Time.Minute != 0 || Array.IndexOf(Settings.ForecastHours, h.Time.Hour) < 0) continue;
            if (h.Time.Date == now.Date) { if (h.Time > now) list.Add(("Today", h)); }
            else if (h.Time.Date == now.Date.AddDays(1)) list.Add(("Tomorrow", h));
        }
        return list;
    }

    public static Sky SkyOf(int code) => code switch
    {
        0 or 1 => Sky.Clear,
        2 => Sky.PartlyCloudy,
        3 => Sky.Cloudy,
        45 or 48 => Sky.Fog,
        >= 51 and <= 67 => Sky.Rain,
        >= 71 and <= 77 => Sky.Snow,
        >= 80 and <= 82 => Sky.Rain,
        85 or 86 => Sky.Snow,
        >= 95 => Sky.Thunder,
        _ => Sky.Cloudy,
    };

    /// <summary>WMO weather code in words.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Clear", 1 => "Mostly clear", 2 => "Partly cloudy", 3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle", 56 or 57 => "Freezing drizzle",
        61 => "Light rain", 63 => "Rain", 65 => "Heavy rain", 66 or 67 => "Freezing rain",
        71 => "Light snow", 73 => "Snow", 75 => "Heavy snow", 77 => "Snow grains",
        80 => "Light showers", 81 => "Showers", 82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm", 96 or 99 => "Thunderstorm with hail",
        _ => "Cloudy",
    };

    public static string Temp(double celsius)
    {
        double v = Settings.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;
        return ((int)Math.Round(v, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "°";
    }

    public static string Clock(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string AgeText()
    {
        var a = Age;
        return a.TotalMinutes < 1.5 ? "updated just now" : a.TotalMinutes < 90 ? $"updated {(int)Math.Round(a.TotalMinutes)} min ago"
             : a.TotalHours < 36 ? $"updated {(int)Math.Round(a.TotalHours)} h ago" : $"updated {(int)Math.Round(a.TotalDays)} days ago";
    }

    // ---- parsing ------------------------------------------------------------------------------------------------

    /// <summary>Reads an Open-Meteo forecast answer. False (and nothing changed) if it is not one.</summary>
    public static bool Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var cur = r.GetProperty("current");
            var hourly = r.GetProperty("hourly");
            var times = hourly.GetProperty("time");
            var temps = hourly.GetProperty("temperature_2m");
            var codes = hourly.GetProperty("weather_code");
            bool hasPop = hourly.TryGetProperty("precipitation_probability", out var pops);
            bool hasDay = hourly.TryGetProperty("is_day", out var days);
            int n = times.GetArrayLength();
            var hours = new List<Hour>(n);
            for (int i = 0; i < n; i++)
            {
                if (temps[i].ValueKind != JsonValueKind.Number || codes[i].ValueKind != JsonValueKind.Number) continue;
                var t = DateTime.ParseExact(times[i].GetString()!, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
                int pop = hasPop && pops[i].ValueKind == JsonValueKind.Number ? (int)Math.Round(pops[i].GetDouble()) : 0;
                bool day = hasDay && days[i].ValueKind == JsonValueKind.Number ? days[i].GetInt32() != 0 : t.Hour is >= 7 and < 20;
                hours.Add(new Hour(t, temps[i].GetDouble(), codes[i].GetInt32(), pop, day));
            }
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            bool isDay = Json.Num(cur, "is_day") is not 0.0;
            offsetSec = (int)(Json.Num(r, "utc_offset_seconds") ?? 0);
            curTemp = temp; curCode = code; curDay = isDay; Hours = hours.ToArray();
            HasData = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Last saved answer, so a restart shows something at once. Returns true if it loaded (new) data.</summary>
    public static bool LoadCache()
    {
        try
        {
            if (!File.Exists(Paths.WeatherFile)) return false;
            var t = File.GetLastWriteTimeUtc(Paths.WeatherFile);
            if (t == cacheStamp) return false;
            cacheStamp = t;
            if (!Parse(File.ReadAllBytes(Paths.WeatherFile))) return false;
            fetchedUtc = t;
            return true;
        }
        catch { return false; }
    }

    // ---- fetching -----------------------------------------------------------------------------------------------

    /// <summary>Decides when the next request happens: now if there is nothing fresh to show, otherwise one timer.</summary>
    public static void Schedule()
    {
        if (Offline) return;
        if (!Settings.ShowWeather || Settings.City.Length == 0) { KillTimer(Strip.Hwnd, Program.T_REFRESH); return; }
        var left = TimeSpan.FromMinutes(Settings.RefreshMinutes) - Age;
        if (Settings.NeedsGeocode || !HasData || left <= TimeSpan.Zero) Fetch();
        else SetTimer(Strip.Hwnd, Program.T_REFRESH, (uint)Math.Max(1000, left.TotalMilliseconds), 0);
    }

    /// <summary>After sleep the timer and the data are both out of date; wait a moment for the network first.</summary>
    public static void OnResume()
    {
        if (Offline || !Settings.ShowWeather || Settings.City.Length == 0) return;
        if (!HasData || Age >= TimeSpan.FromMinutes(Settings.RefreshMinutes)) SetTimer(Strip.Hwnd, Program.T_REFRESH, 8000, 0);
        else Schedule();
    }

    /// <summary>One request on a short-lived thread (two the first time, to look the city up). The UI thread is told
    /// by a window message when it is done.</summary>
    public static void Fetch()
    {
        KillTimer(Strip.Hwnd, Program.T_REFRESH);
        if (Offline || busy || Settings.City.Length == 0) return;
        busy = true;
        string city = Settings.City; bool geocode = Settings.NeedsGeocode;
        double lat = Settings.Latitude, lon = Settings.Longitude;
        nint hwnd = Strip.Hwnd;
        var th = new Thread(() =>
        {
            var res = new Result { City = city, Lat = lat, Lon = lon };
            try
            {
                if (!geocode || Geocode(res)) res.Forecast = Net.Get(ForecastUrl(res.Lat, res.Lon), out res.Status);
            }
            catch (Exception e) { Log.Write($"fetch error: {e.GetType().Name}: {e.Message}"); }
            result = res;
            PostMessageW(hwnd, Strip.WM_FETCHED, 0, 0);
        }, 256 * 1024) { IsBackground = true };
        th.Start();
    }

    static string ForecastUrl(double lat, double lon) =>
        $"https://api.open-meteo.com/v1/forecast?latitude={Json.Inv(lat)}&longitude={Json.Inv(lon)}" +
        "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,precipitation_probability,is_day" +
        "&forecast_days=2&timezone=auto";

    /// <summary>City name to coordinates. "Name, Region or country" picks the match in that region or country.</summary>
    static bool Geocode(Result res)
    {
        var parts = res.City.Split(',', 2, StringSplitOptions.TrimEntries);
        var body = Net.Get($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(parts[0])}&count=10&language=en&format=json", out res.Status);
        if (body == null) return false;
        if (Log.Debug) Log.Write($"geocode: {body.Length} bytes");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            res.NoMatch = true;
            return false;
        }
        var pick = list[0];
        if (parts.Length > 1)
            foreach (var e in list.EnumerateArray())
                if (Same(Json.Str(e, "country"), parts[1]) || Same(Json.Str(e, "admin1"), parts[1]) || Same(Json.Str(e, "country_code"), parts[1])) { pick = e; break; }
        if (Json.Num(pick, "latitude") is not { } la || Json.Num(pick, "longitude") is not { } lo) return false;
        res.Lat = la; res.Lon = lo; res.Place = Json.Str(pick, "name") ?? parts[0]; res.Geocoded = true;
        return true;

        static bool Same(string? a, string b) => a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    static readonly uint[] retryMs = [60_000, 300_000, 900_000];

    /// <summary>UI thread: takes the worker's answer, saves it and sets the next timer.</summary>
    public static void OnFetched()
    {
        var res = result; result = null; busy = false;
        if (res == null) return;
        if (!string.Equals(res.City, Settings.City, StringComparison.OrdinalIgnoreCase)) { Schedule(); return; } // city changed meanwhile

        if (res.NoMatch)
        {
            NotFound = true; HasData = false;
            Log.Write($"city not found: \"{res.City}\"");
            return; // no retry: only a new city name fixes this
        }
        if (res.Geocoded)
        {
            Settings.ResolvedFor = res.City; Settings.Place = res.Place; Settings.Latitude = res.Lat; Settings.Longitude = res.Lon;
            Settings.Save();
            Log.Write($"city \"{res.City}\" = {res.Place} ({Json.Inv(res.Lat)}, {Json.Inv(res.Lon)})");
        }
        if (res.Forecast != null && Parse(res.Forecast))
        {
            NotFound = false; Failures = 0; fetchedUtc = DateTime.UtcNow;
            try
            {
                var tmp = Paths.WeatherFile + ".tmp";
                File.WriteAllBytes(tmp, res.Forecast);
                File.Move(tmp, Paths.WeatherFile, true);
                cacheStamp = File.GetLastWriteTimeUtc(Paths.WeatherFile);
            }
            catch { }
            if (Log.Debug) Log.Write($"fetched: {Temp(curTemp)} code={curCode} hours={Hours.Length}");
            SetTimer(Strip.Hwnd, Program.T_REFRESH, (uint)Settings.RefreshMinutes * 60_000, 0);
        }
        else
        {
            Failures++;
            if (Failures == 1 || Log.Debug) Log.Write($"fetch failed (http {res.Status}); retrying");
            SetTimer(Strip.Hwnd, Program.T_REFRESH, retryMs[Math.Min(Failures, retryMs.Length) - 1], 0);
        }
    }

    /// <summary>--render: pretend the city was not found.</summary>
    public static void Pretend(bool notFound) => NotFound = notFound;
}
