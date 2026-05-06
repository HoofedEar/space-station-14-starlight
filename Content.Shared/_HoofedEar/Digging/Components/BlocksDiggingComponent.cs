using Robust.Shared.GameStates;

namespace Content.Shared._HoofedEar.Digging.Components;

/// <summary>
/// Marker placed on anchored entities that should prevent a <see cref="TileDiggingComponent"/>
/// tool from acting on the tile they occupy. Useful for non-physical structures (soil, etc.)
/// that <see cref="Maps.TurfSystem.IsTileBlocked"/> won't catch on its own.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class BlocksDiggingComponent : Component
{
}
