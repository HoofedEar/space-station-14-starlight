using Robust.Shared.Prototypes;

namespace Content.Shared._HoofedEar.Salvage.Bounty;

/// <summary>
/// Used to categorize salvage bounties for different bounty boards.
/// </summary>
[Prototype]
public sealed partial class SalvageBountyGroupPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;
}
