namespace Content.Server._Starlight.Station;

/// <summary>
/// Translates the station's largest grid (the dropship) onto a tile of the
/// underlying biome whose tile def matches <see cref="AllowedTiles"/>, after
/// the planet has been generated. Used by the Continental drop gamemode to
/// guarantee players land in the Grasslands sub-biome.
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
}
