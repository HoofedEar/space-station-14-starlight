#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RobustMapEditor.Core.Session;
using Robust.Client.GameObjects;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using RobustColor = Robust.Shared.Maths.Color;

namespace RobustMapEditor.Core.Entities;

/// <summary>
/// One selectable entry in the entity palette. Carries the prototype identifier,
/// a display name, and an optional pre-rendered preview composited from every
/// visible layer of the prototype's resolved <c>SpriteComponent</c>.
/// </summary>
public sealed class EntityCatalogEntry : IDisposable
{
    /// <summary>Prototype ID, e.g. "TableCarpet". Stable across sessions; used in YAML and
    /// passed to <c>EntityManager.SpawnEntity</c>.</summary>
    public string Id { get; }

    /// <summary>Human-readable name. Falls back to <see cref="Id"/> if the prototype's
    /// localized name is empty (typical for anonymous intermediates).</summary>
    public string Name { get; }

    /// <summary>Optional "(suffix)" the prototype declares for editor menus.</summary>
    public string? EditorSuffix { get; }

    /// <summary>RGBA preview cropped from the prototype's south-facing frame-0, with every
    /// visible sprite layer composited in draw-depth order (matches <c>EntityPainter</c>
    /// behavior). Null when the prototype has no spawnable sprite.</summary>
    public Image<Rgba32>? Thumbnail { get; }

    public EntityCatalogEntry(string id, string name, string? editorSuffix, Image<Rgba32>? thumbnail)
    {
        Id = id;
        Name = name;
        EditorSuffix = editorSuffix;
        Thumbnail = thumbnail;
    }

    public void Dispose() => Thumbnail?.Dispose();
}

/// <summary>
/// Builds the palette of entity prototypes available for placement in the current document.
/// Filters out abstract prototypes and anything flagged <c>HideSpawnMenu</c>, then skips
/// prototypes with no <c>SpriteComponent</c> — those are invisible utility entities
/// (markers, logic holders) with no business in a visual map editor.
/// </summary>
public static class EntityCatalog
{
    /// <summary>
    /// Build a catalog from the document session's prototype manager.
    /// </summary>
    /// <remarks>
    /// The strategy mirrors the engine's own <c>EntitySpawnWindow</c> /
    /// <c>SpriteSystem.GetPrototypeIcon</c> pattern: spawn a dummy client entity per
    /// prototype into nullspace, walk its fully-resolved <c>SpriteComponent.AllLayers</c>,
    /// snapshot each layer's RSI path + state + color, then delete the dummy. This captures
    /// everything YAML inheritance has already folded into the sprite, plus any
    /// <c>IconComponent</c> / <c>SpriteSpecifier.EntityPrototype</c> overrides that the
    /// naive "read the first layer's png" path misses.
    ///
    /// Enumeration is on the <b>client</b> on purpose. <c>SpriteComponent</c> lives in
    /// <c>Robust.Client</c>, so the server's prototype loader drops <c>- type: Sprite</c>
    /// entries as "Unknown component" during YAML read — the client side keeps them intact.
    /// Prototype IDs are shared, so the list is still valid to pass to server-side
    /// <c>EntityManager.SpawnEntity</c> during placement.
    /// </remarks>
    public static async Task<List<EntityCatalogEntry>> BuildAsync(DocumentSession session)
    {
        var client = session.Pair.Client;
        var protoMan = client.ResolveDependency<IPrototypeManager>();
        var resMan = client.ResolveDependency<IResourceManager>();
        var entMan = client.ResolveDependency<IEntityManager>();

        // Phase 1 — engine tick. Spawn every eligible prototype, snapshot sprite layers,
        // delete. All work here touches engine state and must run on the client's
        // sync-context via WaitPost.
        var metas = new List<EntityMeta>();
        await client.WaitPost(() =>
        {
            foreach (var proto in protoMan.EnumeratePrototypes<EntityPrototype>())
            {
                if (proto.Abstract) continue;
                if (proto.HideSpawnMenu) continue;
                if (!proto.Components.ContainsKey("Sprite")) continue;

                var name = string.IsNullOrWhiteSpace(proto.Name) ? proto.ID : proto.Name;
                var layers = SnapshotLayers(proto.ID, entMan);
                metas.Add(new EntityMeta(proto.ID, name, proto.EditorSuffix, layers));
            }
        });

        // Phase 2 — off-thread. Load + crop + composite PNGs. This is pure CPU + file I/O,
        // both thread-safe under ImageSharp and IResourceManager's ContentFileRead.
        var entries = await Task.Run(() =>
        {
            // A single prototype typically re-uses a handful of RSI sheets across its
            // layers, and many prototypes share the same base RSI. Caching by
            // (path, state) prevents re-loading + re-decoding the same PNG thousands of
            // times; disposed in bulk when the phase finishes.
            var sheetCache = new Dictionary<(string Path, string State), Image<Rgba32>>();
            try
            {
                var result = new List<EntityCatalogEntry>(metas.Count);
                foreach (var meta in metas)
                {
                    Image<Rgba32>? thumb = null;
                    if (meta.Layers.Count > 0)
                    {
                        try { thumb = Composite(meta.Layers, resMan, sheetCache); }
                        catch { thumb?.Dispose(); thumb = null; }
                    }

                    result.Add(new EntityCatalogEntry(meta.Id, meta.Name, meta.Suffix, thumb));
                }

                result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return result;
            }
            finally
            {
                foreach (var img in sheetCache.Values)
                    img.Dispose();
            }
        }).ConfigureAwait(false);

        return entries;
    }

