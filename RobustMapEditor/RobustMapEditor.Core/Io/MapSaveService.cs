#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RobustMapEditor.Core.Documents;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Emitter = YamlDotNet.Core.Emitter;
using RobustYamlMappingFix = Robust.Shared.Serialization.YamlMappingFix;

namespace RobustMapEditor.Core.Io;

/// <summary>
/// Persists a <see cref="MapDocument"/> back to YAML. Unlike
/// <c>MapLoaderSystem.TrySaveMap</c> — which can only write under
/// <c>IResourceManager.UserData</c> — this writes to any absolute path so the editor
/// can overwrite source files directly.
/// </summary>
/// <remarks>
/// The path is: <see cref="MapLoaderSystem.SerializeEntitiesRecursive"/> to obtain
/// a <see cref="Robust.Shared.Serialization.Markdown.Mapping.MappingDataNode"/>, then
/// dump through <c>YamlMappingFix</c> + <see cref="Emitter"/> to a file stream. That
/// mirrors the private <c>MapLoaderSystem.Write</c> path, swapping UserData for
/// <see cref="File.CreateText(string)"/>.
/// </remarks>
public static class MapSaveService
{
    /// <summary>Serialize the document's maps to YAML at the document's current
    /// <see cref="MapDocument.FilePath"/>.</summary>
    public static Task SaveAsync(MapDocument document)
        => SaveAsAsync(document, document.FilePath);

    /// <summary>Serialize the document's maps to YAML at <paramref name="targetPath"/>
    /// and clear the dirty flag on success. The document's own FilePath is not changed;
    /// the caller (UI layer) decides whether to rebind after a Save-As.</summary>
    public static async Task SaveAsAsync(MapDocument document, string targetPath)
    {
        var session = document.Session;
        var server = session.Pair.Server;

        // Grab the map entity set. MapUids is captured at load time and is what
        // SerializeEntitiesRecursive expects — it will walk children (including grids) itself.
        var roots = new HashSet<EntityUid>(session.MapUids);
        if (roots.Count == 0)
            throw new InvalidOperationException(
                "DocumentSession has no map entities to save. Was the file a bare grid?");

        Robust.Shared.Serialization.Markdown.Mapping.MappingDataNode? node = null;
        await server.WaitPost(() =>
        {
            var mapLoader = server.System<MapLoaderSystem>();
            var (data, _cat) = mapLoader.SerializeEntitiesRecursive(roots);
            node = data;
        });

        if (node == null)
            throw new InvalidOperationException("Serialization returned no data.");

        // Write to a temp file and move into place so a crash mid-write doesn't corrupt
        // the original — the editor is expected to be pointed at real repo files.
        var tempPath = targetPath + ".tmp";
        try
        {
            using (var writer = File.CreateText(tempPath))
            {
                var document_ = new YamlDocument(node.ToYaml());
                var stream = new YamlStream { document_ };
                stream.Save(new RobustYamlMappingFix(new Emitter(writer)), false);
            }

            // Atomic replace where the platform supports it; falls back to delete+move.
            if (File.Exists(targetPath))
                File.Replace(tempPath, targetPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, targetPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
            throw;
        }

        document.MarkClean();
    }
}
