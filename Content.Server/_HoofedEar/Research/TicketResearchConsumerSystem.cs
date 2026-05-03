using Content.Server.Popups;
using Content.Server.Research.Systems;
using Content.Shared.Interaction;
using Content.Shared.Research.Components;
using Content.Shared.Stacks;

namespace Content.Server._HoofedEar.Research;

public sealed class TicketResearchConsumerSystem : EntitySystem
{
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly ResearchSystem _research = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TicketResearchConsumerComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<TicketResearchConsumerComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, TicketResearchConsumerComponent component, MapInitEvent args)
    {
        // SharedResearchSystem.OnMapInit skips card generation when the entity has a ResearchClient,
        // which is the case for this combined server/console entity. Re-run it here so the console
        // has tech cards to display.
        if (TryComp<TechnologyDatabaseComponent>(uid, out var db))
            _research.UpdateTechnologyCards(uid, db);
    }

    private void OnInteractUsing(EntityUid uid, TicketResearchConsumerComponent component, InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (!TryComp<StackComponent>(args.Used, out var stack))
            return;

        if (stack.StackTypeId != component.AcceptedStackType)
            return;

        if (!TryComp<ResearchServerComponent>(uid, out var server))
            return;

        var count = stack.Count;
        if (count <= 0)
            return;

        var points = count * component.PointsPerTicket;
        _research.ModifyServerPoints(uid, points, server);
        _stack.SetCount((args.Used, stack), 0);

        _popup.PopupEntity(
            Loc.GetString("research-terminal-tickets-inserted", ("count", count), ("points", points)),
            uid,
            args.User);
        args.Handled = true;
    }
}
