using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._HoofedEar.Salvage.Bounty;

[DataDefinition, NetSerializable, Serializable]
public readonly partial record struct SalvageBountyHistoryData
{
    [DataField]
    public string Id { get; init; } = string.Empty;

    [DataField]
    public BountyResult Result { get; init; } = BountyResult.Completed;

    [DataField]
    public string? ActorName { get; init; } = default;

    [DataField]
    public TimeSpan Timestamp { get; init; } = TimeSpan.MinValue;

    [DataField(required: true)]
    public ProtoId<SalvageBountyPrototype> Bounty { get; init; } = string.Empty;

    public SalvageBountyHistoryData(SalvageBountyData bounty, BountyResult result, TimeSpan timestamp, string? actorName)
    {
        Bounty = bounty.Bounty;
        Result = result;
        Id = bounty.Id;
        ActorName = actorName;
        Timestamp = timestamp;
    }

    public enum BountyResult
    {
        Completed = 0,
        Skipped = 1,
    }
}
