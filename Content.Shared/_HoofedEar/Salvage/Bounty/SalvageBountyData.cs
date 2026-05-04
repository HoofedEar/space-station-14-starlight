using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._HoofedEar.Salvage.Bounty;

/// <summary>
/// One active salvage bounty, stored on the station database.
/// </summary>
[DataDefinition, NetSerializable, Serializable]
public readonly partial record struct SalvageBountyData
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public string Id { get; init; } = string.Empty;

    [DataField(required: true), ViewVariables(VVAccess.ReadWrite)]
    public ProtoId<SalvageBountyPrototype> Bounty { get; init; } = string.Empty;

    public SalvageBountyData(SalvageBountyPrototype bounty, int uniqueIdentifier)
    {
        Bounty = bounty.ID;
        Id = $"{bounty.IdPrefix}{uniqueIdentifier:D3}";
    }
}
