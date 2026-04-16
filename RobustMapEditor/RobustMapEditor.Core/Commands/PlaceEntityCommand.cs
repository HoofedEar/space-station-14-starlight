#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using RobustMapEditor.Core.Session;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace RobustMapEditor.Core.Commands;

/// <summary>
/// Spawns one entity prototype on a grid at a given tile. Undo deletes the
/// spawned entity; redo re-spawns from the prototype (with a fresh
/// <see cref="EntityUid"/>). Intended for Phase 3a click-to-place.
/// </summary>
/// <remarks>
/// Redo semantics: because the engine assigns a new UID on every spawn, we
/// don't attempt to preserve the identity of the originally-placed entity
/// across undo/redo cycles. Other commands in the history that reference the
/// placed entity by UID (e.g. a future "move this entity") would need to track
/// those references separately — not a concern for the first-pass placement
/// tool, which produces independent commands.
/// </remarks>
public sealed class PlaceEntityCommand : IEditCommand
{
    private readonly EntityUid _gridUid;
    private readonly EntityUid[] _affectedGrids;
    private readonly string _prototypeId;
    private readonly Vector2 _localCoords;

    /// <summary>Most recently spawned entity. Cleared on undo, repopulated on redo.
    /// Null before first apply.</summary>
    private EntityUid? _spawned;

    public string Description => $"Place {_prototypeId}";

    public IReadOnlyCollection<EntityUid> AffectedGrids => _affectedGrids;

    /// <summary>
    /// </summary>
    /// <param name="gridUid">Grid the entity is parented to — also reported as the affected
    /// grid so the partial re-render picks it up.</param>
    /// <param name="prototypeId">Prototype ID, e.g. "TableCarpet".</param>
    /// <param name="localCoords">Grid-local position. Tile-centered placement passes
    /// <c>(tile.X + 0.5, tile.Y + 0.5)</c>.</param>
    public PlaceEntityCommand(EntityUid gridUid, string prototypeId, Vector2 localCoords)
    {
        _gridUid = gridUid;
        _affectedGrids = new[] { gridUid };
        _prototypeId = prototypeId;
        _localCoords = localCoords;
    }

    public async Task ApplyAsync(DocumentSession session)
    {
        var server = session.Pair.Server;
        await server.WaitPost(() =>
        {
            var em = server.ResolveDependency<IEntityManager>();
            var coords = new EntityCoordinates(_gridUid, _localCoords);
            _spawned = em.SpawnEntity(_prototypeId, coords);
        });
    }

    public async Task UndoAsync(DocumentSession session)
    {
        if (_spawned is not { } uid)
            throw new InvalidOperationException("Cannot undo a PlaceEntityCommand that has never been applied.");

        var server = session.Pair.Server;
        await server.WaitPost(() =>
        {
            var em = server.ResolveDependency<IEntityManager>();
            // Could have already been deleted by another command or by parent-grid teardown.
            // DeleteEntity is idempotent on an invalid uid, but check to keep our state consistent.
            if (em.EntityExists(uid))
                em.DeleteEntity(uid);
        });

        _spawned = null;
    }
}
