namespace PushToTalkDictation.Stt;

/// <summary>
/// Finds model assets on disk.
///
/// Models are hundreds of megabytes and are not in source control, so they are
/// deliberately *not* copied into the build output on every build — that would double
/// every incremental build and duplicate the model per configuration. Instead a relative
/// <c>ModelDirectory</c> / <c>ModelPath</c> is resolved against an ordered list of roots,
/// so the same <c>appsettings.json</c> works in three layouts without editing:
///
///   1. <c>SpeechToText.ModelRoot</c>, when set — an installer or a shared model store.
///   2. Next to the executable — the published/shipped layout. <c>dotnet publish</c> copies
///      <c>models\</c> here (see the CopyModelsToPublish target in the csproj), so a
///      publish folder is movable as a unit.
///   3. <c>%LOCALAPPDATA%\PushToTalkDictation</c> — a per-user store that survives
///      reinstalls and is writable without elevation.
///   4. Ancestors of the executable's directory — the dev layout, where
///      <c>scripts\download-models.ps1</c> puts the model in the repo root's <c>models\</c>
///      and the exe runs out of <c>bin\&lt;Config&gt;\net8.0-windows\win-x64\</c>.
///
/// An absolute configured path is used exactly as given; no probing.
/// </summary>
public static class ModelPathResolver
{
    /// <summary>
    /// How far up from the executable to look. The dev output sits four levels below the
    /// project root (<c>bin\Debug\net8.0-windows\win-x64</c>), a publish folder five.
    /// </summary>
    private const int AncestorProbeDepth = 5;

    /// <summary>Resolves a configured model directory, or throws naming every location tried.</summary>
    public static string ResolveDirectory(string configured, string? modelRoot) =>
        Resolve(configured, modelRoot, Directory.Exists, "directory");

    /// <summary>Resolves a configured model file, or throws naming every location tried.</summary>
    public static string ResolveFile(string configured, string? modelRoot) =>
        Resolve(configured, modelRoot, File.Exists, "file");

    private static string Resolve(
        string configured,
        string? modelRoot,
        Func<string, bool> exists,
        string noun)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"No model {noun} is configured.");

        if (Path.IsPathRooted(configured))
        {
            var absolute = Path.GetFullPath(configured);
            if (exists(absolute)) return absolute;

            throw Missing(noun, configured, [absolute]);
        }

        var probed = new List<string>(AncestorProbeDepth + 3);

        foreach (var root in ProbeRoots(modelRoot))
        {
            var candidate = Path.GetFullPath(configured, root);
            probed.Add(candidate);
            if (exists(candidate)) return candidate;
        }

        throw Missing(noun, configured, probed);
    }

    /// <summary>
    /// The roots a relative model path is tried against, in order, de-duplicated.
    /// Public so diagnostics can report exactly where the app looked.
    /// </summary>
    public static IReadOnlyList<string> ProbeRoots(string? modelRoot)
    {
        var roots = new List<string>(AncestorProbeDepth + 3);

        void Add(string path)
        {
            var full = Path.GetFullPath(path);
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                roots.Add(full);
        }

        // 1. Explicit override. Relative values are themselves relative to the executable,
        //    so an installer can point at a sibling folder without knowing the install path.
        if (!string.IsNullOrWhiteSpace(modelRoot))
            Add(Path.IsPathRooted(modelRoot) ? modelRoot : Path.GetFullPath(modelRoot, AppContext.BaseDirectory));

        // 2. Beside the executable — what a published install looks like. First of the
        //    implicit roots so a deployed app is deterministic and never picks up a stray
        //    copy from somewhere further up the tree.
        Add(AppContext.BaseDirectory);

        // 3. Per-user store, alongside the logs.
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PushToTalkDictation"));

        // 4. Up and out of bin\<Config>\<Tfm>\<Rid>\ to the project root — the dev layout.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < AncestorProbeDepth && directory.Parent is not null; i++)
        {
            directory = directory.Parent;
            Add(directory.FullName);
        }

        return roots;
    }

    private static Exception Missing(string noun, string configured, IReadOnlyList<string> probed)
    {
        var message =
            $"Model {noun} '{configured}' was not found. Looked in:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, probed.Select(p => "  " + p)) +
            Environment.NewLine +
            @"Run scripts\download-models.ps1, or set SpeechToText.ModelRoot to where the model lives.";

        return noun == "file"
            ? new FileNotFoundException(message, configured)
            : new DirectoryNotFoundException(message);
    }
}
