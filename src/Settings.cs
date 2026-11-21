// glance.json in %LOCALAPPDATA%\TaskbarGlance. Written with defaults on first run so it is easy to edit; re-read when
// it changes. The "location" block is filled in by the app after it looked the city up.
using System.Globalization;
using System.Text.Json;

namespace TaskbarGlance;

static class Paths
{
    /// <summary>GLANCE_DIR overrides the folder (tests, a second copy with other settings).</summary>
    public static readonly string App = Environment.GetEnvironmentVariable("GLANCE_DIR") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarGlance");
    public static readonly string SettingsFile = Path.Combine(App, "glance.json");
    public static readonly string WeatherFile = Path.Combine(App, "weather.json");
    public static readonly string CalendarFile = Path.Combine(App, "calendar.json");
}

static class Settings
{
    public static bool ShowWeather = true;
    public static string City = "";            // "Buenos Aires" or "La Plata, Argentina"
    public static bool Fahrenheit;             // "units": "C" (default) or "F"
    public static string WeatherUrl = "";      // opened on click; empty = a web search for the city's weather
    public static int[] ForecastHours = [8, 14, 20];
    public static int RainWarnPercent = 50;    // chance of rain that counts as "rain later today"
    public static int RefreshMinutes = 30;
    public static bool Left;                   // "position": "left" (taskbar's left edge) or "right" (next to the tray, default)
    public static int OffsetX;                 // px at 100% scaling, measured from the chosen edge

    public static bool ShowCalendar = true;    // shown once "calendars" has an address
    public static string[] Calendars = [];     // iCal addresses (Google Calendar: "Secret address in iCal format")
    public static string CalendarUrl = "";     // opened on click; empty = Google Calendar
    public static int CalendarDays = 14;       // how far ahead the strip and the popup look
    public static int CalendarRefreshMinutes = 15;
    public static int CalendarTitleLength = 24; // longer titles are cut on the strip
    public static int CalendarRows = 8;        // events listed in the popup
    public static bool CalendarIcon;           // "calendarStyle": "icon" = a calendar icon on the strip instead of the next event
    public static int CalendarIconFreeDays;    // with the icon: 0 = always; N = only while nothing is coming in the next N days

    // location block: what the city resolved to. Edit latitude/longitude by hand for an exact spot.
    public static string ResolvedFor = "";     // the "city" text these coordinates belong to
    public static string Place = "";           // display name
    public static double Latitude, Longitude;

    public static bool NeedsGeocode => City.Length > 0 && !string.Equals(City, ResolvedFor, StringComparison.OrdinalIgnoreCase);

    static DateTime stamp;

