using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._HoofedEar.Digging;

[Serializable, NetSerializable]
public sealed partial class TileDigDoAfterEvent : DoAfterEvent
{
    [DataField] public NetEntity Grid;
    [DataField] public Vector2i Tile;

    public TileDigDoAfterEvent()
    {
    }

    public TileDigDoAfterEvent(NetEntity grid, Vector2i tile)
    {
        Grid = grid;
        Tile = tile;
    }

    public override DoAfterEvent Clone() => this;
}
