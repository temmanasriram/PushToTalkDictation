using System.Reflection;

namespace PushToTalkDictation;

/// <summary>
/// Product identity, read once from assembly metadata. The version lives in the csproj
/// and is read back from the built assembly rather than duplicated as a constant here,
/// so there is exactly one place to bump it.
/// </summary>
public static class AppInfo
{
    public const string ProductName = "Push-to-Talk Dictation";

    /// <summary>File name of the shipped HTML user guide, beside the executable.</summary>
    public const string UserGuideFileName = "user-guide.html";

    /// <summary>Version without any build-metadata suffix, e.g. "1.0.0".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>"Push-to-Talk Dictation 1.0.0" — for the tray header and the startup log.</summary>
    public static string NameAndVersion => $"{ProductName} {Version}";

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;

        // InformationalVersion carries what the csproj <Version> says. The SDK appends
        // "+<commit>" when a source revision is known, which is noise in a tray menu.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
