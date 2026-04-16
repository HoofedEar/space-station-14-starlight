using Robust.Client;

namespace Content.PrototypeEditor;

internal static class Program
{
    public static void Main(string[] args)
    {
        ContentStart.StartLibrary(args, new GameControllerOptions
        {
            // Sandboxing protects end users from untrusted content assemblies shipped by a
            // game server. This is a local dev tool — no network trust boundary exists —
            // so we disable it to avoid type-check restrictions on tool-only APIs.
            Sandboxing = false,
            ContentModulePrefix = "Content.",
            ContentBuildDirectory = "Content.PrototypeEditor",
            DefaultWindowTitle = "SS14 Prototype Editor",
            UserDataDirectoryName = "Space Station 14",
            ConfigFileName = "prototype_editor.toml",
        });
    }
}
