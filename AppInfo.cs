using System.Reflection;

namespace ClearMark;

internal static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    public static string Label => $"ClearMark v{Version}";

    private static string ReadVersion()
    {
        string? raw = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(raw))
            return "1.1.1";
        int plus = raw.IndexOf('+');
        return plus < 0 ? raw : raw[..plus];
    }
}
