// Shell actions: open the forecast page, autostart, open the settings file.
using static TaskbarGlance.Native;

namespace TaskbarGlance;

static class Shell
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "TaskbarGlance";

    /// <summary>The page behind the weather segment: weatherUrl, or a web search for the city's weather.</summary>
    public static void OpenWeather()
    {
        if (Settings.City.Length == 0 || Weather.NotFound) { OpenFile(Paths.SettingsFile); return; }
        var url = Settings.WeatherUrl;
        if (url.Length == 0)
            url = "https://www.google.com/search?q=" + Uri.EscapeDataString("weather " + (Settings.Place.Length > 0 ? Settings.Place : Settings.City));
        OpenUrl(url);
    }

    /// <summary>Only web addresses: a settings value never gets to start a program.</summary>
    public static void OpenUrl(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ShellExecuteW(0, "open", url, null, null, 1);
        else Log.Write($"not opened (must start with http:// or https://): {url}");
    }

    public static bool Autostart
    {
        get => RegString(RunKey, RunValue) != null;
        set
        {
            if (value)
            {
                var v = $"\"{Environment.ProcessPath}\"";
                RegSetKeyValueW(HKEY_CURRENT_USER, RunKey, RunValue, REG_SZ, v, (uint)((v.Length + 1) * 2));
            }
            else RegDeleteKeyValueW(HKEY_CURRENT_USER, RunKey, RunValue);
        }
    }

    public static void OpenFile(string path)
    {
        if (ShellExecuteW(0, "open", path, null, null, 1) <= 32) ShellExecuteW(0, "open", "notepad.exe", $"\"{path}\"", null, 1);
    }
}
