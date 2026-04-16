#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using RobustMapEditor.Core.Session;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace RobustMapEditor.Core.Entities;

/// <summary>
/// Read-only snapshot of one entity on the map, built for the inspector panel.
/// Frozen at the moment it was captured — not a live view. Callers that need
/// fresh data should re-run an inspection query; entity state can change under
/// them as edits apply.
/// </summary>
public sealed record InspectedEntity(
    EntityUid Uid,
    string PrototypeId,
    string Name,
    Vector2 LocalCoords,
    EntityUid ParentGridUid,
    IReadOnlyList<string> ComponentNames);

/// <summary>
/// Resolves "what entities sit on this tile?" and "what does this entity look like?" —
/// the two read-side queries the inspector panel needs. Everything runs against the
/// <b>server</b> side of the document's pair because the server owns authoritative
/// transform/component state; the client side is a replica that may lag by a tick.
/// </summary>
/// <remarks>
/// These are reads, not <see cref="Commands.IEditCommand"/>s — they don't push to the
/// undo stack and don't re-render. Callers still gate against the VM's command
/// semaphore so a lookup running during a long paint stroke can't see half-applied
/// state.
/// </remarks>
public static class EntityInspectionService
{
    /// <summary>Return every grid-parented entity whose AABB intersects the given tile's
    /// world-space unit square on the specified grid. Skips contained items (inventory
    /// in a pocket, parts in a machine) — only entities directly parented to the grid
    /// are returned, so the right-click menu lists things a user would think of as
    /// "on the floor."</summary>
    public static async Task<IReadOnlyList<InspectedEntity>> GetEntitiesAtTileAsync(
        DocumentSession session,
        EntityUid gridUid,
        Vector2i tile)
    {
        var server = session.Pair.Server;
        var result = new List<InspectedEntity>();

        await server.WaitPost(() =>
        {
            var entMan = server.ResolveDependency<IEntityManager>();
            var lookup = entMan.System<EntityLookupSystem>();
            var xform = entMan.System<SharedTransformSystem>();

            if (!entMan.EntityExists(gridUid))
                return;

            // Convert the tile's grid-local unit square into a world-space AABB so the
            // broad-phase query can find things sitting on that tile even if the grid is
            // rotated in world space. (Editor currently snaps grids to identity, but
            // keeping this rotation-aware costs nothing and survives future map layouts.)
            var localAabb = new Box2(tile.X, tile.Y, tile.X + 1, tile.Y + 1);
            var worldMat = xform.GetWorldMatrix(gridUid);
            var worldAabb = worldMat.TransformBox(localAabb);

            var hits = lookup.GetEntitiesIntersecting(gridUid, worldAabb);
            foreach (var uid in hits)
            {
                if (!entMan.TryGetComponent(uid, out TransformComponent? xformComp))
                    continue;

                // Filter to direct grid children. The broadphase can surface held items
                // and container contents at the grid's footprint; those aren't sensible
                // to list at a tile coord.
                if (xformComp.ParentUid != gridUid)
                    continue;

                var inspected = BuildInspected(entMan, uid, xformComp);
                if (inspected != null)
                    result.Add(inspected);
            }

            // Stable alphabetical order. The broad-phase hits come back as a HashSet
            // (undefined order), which would make the context menu jump between clicks.
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        });

        return result;
    }

    /// <summary>Refresh a single entity's inspected snapshot, or return null if it no
    /// longer exists. Called after selection to pick up state changes — e.g. after the
    /// user undoes a deletion, the stale selection can be revalidated.</summary>
    public static async Task<InspectedEntity?> InspectAsync(DocumentSession session, EntityUid uid)
    {
        var server = session.Pair.Server;
        InspectedEntity? result = null;

        await server.WaitPost(() =>
        {
            var entMan = server.ResolveDependency<IEntityManager>();
            if (!entMan.EntityExists(uid))
                return;
            if (!entMan.TryGetComponent(uid, out TransformComponent? xformComp))
                return;
            result = BuildInspected(entMan, uid, xformComp);
        });

        return result;
    }

    /// <summary>Shared construction of an <see cref="InspectedEntity"/> from live
    /// server-side components. Returns null when the entity is missing the metadata
    /// component — in practice that only happens mid-teardown.</summary>
    private static InspectedEntity? BuildInspected(
        IEntityManager entMan,
        EntityUid uid,
        TransformComponent xformComp)
    {
        if (!entMan.TryGetComponent(uid, out MetaDataComponent? meta))
            return null;

        var protoId = meta.EntityPrototype?.ID ?? "(no prototype)";
        var name = string.IsNullOrWhiteSpace(meta.EntityName) ? protoId : meta.EntityName;

        var components = new List<string>();
        foreach (var c in entMan.GetComponents(uid))
            components.Add(c.GetType().Name);
        components.Sort(StringComparer.OrdinalIgnoreCase);

        return new InspectedEntity(
            uid,
            protoId,
            name,
            xformComp.LocalPosition,
            xformComp.ParentUid,
            components);
    }
}
