using Robust.Shared.Prototypes;
using Content.Shared.Stacks;

namespace Content.Server._HoofedEar.Research;

/// <summary>
/// Attach to a <c>ResearchServer</c> entity to let players insert ticket stacks
/// (e.g. <c>SalvageTicket</c>) and convert them into research points.
/// </summary>
[RegisterComponent]
public sealed partial class TicketResearchConsumerComponent : Component
{
    /// <summary>
    /// Stack type accepted by this consumer. Defaults to SalvageTicket.
    /// </summary>
    [DataField]
    public ProtoId<StackPrototype> AcceptedStackType = "SalvageTicket";

    /// <summary>
    /// Research points granted per ticket consumed.
    /// </summary>
    [DataField]
    public int PointsPerTicket = 10;
}
