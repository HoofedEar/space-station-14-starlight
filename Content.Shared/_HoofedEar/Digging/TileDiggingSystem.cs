using System.Linq;
using Content.Shared._HoofedEar.Digging.Components;
using Content.Shared.Burial.Components;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared._HoofedEar.Digging;

/// <summary>
/// Handles using a tool with <see cref="TileDiggingComponent"/> on a tile to swap that
/// tile for its base turf and (optionally) drop a configured entity.
/// </summary>
public sealed class TileDiggingSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly TileSystem _tile = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    // Server-authoritative: which DoAfter is currently digging which tile.
    // Used so a fresh dig on the same tile interrupts the in-flight one.
    private readonly Dictionary<(EntityUid Grid, Vector2i Tile), DoAfterId> _activeDigs = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TileDiggingComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<TileDiggingComponent, TileDigDoAfterEvent>(OnDigDoAfter);
    }

    private void OnAfterInteract(EntityUid uid, TileDiggingComponent comp, AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target != null)
            return;

        var coords = args.ClickLocation;
        var mapCoords = _transform.ToMapCoordinates(coords);

        if (!_mapManager.TryFindGridAt(mapCoords, out var gridUid, out var grid))
            return;

        var tileRef = _maps.GetTileRef(gridUid, grid, coords);
        if (_tileDefManager[tileRef.Tile.TypeId] is not ContentTileDefinition tileDef)
            return;

        if (!comp.DiggableTiles.Any(t => t.Id == tileDef.ID))
            return;

        if (_turf.IsTileBlocked(tileRef, CollisionGroup.MobMask) || HasBlockingAnchored(gridUid, grid, tileRef.GridIndices))
        {
            _popup.PopupClient(Loc.GetString("tile-digging-blocked-anchored"), args.User, args.User);
            args.Handled = true;
            return;
        }

        var delay = comp.Delay;
        if (TryComp<ShovelComponent>(uid, out var shovel))
            delay /= shovel.SpeedModifier;

        var ev = new TileDigDoAfterEvent(GetNetEntity(gridUid), tileRef.GridIndices);
        var doAfterArgs = new DoAfterArgs(EntityManager, args.User, delay, ev, uid, used: uid)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        };

        if (_net.IsServer)
        {
            var key = (gridUid, tileRef.GridIndices);
            if (_activeDigs.Remove(key, out var existing))
                _doAfter.Cancel(existing);
        }

        if (!_doAfter.TryStartDoAfter(doAfterArgs, out var newId))
            return;

        if (_net.IsServer && newId is { } id)
            _activeDigs[(gridUid, tileRef.GridIndices)] = id;

        if (comp.DigSound != null && comp.Stream == null)
            comp.Stream = _audio.PlayPredicted(comp.DigSound, uid, args.User)?.Entity;

        args.Handled = true;
    }

    private void OnDigDoAfter(EntityUid uid, TileDiggingComponent comp, TileDigDoAfterEvent args)
    {
        comp.Stream = _audio.Stop(comp.Stream);

        var gridUid = GetEntity(args.Grid);

        if (_net.IsServer)
            _activeDigs.Remove((gridUid, args.Tile));

        if (args.Cancelled || args.Handled)
            return;

        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var tileRef = _maps.GetTileRef(gridUid, grid, args.Tile);
        if (_tileDefManager[tileRef.Tile.TypeId] is not ContentTileDefinition tileDef)
            return;

        if (!comp.DiggableTiles.Any(t => t.Id == tileDef.ID))
            return;

        // World mutation is server-authoritative.
        if (_net.IsClient)
        {
            args.Handled = true;
            return;
        }

        var replacementId = comp.ReplacementTile?.Id;
        if (string.IsNullOrEmpty(replacementId))
            replacementId = tileDef.BaseTurf;

        if (!string.IsNullOrEmpty(replacementId)
            && _proto.TryIndex<ContentTileDefinition>(replacementId, out var replacementDef))
        {
            _tile.ReplaceTile(tileRef, replacementDef, gridUid, grid);
        }

        if (comp.Spawns is { } spawnProto)
        {
            var spawnCoords = _maps.GridTileToLocal(gridUid, grid, args.Tile);
            Spawn(spawnProto, spawnCoords);
        }

        args.Handled = true;
    }

    private bool HasBlockingAnchored(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        var enumerator = _maps.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (enumerator.MoveNext(out var ent))
        {
            if (HasComp<BlocksDiggingComponent>(ent))
                return true;
        }
        return false;
    }
}