    /// <summary>Loads the file if it changed. Returns true when it did.</summary>
    public static bool Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) { Directory.CreateDirectory(Paths.App); Save(); }
            var t = File.GetLastWriteTimeUtc(Paths.SettingsFile);
            if (t == stamp) return false;
            stamp = t;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Paths.SettingsFile), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            ShowWeather = Json.Bool(r, "showWeather", true);
            City = (Json.Str(r, "city") ?? "").Trim();
            Fahrenheit = string.Equals(Json.Str(r, "units"), "F", StringComparison.OrdinalIgnoreCase);
            WeatherUrl = (Json.Str(r, "weatherUrl") ?? "").Trim();
            ForecastHours = Hours(r) ?? [8, 14, 20];
            RainWarnPercent = Math.Clamp(Json.Int(r, "rainWarnPercent", 50), 1, 100);
            RefreshMinutes = Math.Clamp(Json.Int(r, "refreshMinutes", 30), 10, 720);
            Left = Json.Str(r, "position") == "left";
            OffsetX = Json.Int(r, "offsetX", 0);
            ShowCalendar = Json.Bool(r, "showCalendar", true);
            Calendars = Addresses(r);
            CalendarUrl = (Json.Str(r, "calendarUrl") ?? "").Trim();
            CalendarDays = Math.Clamp(Json.Int(r, "calendarDays", 14), 1, 60);
            CalendarRefreshMinutes = Math.Clamp(Json.Int(r, "calendarRefreshMinutes", 15), 5, 720);
            CalendarTitleLength = Math.Clamp(Json.Int(r, "calendarTitleLength", 24), 4, 80);
            CalendarRows = Math.Clamp(Json.Int(r, "calendarRows", 8), 1, 30);
            CalendarIcon = string.Equals(Json.Str(r, "calendarStyle"), "icon", StringComparison.OrdinalIgnoreCase);
            CalendarIconFreeDays = Math.Clamp(Json.Int(r, "calendarIconFreeDays", 0), 0, 60);
            ResolvedFor = ""; Place = ""; Latitude = Longitude = 0;
            if (r.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
                && Json.Num(loc, "latitude") is { } la && Json.Num(loc, "longitude") is { } lo)
            {
                ResolvedFor = Json.Str(loc, "for") ?? ""; Place = Json.Str(loc, "name") ?? ResolvedFor;
                Latitude = la; Longitude = lo;
            }
            return true;
        }
        catch { return false; } // half-written or invalid: keep the last good values
    }

    static int[]? Hours(JsonElement r)
    {
        if (!r.TryGetProperty("forecastHours", out var a) || a.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var e in a.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var h) && h is >= 0 and <= 23 && !list.Contains(h)) list.Add(h);
        list.Sort();
        return list.Count > 0 ? list.ToArray() : null;
    }

    /// <summary>"calendars": a list of addresses, or just one as text.</summary>
    static string[] Addresses(JsonElement r)
    {
        var list = new List<string>();
        void Add(string? s) { if ((s = s?.Trim()) is { Length: > 0 } && !list.Contains(s)) list.Add(s); }
        if (r.TryGetProperty("calendars", out var a))
        {
            if (a.ValueKind == JsonValueKind.String) Add(a.GetString());
            else if (a.ValueKind == JsonValueKind.Array)
                foreach (var e in a.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) Add(e.GetString());
        }
        return list.ToArray();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.App);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteBoolean("showWeather", ShowWeather);
                w.WriteString("city", City);
                w.WriteString("units", Fahrenheit ? "F" : "C");
                w.WriteString("weatherUrl", WeatherUrl);
                w.WriteStartArray("forecastHours");
                foreach (var h in ForecastHours) w.WriteNumberValue(h);
                w.WriteEndArray();
                w.WriteNumber("rainWarnPercent", RainWarnPercent);
                w.WriteNumber("refreshMinutes", RefreshMinutes);
                w.WriteString("position", Left ? "left" : "right");
                w.WriteNumber("offsetX", OffsetX);
                w.WriteBoolean("showCalendar", ShowCalendar);
                w.WriteStartArray("calendars");
                foreach (var c in Calendars) w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteString("calendarUrl", CalendarUrl);
                w.WriteNumber("calendarDays", CalendarDays);
                w.WriteNumber("calendarRefreshMinutes", CalendarRefreshMinutes);
                w.WriteNumber("calendarTitleLength", CalendarTitleLength);
                w.WriteNumber("calendarRows", CalendarRows);
                w.WriteString("calendarStyle", CalendarIcon ? "icon" : "event");
                w.WriteNumber("calendarIconFreeDays", CalendarIconFreeDays);
                if (ResolvedFor.Length > 0)
                {
                    w.WriteStartObject("location");
                    w.WriteString("for", ResolvedFor);
                    w.WriteString("name", Place);
                    w.WriteNumber("latitude", Math.Round(Latitude, 4));
                    w.WriteNumber("longitude", Math.Round(Longitude, 4));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, Paths.SettingsFile, true);
            stamp = File.GetLastWriteTimeUtc(Paths.SettingsFile);
        }
        catch { }
    }
}

static class Json
{
    public static string? Str(JsonElement r, string n) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static double? Num(JsonElement r, string n) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    public static bool Bool(JsonElement r, string n, bool d) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
    public static int Int(JsonElement r, string n, int d) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : d;
    public static string Inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
