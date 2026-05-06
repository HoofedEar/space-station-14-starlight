using System.Numerics;
using Content.Shared.Parallax.Biomes.Markers;
using Content.Shared.Procedural;
using Robust.Shared.Prototypes;

namespace Content.Server._HoofedEar.Station;

/// <summary>
/// Translates the station's largest grid (the dropship) onto a tile of the
/// underlying biome whose tile def matches <see cref="AllowedTiles"/>, after
/// the planet has been generated. Used by the Continental drop gamemode to
/// guarantee players land in the Grasslands sub-biome. Optionally enriches
/// the planet with ore markers, mob markers, and scattered dungeons.
/// </summary>
[RegisterComponent]
public sealed partial class StationDropshipPlanetComponent : Component
{
    [DataField]
    public HashSet<string> AllowedTiles = new()
    {
        "FloorPlanetGrass",
        "FloorPlanetDirt",
    };

    [DataField]
    public int ScanStep = 8;

    [DataField]
    public int MaxScanRadius = 4096;

    [DataField]
    public List<ProtoId<BiomeMarkerLayerPrototype>> OreMarkers = new();

    [DataField]
    public List<ProtoId<BiomeMarkerLayerPrototype>> MobMarkers = new();

    [DataField]
    public List<ProtoId<DungeonConfigPrototype>> DungeonConfigs = new();

    [DataField]
    public int DungeonCountMin = 3;

    [DataField]
    public int DungeonCountMax = 5;

    [DataField]
    public float DungeonOffsetMin = 60f;

    [DataField]
    public float DungeonOffsetMax = 160f;

    /// <summary>
    /// Padding (in tiles) added beyond the dropship's bounding radius before any
    /// dungeon may be placed. Floors both <see cref="DungeonOffsetMin"/> and
    /// <see cref="LandingSiteOffsetMin"/> at runtime so a dungeon can never
    /// generate on top of the ship regardless of YAML config.
    /// </summary>
    [DataField]
    public float DungeonClearance = 16f;

    /// <summary>
    /// Buffer (in tiles) added around the dropship's AABB when sweeping the planet
    /// grid for dungeon walls / biome rocks to remove after dungeons are placed.
    /// Belt-and-suspenders to keep airlocks and exit ramps unblocked even when a
    /// dungeon's irregular shape pokes farther than its center-distance suggested.
    /// </summary>
    [DataField]
    public float LandingZoneBuffer = 3f;

    /// <summary>
    /// If non-zero, the first dungeon is placed in this range relative to the
    /// dropship instead of the normal scatter range, guaranteeing one nearby
    /// landing-site dungeon. Set to 0 to disable.
    /// </summary>
    [DataField]
    public float LandingSiteOffsetMin = 0f;

    [DataField]
    public float LandingSiteOffsetMax = 0f;

    /// <summary>
    /// Offset of the landing-site dungeon from the dropship, written by
    /// <c>StationDropshipPlanetSystem</c> after world generation. Used to
    /// announce the cardinal direction over comms once players spawn.
    /// </summary>
    [DataField]
    public Vector2 LandingSiteDirection = Vector2.Zero;

    /// <summary>
    /// The planet map entity hosting the dropship; cached for the
    /// player-spawn announcement filter.
    /// </summary>
    [DataField]
    public EntityUid PlanetMap = EntityUid.Invalid;

    [DataField]
    public bool Announced;

    /// <summary>
    /// Radius in tiles around the dropship and each dungeon center for which
    /// biome marker layers (ore + mob spawn scoring) are eagerly evaluated at
    /// world-gen time. Without this, the first time a player walks into a
    /// virgin marker chunk the server tick stalls for several seconds while
    /// <c>BiomeSystem.BuildMarkerChunks</c> scans 15 layers across the new
    /// chunk. Doing it up-front collapses many per-crossing freezes into one
    /// bounded spike just after dungeon generation. Set to 0 to disable.
    /// </summary>
    [DataField]
    public float MarkerPreloadRadius = 128f;

    /// <summary>
    /// Prototype spawned at every generated dungeon's center so handheld
    /// station maps (and anything else reading <c>NavMapBeaconComponent</c>) can
    /// locate them. Spawned anchored on the planet grid after generation.
    /// </summary>
    [DataField]
    public EntProtoId DungeonBeaconPrototype = "DungeonNavBeacon";

    /// <summary>
    /// Prototype spawned on the planet grid at the dropship's center after the
    /// landing zone is cleared, so the ship's location is visible on the planet's
    /// nav map while players are walking the surface.
    /// </summary>
    [DataField]
    public EntProtoId DropshipBeaconPrototype = "DropshipNavBeacon";
}