    /// <summary>Spawn a dummy into nullspace, read back every visible sprite layer's RSI
    /// coordinates + tint, delete. Returns an empty list when the prototype refuses to
    /// spawn (missing dependency component, broken YAML, etc.) — that entry still makes
    /// it into the palette, it just has no thumbnail.</summary>
    private static List<LayerSnapshot> SnapshotLayers(string protoId, IEntityManager entMan)
    {
        var snapshots = new List<LayerSnapshot>();

        EntityUid dummy;
        try
        {
            dummy = entMan.SpawnEntity(protoId, MapCoordinates.Nullspace);
        }
        catch
        {
            // Some prototypes declare hard requirements that can't be satisfied in a
            // headless dummy spawn. Fall through to null thumbnail.
            return snapshots;
        }

        try
        {
            if (!entMan.TryGetComponent(dummy, out SpriteComponent? sprite))
                return snapshots;

            var spriteColor = sprite.Color;
            foreach (var layer in sprite.AllLayers)
            {
                if (!layer.Visible) continue;
                if (!layer.RsiState.IsValid) continue;

                var rsi = layer.ActualRsi;
                if (rsi?.Path == null) continue;
                if (!rsi.TryGetState(layer.RsiState, out var state)) continue;

                snapshots.Add(new LayerSnapshot(
                    rsi.Path.ToString(),
                    state.StateId.Name!,
                    rsi.Size.X,
                    rsi.Size.Y,
                    layer.Color,
                    spriteColor));
            }
        }
        finally
        {
            entMan.DeleteEntity(dummy);
        }

        return snapshots;
    }

    /// <summary>Composite a list of pre-resolved layer snapshots into a single image,
    /// in the order they were collected (which is the sprite's layer order — low draw
    /// depth to high). Matches <c>EntityPainter</c>'s per-layer pipeline minus the
    /// map-canvas transforms (rotation, Y-flip, world offset) — thumbnails render
    /// south-facing frame-0 only.</summary>
    private static Image<Rgba32>? Composite(
        List<LayerSnapshot> layers,
        IResourceManager resMan,
        Dictionary<(string, string), Image<Rgba32>> sheetCache)
    {
        Image<Rgba32>? canvas = null;

        foreach (var layer in layers)
        {
            var key = (layer.RsiPath, layer.StateName);
            if (!sheetCache.TryGetValue(key, out var sheet))
            {
                try
                {
                    using var stream = resMan.ContentFileRead($"{layer.RsiPath}/{layer.StateName}.png");
                    sheet = Image.Load<Rgba32>(stream);
                    sheetCache[key] = sheet;
                }
                catch
                {
                    // Missing or unreadable PNG — skip this layer but continue composing.
                    continue;
                }
            }

            // South-facing frame-0 = top-left (rsi.Size.X × rsi.Size.Y) tile of the sheet.
            // (EntityPainter's GetRsiFrame resolves to (0, 0) when direction == 0.)
            var cropW = Math.Min(layer.FrameW, sheet.Width);
            var cropH = Math.Min(layer.FrameH, sheet.Height);
            if (cropW <= 0 || cropH <= 0) continue;

            using var frame = sheet.Clone(o => o.Crop(new Rectangle(0, 0, cropW, cropH)));

            // sprite.Color × layer.Color, baked into the frame via a Multiply blend.
            // Matches EntityPainter; without this, tinted species parts / recolored
            // clothing layers show up at their untinted base color.
            var tint = layer.SpriteColor * layer.LayerColor;
            if (tint != RobustColor.White)
            {
                var tintColor = Color.FromRgba(tint.RByte, tint.GByte, tint.BByte, tint.AByte);
                using var colored = new Image<Rgba32>(frame.Width, frame.Height);
                colored.Mutate(o => o.BackgroundColor(tintColor));
                frame.Mutate(o => o.DrawImage(
                    colored,
                    PixelColorBlendingMode.Multiply,
                    PixelAlphaCompositionMode.SrcAtop,
                    1f));
            }

            // The first layer determines the canvas dimensions. Subsequent layers whose
            // RSI size differs are drawn at (0,0) and clipped — rare and usually a sign
            // of a sprite bug on the content side, not something we need to handle here.
            if (canvas == null)
                canvas = new Image<Rgba32>(frame.Width, frame.Height);

            canvas.Mutate(o => o.DrawImage(frame, new Point(0, 0), 1f));
        }

        return canvas;
    }

    private readonly record struct EntityMeta(
        string Id,
        string Name,
        string? Suffix,
        List<LayerSnapshot> Layers);

    private readonly record struct LayerSnapshot(
        string RsiPath,
        string StateName,
        int FrameW,
        int FrameH,
        RobustColor LayerColor,
        RobustColor SpriteColor);
}
