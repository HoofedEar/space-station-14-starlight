using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Content.PrototypeEditor.Reflection;

/// <summary>
/// Reads XML doc comments off-disk from the repo's source files. Content projects
/// aren't built with <c>GenerateDocumentationFile</c>, so reflection never sees the
/// comments — but this editor runs from a working-copy checkout, so we can parse
/// the .cs files directly.
/// </summary>
public sealed class ComponentDocResolver
{
    private static readonly Regex SummaryRegex = new(
        @"///\s*<summary>\s*(?<body>(?:.|\n)*?)\s*///\s*</summary>",
        RegexOptions.Compiled);

    private static readonly Regex SeeCrefRegex = new(
        @"<see\s+cref\s*=\s*""[^""]*?([^.""]+)""\s*/>",
        RegexOptions.Compiled);

    private static readonly Regex AnyTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    // Matches a class declaration whose simple name is the capture group 'name'.
    // We build this per-lookup so we can anchor on the specific class we want.
    private static Regex BuildClassRegex(string simpleName)
        => new(@"(^|\n)\s*(?:\[[^\n]*\]\s*)*(?:public|internal|private|protected)?\s*(?:sealed|abstract|static)?\s*(?:partial\s+)?class\s+"
               + Regex.Escape(simpleName) + @"\b",
               RegexOptions.Compiled);

    private readonly string? _repoRoot;
    private Dictionary<string, string>? _filenameIndex;
    private readonly Dictionary<string, string?> _summaryCache = new(StringComparer.Ordinal);

    public ComponentDocResolver()
    {
        _repoRoot = FindRepoRoot(AppContext.BaseDirectory);
    }

    /// <summary>
    /// Returns the class-level &lt;summary&gt; for the component type, falling back
    /// to its corresponding system's summary if the component has none. Returns
    /// null when no source file or summary is found.
    /// </summary>
    public string? GetSummary(Type componentType)
    {
        if (_repoRoot == null)
            return null;

        var key = componentType.FullName ?? componentType.Name;
        if (_summaryCache.TryGetValue(key, out var cached))
            return cached;

        var result = TryReadSummary(componentType.Name);

        if (string.IsNullOrEmpty(result) && componentType.Name.EndsWith("Component", StringComparison.Ordinal))
        {
            var stem = componentType.Name[..^"Component".Length];
            // Some systems use the "Shared" prefix when they live in Content.Shared
            // alongside partial client/server siblings; check both shapes.
            result = TryReadSummary(stem + "System")
                     ?? TryReadSummary("Shared" + stem + "System");
        }

        _summaryCache[key] = result;
        return result;
    }

    private string? TryReadSummary(string classSimpleName)
    {
        var path = FindFile(classSimpleName + ".cs");
        if (path == null)
            return null;

        string text;
        try { text = File.ReadAllText(path); }
        catch { return null; }

        // Locate the class declaration first so we don't accidentally pick up a
        // <summary> that belongs to a nested helper or a preceding type.
        var classMatch = BuildClassRegex(classSimpleName).Match(text);
        if (!classMatch.Success)
            return null;

        // Walk backward from the class declaration to collect the contiguous
        // block of /// lines directly above it (possibly interleaved with
        // [Attributes]). Stop at the first non-comment, non-attribute line.
        var lines = text[..classMatch.Index].Split('\n');
        var commentBlock = new List<string>();
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("///"))
            {
                commentBlock.Insert(0, lines[i]);
                continue;
            }
            if (trimmed.Length == 0 || trimmed.StartsWith("["))
                continue;
            break;
        }

        if (commentBlock.Count == 0)
            return null;

        var joined = string.Join('\n', commentBlock);
        var summary = SummaryRegex.Match(joined);
        if (!summary.Success)
            return null;

        return CleanSummary(summary.Groups["body"].Value);
    }

    private static string CleanSummary(string raw)
    {
        // Strip `///` prefixes, normalize whitespace, collapse runs of blank
        // lines — a doc comment that reads "one idea per line" looks like a
        // wall of text otherwise.
        var sb = new StringBuilder();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart();
            if (line.StartsWith("///"))
                line = line[3..];
            line = line.Trim();
            if (line.Length == 0)
            {
                // Treat blank XML lines as paragraph breaks.
                if (sb.Length > 0 && sb[^1] != '\n')
                    sb.Append('\n');
                continue;
            }
            if (sb.Length > 0 && sb[^1] != '\n')
                sb.Append(' ');
            sb.Append(line);
        }

        // Strip simple <see cref="X"/> tags to just X — keeps the summary
        // readable without needing a full XML doc renderer.
        var cleaned = SeeCrefRegex.Replace(sb.ToString(), "$1");
        cleaned = AnyTagRegex.Replace(cleaned, string.Empty);
        return cleaned.Trim();
    }

    private string? FindFile(string filename)
    {
        EnsureIndex();
        return _filenameIndex!.TryGetValue(filename, out var path) ? path : null;
    }

    private void EnsureIndex()
    {
        if (_filenameIndex != null)
            return;

        _filenameIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_repoRoot == null)
            return;

        // Exclude build outputs so we don't trawl hundreds of megabytes of
        // generated files. RobustToolbox *is* included because engine types
        // (e.g. TransformComponent) live there.
        var excluded = new[] { Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                               Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                               Path.AltDirectorySeparatorChar + "bin" + Path.AltDirectorySeparatorChar,
                               Path.AltDirectorySeparatorChar + "obj" + Path.AltDirectorySeparatorChar };

        foreach (var path in Directory.EnumerateFiles(_repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            var skip = false;
            foreach (var ex in excluded)
            {
                if (path.Contains(ex, StringComparison.OrdinalIgnoreCase))
                {
                    skip = true;
                    break;
                }
            }
            if (skip) continue;

            var name = Path.GetFileName(path);
            // First-write-wins: partial classes or duplicated filenames will
            // both resolve to the first path, which is fine for a summary lookup.
            _filenameIndex.TryAdd(name, path);
        }
    }

    private static string? FindRepoRoot(string start)
    {
        // Walk up from the running binary until we hit the solution file. This
        // is more reliable than Environment.CurrentDirectory, which can be set
        // to anything by whoever launched the tool.
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SpaceStation14.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
