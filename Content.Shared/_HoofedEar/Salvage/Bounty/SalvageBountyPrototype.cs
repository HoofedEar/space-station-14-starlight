using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._HoofedEar.Salvage.Bounty;

/// <summary>
/// A salvage bounty: a set of items that must be sold together in a labeled
/// container at the salvage sale console to earn bonus salvage tickets.
/// </summary>
[Prototype]
public sealed partial class SalvageBountyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Bonus salvage tickets paid out when the bounty is fulfilled.
    /// </summary>
    [DataField(required: true)]
    public int Reward;

    /// <summary>
    /// Flavor description shown on the console.
    /// </summary>
    [DataField]
    public LocId Description = string.Empty;

    /// <summary>
    /// Item entries that must be present in the labeled container.
    /// </summary>
    [DataField(required: true)]
    public List<SalvageBountyItemEntry> Entries = new();

    /// <summary>
    /// Prefix prepended to the generated bounty ID (e.g. "SLV").
    /// </summary>
    [DataField]
    public string IdPrefix = "SLV";

    /// <summary>
    /// Group used to bucket this bounty for board rotation.
    /// </summary>
    [DataField]
    public ProtoId<SalvageBountyGroupPrototype> Group = "SalvageBoardBounty";

    /// <summary>
    /// Optional sprite shown on the console entry.
    /// </summary>
    [DataField]
    public SpriteSpecifier? Sprite;
}

[DataDefinition, Serializable, NetSerializable]
public readonly partial record struct SalvageBountyItemEntry()
{
    [DataField(required: true)]
    public EntityWhitelist Whitelist { get; init; } = default!;

    [DataField]
    public EntityWhitelist? Blacklist { get; init; } = null;

    [DataField]
    public int Amount { get; init; } = 1;

    [DataField]
    public LocId Name { get; init; } = string.Empty;
}
