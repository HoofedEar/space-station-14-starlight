using Content.Shared._HoofedEar.Salvage.Bounty;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._HoofedEar.Salvage.Bounty;

/// <summary>
/// Stores active and historical salvage bounties for a station's bounty board.
/// </summary>
[RegisterComponent]
public sealed partial class StationSalvageBountyDatabaseComponent : Component
{
    [DataField]
    public int MaxBounties = 6;

    [DataField]
    public List<SalvageBountyData> Bounties = new();

    [DataField]
    public List<SalvageBountyHistoryData> History = new();

    /// <summary>
    /// Running counter used to assign unique ids to bounties.
    /// </summary>
    [DataField]
    public int TotalBounties;

    [DataField]
    public ProtoId<SalvageBountyGroupPrototype> Group = "SalvageBoardBounty";

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan NextSkipTime = TimeSpan.Zero;

    [DataField]
    public TimeSpan SkipDelay = TimeSpan.FromMinutes(15);
}
