#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using RobustMapEditor.Core.Commands;
using RobustMapEditor.Core.Rendering;
using RobustMapEditor.Core.Session;
using Robust.Shared.GameObjects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RobustMapEditor.Core.Documents;

/// <summary>
/// One grid's worth of rendered pixels plus the world-space metadata needed to
/// place it relative to siblings AND the grid-local tile bounds needed to
/// invert a canvas pixel back to a tile coord for hit-testing. Owned by a
/// <see cref="MapDocument"/>; disposed when the document re-renders or is
/// itself disposed.
/// </summary>
public sealed class RenderedGrid : IDisposable
{
    public EntityUid? GridUid { get; }
    /// <summary>World-space offset of the grid's bottom-left corner, in tile units.</summary>
    public Vector2 WorldOffset { get; }
    /// <summary>Grid-local tile coord corresponding to the image's bottom-left corner
    /// <em>before</em> the vertical flip. i.e. the minimum (X,Y) of populated tiles used
    /// when the renderer sized the canvas.</summary>
    public Robust.Shared.Maths.Vector2i TileMin { get; }
    /// <summary>Number of tiles the image covers along each axis. X == PixelWidth / 32,
    /// Y == PixelHeight / 32.</summary>
    public Robust.Shared.Maths.Vector2i TileCount { get; }
    public Image<Rgba32> Image { get; }

    public int PixelWidth  => Image.Width;
    public int PixelHeight => Image.Height;

    public RenderedGrid(
        EntityUid? gridUid,
        Vector2 worldOffset,
        Robust.Shared.Maths.Vector2i tileMin,
        Robust.Shared.Maths.Vector2i tileCount,
        Image<Rgba32> image)
    {
        GridUid = gridUid;
        WorldOffset = worldOffset;
        TileMin = tileMin;
        TileCount = tileCount;
        Image = image;
    }

    /// <summary>Pixels per world tile for the canvas placement math. Mirrors
    /// <c>EyeManager.PixelsPerMeter</c>.</summary>
    private const double PixelsPerTile = 32.0;

    /// <summary>Hit-test a world-pixel point against this grid and return the corresponding
    /// grid-local tile coord, or null if the point is outside the image. The image was
    /// vertically flipped post-render, so we undo that flip to get back to the engine's
    /// y-up tile coord space.</summary>
    public Robust.Shared.Maths.Vector2i? TryWorldPixelToTile(Vector2 worldPixel)
    {
        var originX = WorldOffset.X * (float)PixelsPerTile;
        var originY = WorldOffset.Y * (float)PixelsPerTile;
        var localX  = worldPixel.X - originX;
        var localY  = worldPixel.Y - originY;

        if (localX < 0 || localY < 0 || localX >= PixelWidth || localY >= PixelHeight)
            return null;

        var canvasTileX        = (int)Math.Floor(localX / PixelsPerTile);
        var canvasTileYFlipped = (int)Math.Floor(localY / PixelsPerTile);
        // Reverse the vertical flip MapRenderer applies.
        var canvasTileY = TileCount.Y - 1 - canvasTileYFlipped;

        return new Robust.Shared.Maths.Vector2i(
            canvasTileX + TileMin.X,
            canvasTileY + TileMin.Y);
    }

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// A map open in the editor. Wraps a <see cref="DocumentSession"/> (live engine pair
/// with the map's entities resident), holds the latest render, and owns the undo/redo
/// history.
/// </summary>
/// <remarks>
/// Phase 2A policy: nothing persists to disk until the user explicitly saves.
/// <see cref="IsDirty"/> is derived from <see cref="History"/>, so undoing back to
/// the saved cursor transparently clears the dirty flag.
/// </remarks>
public sealed class MapDocument : IAsyncDisposable
{
    private readonly DocumentSession _session;
    private readonly MapHistory _history = new();
    private List<RenderedGrid> _grids;

    public string FilePath => _session.FilePath;

    /// <summary>Rendered grids, refreshed after every command via <see cref="RerenderAsync"/>.</summary>
    public IReadOnlyList<RenderedGrid> Grids => _grids;

    /// <summary>Undo/redo stack. Inspect for CanUndo/CanRedo; mutate via
    /// <see cref="ExecuteAsync"/> / <see cref="UndoAsync"/> / <see cref="RedoAsync"/>.</summary>
    public MapHistory History => _history;

    /// <summary>True if the history cursor is past the last save point. Drives UI title + save enable.</summary>
    public bool IsDirty => _history.IsDirty;

    /// <summary>Internal access for save/palette services in the Core assembly. Not
    /// exposed outside — UI layers should use the public convenience methods below.</summary>
    internal DocumentSession Session => _session;

    /// <summary>Build a tile palette for this document. Convenience over reaching into
    /// the session — keeps the session encapsulated inside Core.</summary>
    public Task<List<Tiles.TileCatalogEntry>> BuildTileCatalogAsync()
        => Tiles.TileCatalog.BuildAsync(_session);

    /// <summary>Build the entity-prototype palette for this document. Same encapsulation
    /// rationale as <see cref="BuildTileCatalogAsync"/>.</summary>
    public Task<List<Entities.EntityCatalogEntry>> BuildEntityCatalogAsync()
        => Entities.EntityCatalog.BuildAsync(_session);

    /// <summary>Inspector-panel hit-test: which entities sit on a given tile?
    /// Forwards to <see cref="Entities.EntityInspectionService"/> with the document's
    /// session so the Ui assembly doesn't need access to <c>DocumentSession</c>.</summary>
    public Task<IReadOnlyList<Entities.InspectedEntity>> GetEntitiesAtTileAsync(
        EntityUid gridUid,
        Robust.Shared.Maths.Vector2i tile)
        => Entities.EntityInspectionService.GetEntitiesAtTileAsync(_session, gridUid, tile);

