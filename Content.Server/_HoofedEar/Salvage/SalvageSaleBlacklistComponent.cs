using Robust.Shared.Prototypes;

namespace Content.Server._HoofedEar.Salvage;

/// <summary>
/// Companion to <see cref="SalvageSaleConsoleComponent"/> that prevents specific entity prototypes
/// from being sold. An item is rejected if its prototype ID — or any descendant's prototype ID —
/// is listed here, on top of the existing <c>CargoSellBlacklist</c> check.
/// </summary>
[RegisterComponent]
public sealed partial class SalvageSaleBlacklistComponent : Component
{
    [DataField(required: true)]
    public HashSet<EntProtoId> Blacklist = new();
}
