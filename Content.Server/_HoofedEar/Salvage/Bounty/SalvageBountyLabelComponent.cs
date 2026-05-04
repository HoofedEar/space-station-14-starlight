namespace Content.Server._HoofedEar.Salvage.Bounty;

/// <summary>
/// Marks a paper label as belonging to a specific salvage bounty. When attached
/// to a crate that gets sold via the salvage sale console, completion is checked
/// and the bounty reward is paid out.
/// </summary>
[RegisterComponent]
public sealed partial class SalvageBountyLabelComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public string Id = string.Empty;

    [DataField]
    public EntityUid? AssociatedStationId;
}
