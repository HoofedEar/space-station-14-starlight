using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace Content.Server._HoofedEar.Salvage;

/// <summary>
/// Console that sells items on nearby <c>CargoPalletSell</c> pads in exchange for
/// a stack of salvage tickets, dropped onto one of the pallets.
/// </summary>
[RegisterComponent]
public sealed partial class SalvageSaleConsoleComponent : Component
{
    /// <summary>
    /// How many spesos of value buy a single ticket. Total is floored when converted.
    /// </summary>
    [DataField]
    public int SpesosPerTicket = 100;

    /// <summary>
    /// Stack prototype to spawn on the pallet as payment.
    /// </summary>
    [DataField]
    public ProtoId<StackPrototype> TicketStack = "SalvageTicket";
}
