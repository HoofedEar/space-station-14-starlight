using System.Numerics;
using Content.Shared.Parallax.Biomes.Markers;
using Content.Shared.Procedural;
using Robust.Shared.Prototypes;

namespace Content.Server._Starlight.Station;

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
}
