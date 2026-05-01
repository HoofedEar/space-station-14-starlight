using System.Numerics;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Starlight.Station;

public sealed class StationDropshipPlanetSystem : EntitySystem
{
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly SharedBiomeSystem _biome = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefMan = default!;

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
        if (!HasComp<BiomeComponent>(mapUid))
            return;

        if (!TryFindLandingZone((mapUid, mapGrid), ent.Comp, out var landing))
        {
            Log.Warning(
                $"StationDropshipPlanet: no allowed-biome tile found within radius {ent.Comp.MaxScanRadius}; leaving dropship at origin");
            return;
        }

        _transform.SetCoordinates(gridUid, new EntityCoordinates(mapUid, landing));
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
