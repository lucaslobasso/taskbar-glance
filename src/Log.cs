// Tiny append-only log (glance.log next to the settings): startup facts and errors only, capped at 64 KB.
namespace TaskbarGlance;

static class Log
{
    static readonly string File_ = Path.Combine(Paths.App, "glance.log");
    public static readonly bool Debug = Environment.GetEnvironmentVariable("GLANCE_DEBUG") == "1";

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(Paths.App);
            var fi = new FileInfo(File_);
            if (fi.Exists && fi.Length > 64 * 1024) File.Move(File_, File_ + ".old", true);
            File.AppendAllText(File_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
