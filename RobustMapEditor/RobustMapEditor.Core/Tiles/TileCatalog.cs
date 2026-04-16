#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RobustMapEditor.Core.Session;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RobustMapEditor.Core.Tiles;

/// <summary>
/// One selectable entry in the tile palette. Carries both the engine-side IDs
/// (needed to construct <see cref="Robust.Shared.Map.Tile"/>) and a
/// pre-rendered 32×32 thumbnail for the UI.
/// </summary>
public sealed class TileCatalogEntry : IDisposable
{
    /// <summary>Numeric tile ID assigned at engine registration. Goes into <c>new Tile(TypeId)</c>.</summary>
    public int TypeId { get; }

    /// <summary>String prototype ID (e.g. "FloorSteel"). Stable across sessions; used in YAML.</summary>
    public string Id { get; }

    /// <summary>Human-readable label for the palette. Falls back to <see cref="Id"/> if the
    /// definition doesn't provide one.</summary>
    public string Name { get; }

    /// <summary>True for the special space / empty-tile entry (eraser).</summary>
    public bool IsEmpty { get; }

    /// <summary>32×32 RGBA preview. Null for <see cref="IsEmpty"/>.</summary>
    public Image<Rgba32>? Thumbnail { get; }

    public TileCatalogEntry(int typeId, string id, string name, bool isEmpty, Image<Rgba32>? thumbnail)
    {
        TypeId = typeId;
        Id = id;
        Name = name;
        IsEmpty = isEmpty;
        Thumbnail = thumbnail;
    }

    public void Dispose() => Thumbnail?.Dispose();
}

/// <summary>
/// Builds the palette of tile definitions available for the currently-open document.
/// Enumerates <see cref="ITileDefinitionManager"/> for registered tiles and decodes
/// their sprite sheets' first variant for thumbnails.
/// </summary>
/// <remarks>
/// We use the document's own pair rather than the app-level <c>EditorSession</c> so
/// tile IDs line up with the grids being edited — in principle the two pairs share the
/// same prototype registrations (same content assembly), but we avoid the assumption.
/// </remarks>
public static class TileCatalog
{
    /// <summary>Tile size in pixels (32) — matches <c>EyeManager.PixelsPerMeter</c>.</summary>
    private const int TileSize = 32;

    /// <summary>Build a catalog from a document session. Runs on the pool thread; safe to
    /// await from the UI. The returned list owns its thumbnail images — dispose the
    /// catalog (or individual entries) when it's replaced.</summary>
    public static async Task<List<TileCatalogEntry>> BuildAsync(DocumentSession session)
    {
        var server = session.Pair.Server;
        var client = session.Pair.Client;

        var tileDefMan = server.ResolveDependency<ITileDefinitionManager>();
        var resMan = client.ResolveDependency<IResourceManager>();

        // Collect definition metadata on the server thread. Image decoding happens off-thread
        // below — Image.Load doesn't need engine locks.
        var metas = new List<(int typeId, string id, string name, string? spritePath)>();
        await server.WaitPost(() =>
        {
            foreach (var def in tileDefMan)
            {
                var spritePath = def.Sprite.ToString();
                metas.Add((
                    def.TileId,
                    def.ID,
                    string.IsNullOrWhiteSpace(def.Name) ? def.ID : def.Name,
                    string.IsNullOrWhiteSpace(spritePath) ? null : spritePath));
            }
        });

        // Decode the sprites off the awaiting thread. Without this, the Image.Load calls
        // run on whatever SynchronizationContext this method was awaited on — usually
        // Avalonia's UI thread — which stalls input during Open. ContentFileRead returns
        // a fresh Stream per call and Image.Load is pure CPU, so this is safe to hop off.
        var entries = await Task.Run(() =>
        {
            var result = new List<TileCatalogEntry>(metas.Count);
            foreach (var (typeId, id, name, spritePath) in metas)
            {
                if (spritePath == null)
                {
                    // Space / empty tile — used as the eraser.
                    result.Add(new TileCatalogEntry(typeId, id, name, isEmpty: true, thumbnail: null));
                    continue;
                }

                Image<Rgba32>? thumb = null;
                try
                {
                    using var stream = resMan.ContentFileRead(spritePath);
                    using var sheet = Image.Load<Rgba32>(stream);
                    // Sheet is `TileSize * variants` wide; variant 0 starts at x=0.
                    // Match TilePainter's vertical-flip so the palette preview matches what
                    // MapRenderer draws on the canvas.
                    thumb = sheet.Clone(o => o
                        .Crop(new Rectangle(0, 0, TileSize, TileSize))
                        .Flip(FlipMode.Vertical));
                }
                catch (Exception)
                {
                    // Tile def references a missing or unreadable sprite. Skip silently rather
                    // than aborting the whole catalog build.
                    thumb?.Dispose();
                    continue;
                }

                result.Add(new TileCatalogEntry(typeId, id, name, isEmpty: false, thumbnail: thumb));
            }

            // Stable order: empty tile first (for easy eraser selection), rest alphabetically by name.
            result.Sort((a, b) =>
            {
                if (a.IsEmpty != b.IsEmpty) return a.IsEmpty ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }).ConfigureAwait(false);

        return entries;
    }
}
