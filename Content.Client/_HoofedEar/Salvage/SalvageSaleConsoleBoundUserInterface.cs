using Content.Shared._HoofedEar.Salvage;
using Robust.Client.UserInterface;

namespace Content.Client._HoofedEar.Salvage;

public sealed class SalvageSaleConsoleBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private SalvageSaleMenu? _menu;

    public SalvageSaleConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<SalvageSaleMenu>();
        _menu.AppraiseRequested += () => SendMessage(new SalvageSaleAppraiseMessage());
        _menu.SellRequested += () => SendMessage(new SalvageSaleSellMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not SalvageSaleConsoleBuiState saleState)
            return;

        _menu?.UpdateState(saleState);
    }
}