    /// <summary>Refresh a single entity's inspected snapshot. Null if it no longer exists.</summary>
    public Task<Entities.InspectedEntity?> InspectEntityAsync(EntityUid uid)
        => Entities.EntityInspectionService.InspectAsync(_session, uid);

    /// <summary>Grid UIDs in the order the session loaded them. Callers use this to target
    /// edit commands at the correct grid (e.g. "paint on the grid under the cursor").</summary>
    public IReadOnlyList<EntityUid> GridUids
    {
        get
        {
            var list = new List<EntityUid>(_session.Grids.Count);
            foreach (var (uid, _) in _session.Grids)
                list.Add(uid);
            return list;
        }
    }

    private MapDocument(DocumentSession session, List<RenderedGrid> grids)
    {
        _session = session;
        _grids = grids;
    }

    /// <summary>Open and render a map file. Owns the session — disposing the document
    /// disposes the session.</summary>
    public static async Task<MapDocument> OpenAsync(string filePath)
    {
        var session = await DocumentSession.OpenAsync(filePath);
        try
        {
            var rendered = await MapRenderer.RenderAsync(session);
            return new MapDocument(session, rendered);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>Execute a command: apply it, push to history, and re-render only the grids
    /// the command reports as affected. Grids untouched by the command keep their prior
    /// <see cref="RenderedGrid"/> instance, so <see cref="Views.MapCanvas"/>'s bitmap cache
    /// can preserve their Avalonia bitmaps unchanged.</summary>
    public async Task ExecuteAsync(IEditCommand command)
    {
        await _history.ExecuteAsync(command, _session);
        await PartialRerenderAsync(command.AffectedGrids);
    }

    /// <summary>Reverse the most recent command, if any, and re-render its affected grids.</summary>
    public async Task<bool> UndoAsync()
    {
        var cmd = await _history.UndoAsync(_session);
        if (cmd == null) return false;
        await PartialRerenderAsync(cmd.AffectedGrids);
        return true;
    }

    /// <summary>Re-apply the next command in the redo tail, if any, and re-render its
    /// affected grids.</summary>
    public async Task<bool> RedoAsync()
    {
        var cmd = await _history.RedoAsync(_session);
        if (cmd == null) return false;
        await PartialRerenderAsync(cmd.AffectedGrids);
        return true;
    }

    /// <summary>Re-render every grid from current live state. Disposes the old image list
    /// after swap. Used on open; post-command re-renders go through
    /// <see cref="PartialRerenderAsync"/>.</summary>
    public async Task RerenderAsync()
    {
        var newGrids = await MapRenderer.RenderAsync(_session);
        var old = _grids;
        _grids = newGrids;
        foreach (var g in old)
            g.Dispose();
    }

    /// <summary>Re-render only the grids in <paramref name="affectedGrids"/>. Produces a new
    /// <see cref="Grids"/> list that preserves <see cref="RenderedGrid"/> identity for
    /// untouched entries (so downstream bitmap caches can keep them) and disposes the
    /// old images for the grids that were replaced. Grids newly present or newly absent
    /// from the session (e.g. if a future command creates/removes a grid) are handled too.
    /// An empty <paramref name="affectedGrids"/> is a no-op.</summary>
    private async Task PartialRerenderAsync(IReadOnlyCollection<EntityUid> affectedGrids)
    {
        if (affectedGrids.Count == 0)
            return;

        var rerendered = await MapRenderer.RenderGridsAsync(_session, affectedGrids);

        // Key rerendered grids by uid for the merge pass.
        var byUid = new Dictionary<EntityUid, RenderedGrid>(rerendered.Count);
        foreach (var g in rerendered)
            if (g.GridUid is { } uid)
                byUid[uid] = g;

        // Rebuild _grids in the session's grid order. For each live session grid:
        //   - if it was re-rendered, use the new instance and dispose the old one
        //   - otherwise, reuse the existing RenderedGrid instance (identity preserved)
        // Any old entries whose uid is no longer in the session (grid deleted) are disposed.
        var old = _grids;
        var oldByUid = new Dictionary<EntityUid, RenderedGrid>(old.Count);
        foreach (var g in old)
            if (g.GridUid is { } uid)
                oldByUid[uid] = g;

        var next = new List<RenderedGrid>(_session.Grids.Count);
        var keptOldUids = new HashSet<EntityUid>();
        foreach (var (uid, _) in _session.Grids)
        {
            if (byUid.TryGetValue(uid, out var fresh))
            {
                next.Add(fresh);
                // The old entry for this uid, if any, will be disposed below.
            }
            else if (oldByUid.TryGetValue(uid, out var keep))
            {
                next.Add(keep);
                keptOldUids.Add(uid);
            }
            // Else: session has a grid with no previous render (e.g. newly empty/re-added);
            // skip — a full RerenderAsync would pick it up.
        }

        _grids = next;

        // Dispose any old RenderedGrid we didn't carry forward.
        foreach (var g in old)
        {
            if (g.GridUid is { } uid && keptOldUids.Contains(uid))
                continue;
            g.Dispose();
        }
    }

    /// <summary>Snap the saved-cursor to current history position. Call on successful save.</summary>
    public void MarkClean() => _history.MarkSaved();

    public async ValueTask DisposeAsync()
    {
        foreach (var g in _grids)
            g.Dispose();
        _grids = new List<RenderedGrid>();
        await _session.DisposeAsync();
    }
}
