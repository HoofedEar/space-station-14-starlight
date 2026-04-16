#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.MapRenderer.Painters;
using RobustMapEditor.Core.Documents;
using RobustMapEditor.Core.Session;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static Robust.UnitTesting.RobustIntegrationTest;

namespace RobustMapEditor.Core.Rendering;

/// <summary>
/// Renders a <see cref="DocumentSession"/>'s live grids into one <see cref="RenderedGrid"/>
/// per grid. Unlike <c>Content.MapRenderer.MapPainter</c>, this renderer does NOT own
/// or initialize a pair — it reuses the session's long-lived pair so subsequent re-renders
/// (after tile/entity edits) can run without re-parsing YAML.
/// </summary>
/// <remarks>
/// Painters are reconstructed on every render pass because <see cref="GridPainter"/>
/// snapshots entity and decal data in its constructor. Caching painters across passes
/// would render stale state after edits. A single pass that touches multiple grids
/// reuses the same painter pair — that's why <see cref="RenderGridsAsync"/> builds them
/// once per call rather than once per grid.
/// </remarks>
public sealed class MapRenderer
{
    /// <summary>
    /// Render every grid in the given session. Returns one <see cref="RenderedGrid"/>
    /// per non-empty grid, sorted in the order the session lists them.
    /// </summary>
    public static Task<List<RenderedGrid>> RenderAsync(DocumentSession session)
        => RenderGridsAsync(session, gridFilter: null);

    /// <summary>
    /// Phase 2C partial re-render: rebuild <see cref="RenderedGrid"/>s for only the
    /// grids in <paramref name="gridUids"/>. Grids in the session that aren't in the
    /// set are skipped entirely — callers are expected to splice the returned list
    /// back into their existing grid list, preserving untouched entries.
    /// </summary>
    /// <remarks>
    /// Entries in <paramref name="gridUids"/> that don't correspond to a grid in the
    /// session are silently ignored. An empty set returns an empty list without
    /// booting the painters.
    /// </remarks>
    public static Task<List<RenderedGrid>> RenderGridsAsync(
        DocumentSession session,
        IReadOnlyCollection<EntityUid> gridUids)
    {
        if (gridUids.Count == 0)
            return Task.FromResult(new List<RenderedGrid>());

        // HashSet gives O(1) membership checks in the per-grid loop.
        var filter = gridUids as HashSet<EntityUid> ?? new HashSet<EntityUid>(gridUids);
        return RenderGridsAsync(session, filter);
    }

    private static async Task<List<RenderedGrid>> RenderGridsAsync(
        DocumentSession session,
        HashSet<EntityUid>? gridFilter)
    {
        var pair = session.Pair;
        var server = pair.Server;
        var client = pair.Client;

        // Settle server→client replication before GridPainter snapshots entities.
        // GridPainter.GetEntities walks server entities but reads each one's client-side
        // SpriteComponent via the net-entity mapping; a brand-new server entity has no
        // client mirror until the client has both received the state AND run a tick to
        // materialise components. One tick each is not enough in practice — the server
        // queues state delivery on its tick, the client processes it on the next tick,
        // and SpriteComponent doesn't show up on the client until a subsequent idle.
        // MapPainter uses 10 for the same reason. We pick 5 as a compromise: enough for
        // network settling without making every click feel laggy, matching the PoolManager
        // default. Covers PlaceEntityCommand, DeleteEntityCommand, and anything future
        // that mutates server state before a render.
        await pair.RunTicksSync(5);
        await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());

        var sEntityManager = server.ResolveDependency<IEntityManager>();
        var mapSys = sEntityManager.System<SharedMapSystem>();
        var xformSystem = sEntityManager.System<SharedTransformSystem>();

        var tilePainter = new TilePainter(client, server);
        var gridPainter = new GridPainter(client, server);

        var results = new List<RenderedGrid>(
            gridFilter?.Count ?? session.Grids.Count);

        foreach (var (uid, grid) in session.Grids)
        {
            if (gridFilter != null && !gridFilter.Contains(uid))
                continue;

            var rendered = await RenderOneGridAsync(
                server, mapSys, xformSystem, tilePainter, gridPainter, uid, grid);
            if (rendered != null)
                results.Add(rendered);
        }

        return results;
    }

    private static async Task<RenderedGrid?> RenderOneGridAsync(
        ServerIntegrationInstance server,
        SharedMapSystem mapSys,
        SharedTransformSystem xformSystem,
        TilePainter tilePainter,
        GridPainter gridPainter,
        EntityUid uid,
        MapGridComponent grid)
    {
        // Same canvas-size math as MapPainter.Paint — one pixel per (tileSize * 32)
        // unit, bounded by the grid's populated tile rect.
        var tiles = mapSys.GetAllTiles(uid, grid).ToList();
        if (tiles.Count == 0)
            return null;

        var tileXSize = grid.TileSize * TilePainter.TileImageSize;
        var tileYSize = grid.TileSize * TilePainter.TileImageSize;

        var minX = tiles.Min(t => t.X);
        var minY = tiles.Min(t => t.Y);
        var maxX = tiles.Max(t => t.X);
        var maxY = tiles.Max(t => t.Y);
        var w = (maxX - minX + 1) * tileXSize;
        var h = (maxY - minY + 1) * tileYSize;

        // MapGrids have no LocalAABB, so we push painter output so the grid's
        // populated region starts at canvas (0,0).
        var customOffset = new Vector2();
        if (grid.LocalAABB.IsEmpty())
            customOffset = new Vector2(-minX, -minY);

        var canvas = new Image<Rgba32>(w, h);

        await server.WaitPost(() =>
        {
            tilePainter.Run(canvas, uid, grid, customOffset);
            gridPainter.Run(canvas, uid, grid, customOffset);

            // Painters write in grid-local (y-up) space; ImageSharp is y-down.
            canvas.Mutate(e => e.Flip(FlipMode.Vertical));
        });

        var worldPos = xformSystem.GetWorldPosition(uid);

        return new RenderedGrid(
            uid,
            worldPos,
            new Vector2i(minX, minY),
            new Vector2i(maxX - minX + 1, maxY - minY + 1),
            canvas);
    }
}
