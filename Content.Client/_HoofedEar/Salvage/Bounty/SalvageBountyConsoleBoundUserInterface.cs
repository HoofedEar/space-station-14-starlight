using Content.Shared._HoofedEar.Salvage.Bounty;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._HoofedEar.Salvage.Bounty;

[UsedImplicitly]
public sealed class SalvageBountyConsoleBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private SalvageBountyMenu? _menu;

    public SalvageBountyConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<SalvageBountyMenu>();

        _menu.OnLabelButtonPressed += id => SendMessage(new SalvageBountyPrintLabelMessage(id));
        _menu.OnSkipButtonPressed += id => SendMessage(new SalvageBountySkipMessage(id));
    }

    protected override void UpdateState(BoundUserInterfaceState message)
    {
        base.UpdateState(message);

        if (message is not SalvageBountyConsoleState state)
            return;

        _menu?.UpdateEntries(state.Bounties, state.History, state.UntilNextSkip);
    }
}
