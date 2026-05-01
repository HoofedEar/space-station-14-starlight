using System.Numerics;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Parallax.Biomes.Markers;
using Content.Shared.Procedural;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Starlight.Station;

public sealed class StationDropshipPlanetSystem : EntitySystem
{
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly BiomeSystem _biome = default!;
    [Dependency] private readonly DungeonSystem _dungeon = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefMan = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationDropshipPlanetComponent, StationPostInitEvent>(
            OnPostInit,
            after: new[] { typeof(StationBiomeSystem) });
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
        if (!TryComp<BiomeComponent>(mapUid, out var biome))
            return;

        var landing = Vector2.Zero;
        if (TryFindLandingZone((mapUid, mapGrid), ent.Comp, out var found))
        {
            landing = found;
            _transform.SetCoordinates(gridUid, new EntityCoordinates(mapUid, landing));
        }
        else
        {
            Log.Warning(
                $"StationDropshipPlanet: no allowed-biome tile found within radius {ent.Comp.MaxScanRadius}; leaving dropship at origin");
        }

        AddMarkers(mapUid, biome, ent.Comp.OreMarkers);
        AddMarkers(mapUid, biome, ent.Comp.MobMarkers);
        ScatterDungeons((mapUid, mapGrid), landing, ent.Comp);
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

    private void ScatterDungeons(
        Entity<MapGridComponent> map,
        Vector2 origin,
        StationDropshipPlanetComponent comp)
    {
        if (comp.DungeonConfigs.Count == 0)
            return;

        var count = _random.Next(comp.DungeonCountMin, comp.DungeonCountMax + 1);
        var landingSiteEnabled = comp.LandingSiteOffsetMax > 0f;
        for (var i = 0; i < count; i++)
        {
            var configId = _random.Pick(comp.DungeonConfigs);
            if (!_proto.TryIndex(configId, out var config))
            {
                Log.Warning($"StationDropshipPlanet: unknown dungeon config '{configId}'");
                continue;
            }

            var (offMin, offMax) = (i == 0 && landingSiteEnabled)
                ? (comp.LandingSiteOffsetMin, comp.LandingSiteOffsetMax)
                : (comp.DungeonOffsetMin, comp.DungeonOffsetMax);

            var angle = _random.NextAngle();
            var distance = _random.NextFloat(offMin, offMax);
            var pos = (Vector2i)(origin + angle.ToVec() * distance);
            var seed = _random.Next();

            try
            {
                _dungeon.GenerateDungeon(config, map.Owner, map.Comp, pos, seed);
            }
            catch (Exception e)
            {
                Log.Warning($"StationDropshipPlanet: failed to generate dungeon {configId} at {pos}: {e.Message}");
            }
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
