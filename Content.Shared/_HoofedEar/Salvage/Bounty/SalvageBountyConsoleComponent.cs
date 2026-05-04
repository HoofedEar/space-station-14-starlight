using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._HoofedEar.Salvage.Bounty;

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class SalvageBountyConsoleComponent : Component
{
    /// <summary>
    /// Entity prototype spawned when the print button is pressed.
    /// </summary>
    [DataField]
    public EntProtoId BountyLabelId = "PaperSalvageBountyManifest";

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextPrintTime = TimeSpan.Zero;

    [DataField]
    public TimeSpan PrintDelay = TimeSpan.FromSeconds(5);

    [DataField]
    public SoundSpecifier PrintSound = new SoundPathSpecifier("/Audio/Machines/printer.ogg");

    [DataField]
    public SoundSpecifier SkipSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier DenySound = new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_two.ogg");

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextDenySoundTime = TimeSpan.Zero;

    [DataField]
    public TimeSpan DenySoundDelay = TimeSpan.FromSeconds(2);
}

[NetSerializable, Serializable]
public enum SalvageBountyConsoleUiKey : byte
{
    Bounty,
}

[NetSerializable, Serializable]
public sealed class SalvageBountyConsoleState : BoundUserInterfaceState
{
    public List<SalvageBountyData> Bounties;
    public List<SalvageBountyHistoryData> History;
    public TimeSpan UntilNextSkip;

    public SalvageBountyConsoleState(List<SalvageBountyData> bounties, List<SalvageBountyHistoryData> history, TimeSpan untilNextSkip)
    {
        Bounties = bounties;
        History = history;
        UntilNextSkip = untilNextSkip;
    }
}

[Serializable, NetSerializable]
public sealed class SalvageBountyPrintLabelMessage : BoundUserInterfaceMessage
{
    public string BountyId;

    public SalvageBountyPrintLabelMessage(string bountyId)
    {
        BountyId = bountyId;
    }
}

[Serializable, NetSerializable]
public sealed class SalvageBountySkipMessage : BoundUserInterfaceMessage
{
    public string BountyId;

    public SalvageBountySkipMessage(string bountyId)
    {
        BountyId = bountyId;
    }
}
