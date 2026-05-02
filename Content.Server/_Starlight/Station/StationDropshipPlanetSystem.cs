using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Chat.Managers;
using Content.Server.Parallax;
using Content.Server.Pinpointer;
using Content.Server.Procedural;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Localizations;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Markers;
using Content.Shared.Pinpointer;
using Content.Shared.Procedural;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Starlight.Station;

public sealed class StationDropshipPlanetSystem : EntitySystem
{
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly BiomeSystem _biome = default!;
    [Dependency] private readonly DungeonSystem _dungeon = default!;
    [Dependency] private readonly NavMapSystem _navMap = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefMan = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IChatManager _chat = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationDropshipPlanetComponent, StationPostInitEvent>(
            OnPostInit,
            after: new[] { typeof(StationBiomeSystem) });
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawn);
    }

    private void OnPostInit(Entity<StationDropshipPlanetComponent> ent, ref StationPostInitEvent args)
    {
        var grid = _station.GetLargestGrid(ent.Owner);
        if (grid is not { } gridUid)
            return;

        var mapId = Transform(gridUid).MapID;
        var mapUid = _map.GetMapOrInvalid(mapId);

        if (!TryComp<MapGridComponent>(mapUid, out var mapGrid))
            return;
        if (!TryComp<MapGridComponent>(gridUid, out var dropshipGrid))
            return;
        if (!TryComp<BiomeComponent>(mapUid, out var biome))
            return;

        var landing = Vector2.Zero;
        if (TryFindLandingZone((mapUid, mapGrid), ent.Comp, out var found))
        {
            landing = found;
            _transform.SetCoordinates(gridUid, new EntityCoordinates(mapUid, landing));
            // mining_new.yml is authored at a non-zero angle; snap to cardinal so the
            // ship sits axis-aligned on the planet instead of skewed diagonally.
            _transform.SetLocalRotation(gridUid, Angle.Zero);
        }
        else
        {
            Log.Warning(
                $"StationDropshipPlanet: no allowed-biome tile found within radius {ent.Comp.MaxScanRadius}; leaving dropship at origin");
        }

        AddMarkers(mapUid, biome, ent.Comp.OreMarkers);
        AddMarkers(mapUid, biome, ent.Comp.MobMarkers);
        ent.Comp.PlanetMap = mapUid;

        // Half-diagonal of the dropship AABB is the worst-case overlap radius for a
        // dungeon center placed in any direction. Floor every dungeon offset at this.
        var aabb = dropshipGrid.LocalAABB;
        var dropshipRadius = MathF.Sqrt(aabb.Width * aabb.Width + aabb.Height * aabb.Height) * 0.5f;
        var minDungeonDistance = dropshipRadius + ent.Comp.DungeonClearance;

        _ = ScatterAndClearAsync((mapUid, mapGrid), (gridUid, dropshipGrid), landing, ent.Comp, minDungeonDistance);
    }

    private void OnPlayerSpawn(PlayerSpawnCompleteEvent args)
    {
        if (!TryComp<StationDropshipPlanetComponent>(args.Station, out var comp))
            return;
        if (comp.Announced)
            return;
        if (comp.LandingSiteDirection == Vector2.Zero)
            return;
        if (!TryComp<MapComponent>(comp.PlanetMap, out var mapComp))
            return;

        var dir = ContentLocalizationManager.FormatDirection(comp.LandingSiteDirection.GetDir()).ToLower();
        var msg = Loc.GetString("continental-drop-announcement-dungeon", ("direction", dir));
        _chat.ChatMessageToManyFiltered(
            Filter.BroadcastMap(mapComp.MapId),
            ChatChannel.Radio,
            msg,
            msg,
            comp.PlanetMap,
            false,
            true,
            null);

        comp.Announced = true;
    }

    private void AddMarkers(
        EntityUid mapUid,
        BiomeComponent biome,
        List<ProtoId<BiomeMarkerLayerPrototype>> markers)
    {
        foreach (var marker in markers)
        {
            _biome.AddMarkerLayer(mapUid, biome, marker);
        }
    }

    private async Task ScatterAndClearAsync(
        Entity<MapGridComponent> map,
        Entity<MapGridComponent> dropship,
        Vector2 origin,
        StationDropshipPlanetComponent comp,
        float minDistance)
    {
        // Pre-mark the dropship's footprint so dungeon generators skip these tiles
        // entirely. Combined with the post-clear sweep this is belt-and-suspenders:
        // pre-mark deflects the dungeon's own placements, post-clear catches biome
        // rocks and any anchored entities the pre-mark didn't cover.
        var reservedTiles = ComputeLandingReservedTiles(dropship, origin, comp.LandingZoneBuffer);

        var tasks = new List<Task>();
        var dungeons = new List<(Vector2i Pos, ProtoId<DungeonConfigPrototype> ConfigId, bool IsLandingSite)>();

        if (comp.DungeonConfigs.Count > 0)
        {
            var count = _random.Next(comp.DungeonCountMin, comp.DungeonCountMax + 1);
            var landingSiteEnabled = comp.LandingSiteOffsetMax > 0f;
            // Cardinal direction for the landing-site dungeon (mirrors salvage's GetDungeonRotation)
            // so players have a single seed-determined "walk this way" cue rather than searching every direction.
            var landingAngle = new Angle(Math.PI / 2 * _random.Next(0, 4));
            for (var i = 0; i < count; i++)
            {
                var configId = _random.Pick(comp.DungeonConfigs);
                if (!_proto.TryIndex(configId, out var config))
                {
                    Log.Warning($"StationDropshipPlanet: unknown dungeon config '{configId}'");
                    continue;
                }

                var isLandingSite = i == 0 && landingSiteEnabled;
                var (offMin, offMax) = isLandingSite
                    ? (comp.LandingSiteOffsetMin, comp.LandingSiteOffsetMax)
                    : (comp.DungeonOffsetMin, comp.DungeonOffsetMax);

                offMin = MathF.Max(offMin, minDistance);
                offMax = MathF.Max(offMax, offMin + 1f);

                var angle = isLandingSite ? landingAngle : _random.NextAngle();
                var distance = _random.NextFloat(offMin, offMax);
                var offset = angle.ToVec() * distance;
                var pos = (Vector2i)(origin + offset);
                var seed = _random.Next();

                if (isLandingSite)
                    comp.LandingSiteDirection = offset;

                dungeons.Add((pos, configId, isLandingSite));
                tasks.Add(GenerateOne(config, map, pos, seed, configId, reservedTiles));
            }
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception e)
        {
            Log.Warning($"StationDropshipPlanet: dungeon job(s) failed: {e.Message}");
        }

        if (!Exists(map.Owner) || !Exists(dropship.Owner))
            return;

        ClearLandingZone(map, dropship, origin, comp.LandingZoneBuffer);
        SpawnDungeonBeacons(map, dungeons, comp);
    }

    private void SpawnDungeonBeacons(
        Entity<MapGridComponent> map,
        List<(Vector2i Pos, ProtoId<DungeonConfigPrototype> ConfigId, bool IsLandingSite)> dungeons,
        StationDropshipPlanetComponent comp)
    {
        foreach (var (pos, configId, isLandingSite) in dungeons)
        {
            // Centre of the tile so the beacon snaps cleanly when anchored.
            var coords = new EntityCoordinates(map.Owner, pos + new Vector2(0.5f, 0.5f));
            var beacon = Spawn(comp.DungeonBeaconPrototype, coords);

            if (!TryComp<NavMapBeaconComponent>(beacon, out var navBeacon))
                continue;

            // Landing-site dungeon gets a dedicated label so the radio cue and the
            // map marker line up; otherwise look up a friendly name per config and
            // fall back to the raw config id when none is localised.
            var key = isLandingSite
                ? "dungeon-beacon-landing-site"
                : $"dungeon-beacon-{configId.Id.ToLowerInvariant()}";
            var text = Loc.TryGetString(key, out var label) ? label : configId.Id;
            _navMap.SetBeaconText(beacon, text, navBeacon);
            if (isLandingSite)
                _navMap.SetBeaconColor(beacon, Color.Cyan, navBeacon);
        }
    }

    private async Task GenerateOne(
        DungeonConfigPrototype config,
        Entity<MapGridComponent> map,
        Vector2i pos,
        int seed,
        ProtoId<DungeonConfigPrototype> configId,
        IReadOnlySet<Vector2i> reservedTiles)
    {
        try
        {
            await _dungeon.GenerateDungeonAsync(config, map.Owner, map.Comp, pos, seed, reservedTiles);
        }
        catch (Exception e)
        {
            Log.Warning($"StationDropshipPlanet: failed to generate dungeon {configId} at {pos}: {e.Message}");
        }
    }

    private HashSet<Vector2i> ComputeLandingReservedTiles(
        Entity<MapGridComponent> dropship,
        Vector2 landing,
        float buffer)
    {
        var area = dropship.Comp.LocalAABB.Translated(landing).Enlarged(buffer);
        var reserved = new HashSet<Vector2i>();
        var minX = (int)MathF.Floor(area.Left);
        var maxX = (int)MathF.Ceiling(area.Right);
        var minY = (int)MathF.Floor(area.Bottom);
        var maxY = (int)MathF.Ceiling(area.Top);
        for (var x = minX; x < maxX; x++)
        {
            for (var y = minY; y < maxY; y++)
            {
                reserved.Add(new Vector2i(x, y));
            }
        }
        return reserved;
    }

    private void ClearLandingZone(
        Entity<MapGridComponent> map,
        Entity<MapGridComponent> dropship,
        Vector2 landing,
        float buffer)
    {
        // Dropship is parented to the planet at `landing` with rotation 0, so its
        // local AABB translated by `landing` is the planet-grid area to clear.
        var area = dropship.Comp.LocalAABB.Translated(landing).Enlarged(buffer);

        // Materialize before mutating — deletes / SetTile invalidate the iterator.
        var indices = new List<Vector2i>();
        foreach (var tile in _map.GetLocalTilesIntersecting(map.Owner, map.Comp, area))
        {
            indices.Add(tile.GridIndices);
        }

        foreach (var idx in indices)
        {
            var anchored = new List<EntityUid>(_map.GetAnchoredEntities((map.Owner, map.Comp), idx));
            foreach (var uid in anchored)
            {
                QueueDel(uid);
            }

            // Replace with the biome's natural tile so the area looks like ground,
            // not space — SetTile marks the tile as modified, so the biome won't
            // re-fill it on its own if we leave it Empty.
            var replacement = _biome.TryGetBiomeTile(map.Owner, map.Comp, idx, out var biomeTile)
                ? biomeTile.Value
                : Tile.Empty;
            _map.SetTile(map.Owner, map.Comp, idx, replacement);
        }
    }

    private bool TryFindLandingZone(
        Entity<MapGridComponent> map,
        StationDropshipPlanetComponent comp,
        out Vector2 position)
    {
        var step = Math.Max(1, comp.ScanStep);
        var max = comp.MaxScanRadius;

        if (IsAllowed(map, Vector2i.Zero, comp))
        {
            position = Vector2.Zero;
            return true;
        }

        for (var r = step; r <= max; r += step)
        {
            for (var x = -r; x <= r; x += step)
            {
                if (IsAllowed(map, new Vector2i(x, -r), comp))
                {
                    position = new Vector2(x, -r);
                    return true;
                }
                if (IsAllowed(map, new Vector2i(x, r), comp))
                {
                    position = new Vector2(x, r);
                    return true;
                }
            }
            for (var y = -r + step; y < r; y += step)
            {
                if (IsAllowed(map, new Vector2i(-r, y), comp))
                {
                    position = new Vector2(-r, y);
                    return true;
                }
                if (IsAllowed(map, new Vector2i(r, y), comp))
                {
                    position = new Vector2(r, y);
                    return true;
                }
            }
        }

        position = Vector2.Zero;
        return false;
    }

    private bool IsAllowed(
        Entity<MapGridComponent> map,
        Vector2i indices,
        StationDropshipPlanetComponent comp)
    {
        if (!_biome.TryGetBiomeTile(map.Owner, map.Comp, indices, out var tile))
            return false;
        var defId = _tileDefMan[tile.Value.TypeId].ID;
        return comp.AllowedTiles.Contains(defId);
    }
}
