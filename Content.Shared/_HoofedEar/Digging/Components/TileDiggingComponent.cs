using Content.Shared.Maps;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._HoofedEar.Digging.Components;

/// <summary>
/// Marks a tool as able to dig out specific tile types, swap them for their <see cref="ContentTileDefinition.BaseTurf"/>
/// (or a configured replacement), and optionally drop an entity.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class TileDiggingComponent : Component
{
    /// <summary>
    /// Tile prototypes this tool can dig.
    /// </summary>
    [DataField(required: true)]
    public List<ProtoId<ContentTileDefinition>> DiggableTiles = new();

    /// <summary>
    /// Entity to spawn at the dug tile on completion. Null = no drop.
    /// </summary>
    [DataField]
    public EntProtoId? Spawns;

    /// <summary>
    /// Optional override of the resulting tile. If null, falls back to the dug tile's
    /// <see cref="ContentTileDefinition.BaseTurf"/>.
    /// </summary>
    [DataField]
    public ProtoId<ContentTileDefinition>? ReplacementTile;

    /// <summary>
    /// Base time required to dig a tile. Modified by <see cref="Burial.Components.ShovelComponent.SpeedModifier"/>
    /// when present on the same entity.
    /// </summary>
    [DataField]
    public TimeSpan Delay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Looped sound played while digging.
    /// </summary>
    [DataField]
    public SoundSpecifier? DigSound = new SoundPathSpecifier("/Audio/Items/shovel_dig.ogg")
    {
        Params = AudioParams.Default.WithLoop(true),
    };

    /// <summary>
    /// Active looped audio stream for the current dig. Server-only transient state.
    /// </summary>
    [ViewVariables]
    public EntityUid? Stream;
}
