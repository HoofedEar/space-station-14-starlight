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
/// Deletes one entity. Undo re-spawns it from its prototype at its original
/// grid-local coordinates, producing a fresh <see cref="EntityUid"/>.
/// </summary>
/// <remarks>
/// <b>Known limitation (Phase 3a):</b> undo re-spawns from the prototype only —
/// any component overrides that had been written into the entity (whether from
/// the source YAML or future in-editor component edits) are lost. Once the
/// Phase 3b inspector arrives and we know how to capture/apply component deltas,
/// this command should snapshot them on apply and reapply them after the
/// re-spawn.
/// </remarks>
public sealed class DeleteEntityCommand : IEditCommand
{
    private readonly EntityUid _targetUid;

    // Captured on first apply so the command can be re-applied after an undo.
    private string? _prototypeId;
    private EntityUid _parentGridUid;
    private Vector2 _localCoords;
    private EntityUid[] _affectedGrids = Array.Empty<EntityUid>();

    /// <summary>Uid of the entity that currently represents this command's target — the
    /// original on first apply, a respawned replacement after undo/redo. Null between
    /// apply (entity deleted) and undo (respawned).</summary>
    private EntityUid? _liveTarget;

    public string Description => _prototypeId != null
        ? $"Delete {_prototypeId}"
        : "Delete entity";

    public IReadOnlyCollection<EntityUid> AffectedGrids => _affectedGrids;

    public DeleteEntityCommand(EntityUid targetUid)
    {
        _targetUid = targetUid;
        _liveTarget = targetUid;
    }

    public async Task ApplyAsync(DocumentSession session)
    {
        var server = session.Pair.Server;
        await server.WaitPost(() =>
        {
            var em = server.ResolveDependency<IEntityManager>();

            // Resolve the target. After a redo, the original _targetUid is stale and we
            // use the most recently re-spawned uid instead.
            var target = _liveTarget ?? _targetUid;
            if (!em.EntityExists(target))
                throw new InvalidOperationException("DeleteEntityCommand target no longer exists.");

            // First apply: capture prototype + grid-local coords for re-spawn on undo.
            if (_prototypeId == null)
            {
                var meta = em.GetComponent<MetaDataComponent>(target);
                _prototypeId = meta.EntityPrototype?.ID
                    ?? throw new InvalidOperationException(
                        "DeleteEntityCommand target has no prototype — can't be undone safely.");

                var xform = em.GetComponent<TransformComponent>(target);
                if (xform.ParentUid == EntityUid.Invalid || !em.HasComponent<Robust.Shared.Map.Components.MapGridComponent>(xform.ParentUid))
                    throw new InvalidOperationException(
                        "DeleteEntityCommand target is not grid-parented — unsupported in Phase 3a.");

                _parentGridUid = xform.ParentUid;
                _localCoords = xform.LocalPosition;
                _affectedGrids = new[] { _parentGridUid };
            }

            em.DeleteEntity(target);
            _liveTarget = null;
        });
    }

    public async Task UndoAsync(DocumentSession session)
    {
        if (_prototypeId == null)
            throw new InvalidOperationException("Cannot undo a DeleteEntityCommand that has never been applied.");

        var server = session.Pair.Server;
        await server.WaitPost(() =>
        {
            var em = server.ResolveDependency<IEntityManager>();
            var coords = new EntityCoordinates(_parentGridUid, _localCoords);
            _liveTarget = em.SpawnEntity(_prototypeId, coords);
        });
    }
}
