using Content.Shared.Pinpointer;

namespace Content.Server.Pinpointer;

public sealed partial class NavMapSystem
{
    public void SetBeaconText(EntityUid uid, string? text, NavMapBeaconComponent? comp = null)
    {
        if (!Resolve(uid, ref comp) || comp.Text == text)
            return;

        comp.Text = text;
        Dirty(uid, comp);
        UpdateNavMapBeaconData(uid, comp);
    }

    public void SetBeaconColor(EntityUid uid, Color color, NavMapBeaconComponent? comp = null)
    {
        if (!Resolve(uid, ref comp) || comp.Color == color)
            return;

        comp.Color = color;
        Dirty(uid, comp);
        UpdateNavMapBeaconData(uid, comp);
    }
}
