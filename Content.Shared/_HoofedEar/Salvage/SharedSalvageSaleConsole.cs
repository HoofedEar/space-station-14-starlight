using Robust.Shared.Serialization;

namespace Content.Shared._HoofedEar.Salvage;

[NetSerializable, Serializable]
public enum SalvageSaleConsoleUiKey : byte
{
    Sale,
}

[NetSerializable, Serializable]
public sealed class SalvageSaleConsoleBuiState : BoundUserInterfaceState
{
    /// <summary>Total spesos value of items on sell pallets.</summary>
    public int Appraisal;

    /// <summary>Tickets that would be paid out if the player hits sell now.</summary>
    public int Tickets;

    /// <summary>Number of sellable items on the pallets.</summary>
    public int Count;

    /// <summary>Spesos required per ticket (exchange rate).</summary>
    public int SpesosPerTicket;

    /// <summary>Whether the buttons should be enabled.</summary>
    public bool Enabled;

    public SalvageSaleConsoleBuiState(int appraisal, int tickets, int count, int spesosPerTicket, bool enabled)
    {
        Appraisal = appraisal;
        Tickets = tickets;
        Count = count;
        SpesosPerTicket = spesosPerTicket;
        Enabled = enabled;
    }
}

[Serializable, NetSerializable]
public sealed class SalvageSaleSellMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class SalvageSaleAppraiseMessage : BoundUserInterfaceMessage;
