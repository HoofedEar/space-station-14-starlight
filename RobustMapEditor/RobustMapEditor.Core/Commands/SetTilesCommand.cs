#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RobustMapEditor.Core.Session;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace RobustMapEditor.Core.Commands;

/// <summary>
/// Mutates one or more tiles on a single grid. Covers both single-cell clicks
/// and multi-cell drag strokes — a drag is batched into one command so undo
/// reverses the whole stroke atomically.
/// </summary>
/// <remarks>
/// "Before" tile values are snapshotted on first apply (inside WaitPost so they
/// match the engine's authoritative state). Subsequent apply/undo cycles reuse
/// that snapshot. Coordinates in the same command are assumed unique — callers
/// deduplicate during stroke collection.
/// </remarks>
public sealed class SetTilesCommand : IEditCommand
{
    private readonly EntityUid _gridUid;
    private readonly EntityUid[] _affectedGrids;
    private readonly (Vector2i Coords, Tile NewTile)[] _edits;
    private Tile[]? _oldTiles;

    public string Description =>
        _edits.Length == 1 ? "Set tile" : $"Paint {_edits.Length} tiles";

    public IReadOnlyCollection<EntityUid> AffectedGrids => _affectedGrids;

    /// <summary>Construct a command from a sequence of (coord, new-tile) pairs. Callers
    /// must deduplicate by coord before constructing — the last write wins semantically
    /// but we still record one before/after per entry, which bloats undo memory.</summary>
    public SetTilesCommand(EntityUid gridUid, IEnumerable<(Vector2i Coords, Tile NewTile)> edits)
    {
        _gridUid = gridUid;
        _affectedGrids = new[] { gridUid };
        _edits = edits.ToArray();
        if (_edits.Length == 0)
            throw new ArgumentException("SetTilesCommand needs at least one edit.", nameof(edits));
    }

    public async Task ApplyAsync(DocumentSession session)
    {
        var server = session.Pair.Server;
        await server.WaitPost(() =>
        {
            var em = server.ResolveDependency<IEntityManager>();
            var mapSys = em.System<SharedMapSystem>();
            var grid = em.GetComponent<MapGridComponent>(_gridUid);

            // First apply: capture before-state. Do this inside WaitPost so the read
            // and subsequent writes are consistent with the server's tick.
            if (_oldTiles == null)
            {
                var snapshot = new Tile[_edits.Length];
                for (var i = 0; i < _edits.Length; i++)
                    snapshot[i] = mapSys.GetTileRef(_gridUid, grid, _edits[i].Coords).Tile;
                _oldTiles = snapshot;
            }

            for (var i = 0; i < _edits.Length; i++)
                mapSys.SetTile(_gridUid, grid, _edits[i].Coords, _edits[i].NewTile);
        });
    }

    public async Task UndoAsync(DocumentSession session)
    {
        if (_oldTiles == null)
            throw new InvalidOperationException("Cannot undo a command that has never been applied.");

        var server = session.Pair.Server;
        await server.WaitPost(() =>
        {
            var em = server.ResolveDependency<IEntityManager>();
            var mapSys = em.System<SharedMapSystem>();
            var grid = em.GetComponent<MapGridComponent>(_gridUid);

            for (var i = 0; i < _edits.Length; i++)
                mapSys.SetTile(_gridUid, grid, _edits[i].Coords, _oldTiles[i]);
        });
    }
}
