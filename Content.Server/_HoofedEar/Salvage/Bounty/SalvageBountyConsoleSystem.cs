using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.NameIdentifier;
using Content.Server.Station.Systems;
using Content.Shared._HoofedEar.Salvage.Bounty;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.IdentityManagement;
using Content.Shared.Labels.EntitySystems;
using Content.Shared.NameIdentifier;
using Content.Shared.Paper;
using Content.Shared.Stacks;
using Content.Shared.Whitelist;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._HoofedEar.Salvage.Bounty;

/// <summary>
/// Server-side logic for the salvage bounty board: filling the bounty database,
/// printing labels, skipping bounties, and exposing helpers used by the salvage
/// sale console to detect completion.
/// </summary>
public sealed class SalvageBountyConsoleSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly AccessReaderSystem _accessReader = default!;
    [Dependency] private readonly ContainerSystem _container = default!;
    [Dependency] private readonly NameIdentifierSystem _nameIdentifier = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelistSys = default!;
    [Dependency] private readonly PaperSystem _paperSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private static readonly ProtoId<NameIdentifierGroupPrototype> BountyNameIdentifierGroup = "Bounty";

    private EntityQuery<StackComponent> _stackQuery;
    private EntityQuery<ContainerManagerComponent> _containerQuery;
    private EntityQuery<SalvageBountyLabelComponent> _bountyLabelQuery;

    public override void Initialize()
    {
        base.Initialize();

        _stackQuery = GetEntityQuery<StackComponent>();
        _containerQuery = GetEntityQuery<ContainerManagerComponent>();
        _bountyLabelQuery = GetEntityQuery<SalvageBountyLabelComponent>();

        SubscribeLocalEvent<SalvageBountyConsoleComponent, BoundUIOpenedEvent>(OnConsoleOpened);
        SubscribeLocalEvent<SalvageBountyConsoleComponent, SalvageBountyPrintLabelMessage>(OnPrintLabelMessage);
        SubscribeLocalEvent<SalvageBountyConsoleComponent, SalvageBountySkipMessage>(OnSkipMessage);
        SubscribeLocalEvent<StationSalvageBountyDatabaseComponent, MapInitEvent>(OnMapInit);
    }

    private void OnConsoleOpened(EntityUid uid, SalvageBountyConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (_station.GetOwningStation(uid) is not { } station ||
            !TryComp<StationSalvageBountyDatabaseComponent>(station, out var db))
            return;

        var untilNextSkip = db.NextSkipTime - _timing.CurTime;
        _ui.SetUiState(uid, SalvageBountyConsoleUiKey.Bounty, new SalvageBountyConsoleState(db.Bounties, db.History, untilNextSkip));
    }

    private void OnPrintLabelMessage(EntityUid uid, SalvageBountyConsoleComponent component, SalvageBountyPrintLabelMessage args)
    {
        if (_timing.CurTime < component.NextPrintTime)
            return;

        if (_station.GetOwningStation(uid) is not { } station)
            return;

        if (!TryGetBountyFromId(station, args.BountyId, out var bounty))
            return;

        var label = Spawn(component.BountyLabelId, Transform(uid).Coordinates);
        component.NextPrintTime = _timing.CurTime + component.PrintDelay;
        SetupBountyLabel(label, station, bounty.Value);
        _audio.PlayPvs(component.PrintSound, uid);
    }

    private void OnSkipMessage(EntityUid uid, SalvageBountyConsoleComponent component, SalvageBountySkipMessage args)
    {
        if (_station.GetOwningStation(uid) is not { } station ||
            !TryComp<StationSalvageBountyDatabaseComponent>(station, out var db))
            return;

        if (_timing.CurTime < db.NextSkipTime)
            return;

        if (!TryGetBountyFromId(station, args.BountyId, out var bounty))
            return;

        if (args.Actor is not { Valid: true } mob)
            return;

        if (TryComp<AccessReaderComponent>(uid, out var accessReader) && !_accessReader.IsAllowed(mob, uid, accessReader))
        {
            if (_timing.CurTime >= component.NextDenySoundTime)
            {
                component.NextDenySoundTime = _timing.CurTime + component.DenySoundDelay;
                _audio.PlayPvs(component.DenySound, uid);
            }
            return;
        }

        if (!TryRemoveBounty(station, bounty.Value, true, args.Actor))
            return;

        FillBountyDatabase(station);
        db.NextSkipTime = _timing.CurTime + db.SkipDelay;
        var untilNextSkip = db.NextSkipTime - _timing.CurTime;
        _ui.SetUiState(uid, SalvageBountyConsoleUiKey.Bounty, new SalvageBountyConsoleState(db.Bounties, db.History, untilNextSkip));
        _audio.PlayPvs(component.SkipSound, uid);
    }

    public void SetupBountyLabel(EntityUid uid, EntityUid stationId, SalvageBountyData bounty, PaperComponent? paper = null, SalvageBountyLabelComponent? label = null)
    {
        if (!Resolve(uid, ref paper, ref label) || !_protoMan.Resolve<SalvageBountyPrototype>(bounty.Bounty, out var prototype))
            return;

        label.Id = bounty.Id;
        label.AssociatedStationId = stationId;

        var msg = new FormattedMessage();
        msg.AddMarkupOrThrow(Loc.GetString("salvage-bounty-manifest-header", ("id", bounty.Id)));
        msg.PushNewline();
        msg.AddMarkupOrThrow(Loc.GetString("salvage-bounty-manifest-list-start"));
        msg.PushNewline();
        foreach (var entry in prototype.Entries)
        {
            msg.AddMarkupOrThrow($"- {Loc.GetString("salvage-bounty-console-manifest-entry",
                ("amount", entry.Amount),
                ("item", Loc.GetString(entry.Name)))}");
            msg.PushNewline();
        }
        msg.AddMarkupOrThrow(Loc.GetString("salvage-bounty-console-manifest-reward", ("reward", prototype.Reward)));
        _paperSystem.SetContent((uid, paper), msg.ToMarkup());
    }

    private void OnMapInit(EntityUid uid, StationSalvageBountyDatabaseComponent component, MapInitEvent args)
    {
        FillBountyDatabase(uid, component);
    }

    /// <summary>
    /// Tops up the database with random bounties from the configured group until
    /// it reaches <see cref="StationSalvageBountyDatabaseComponent.MaxBounties"/>.
    /// </summary>
    public void FillBountyDatabase(EntityUid uid, StationSalvageBountyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        while (component.Bounties.Count < component.MaxBounties)
        {
            if (!TryAddBounty(uid, component))
                break;
        }

        UpdateBountyConsoles();
    }

    public bool TryAddBounty(EntityUid uid, StationSalvageBountyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        var allBounties = _protoMan.EnumeratePrototypes<SalvageBountyPrototype>()
            .Where(p => p.Group == component.Group)
            .ToList();

        if (allBounties.Count == 0)
            return false;

        var filteredBounties = new List<SalvageBountyPrototype>();
        foreach (var proto in allBounties)
        {
            if (component.Bounties.Any(b => b.Bounty == proto.ID))
                continue;
            filteredBounties.Add(proto);
        }

        var pool = filteredBounties.Count == 0 ? allBounties : filteredBounties;
        var bounty = _random.Pick(pool);
        return TryAddBounty(uid, bounty, component);
    }

    public bool TryAddBounty(EntityUid uid, SalvageBountyPrototype bounty, StationSalvageBountyDatabaseComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        if (component.Bounties.Count >= component.MaxBounties)
            return false;

        _nameIdentifier.GenerateUniqueName(uid, BountyNameIdentifierGroup, out var randomVal);
        var newBounty = new SalvageBountyData(bounty, randomVal);
        if (component.Bounties.Any(b => b.Id == newBounty.Id))
        {
            Log.Error("Failed to add salvage bounty {ID} because another with the same ID already existed", newBounty.Id);
            return false;
        }

        component.Bounties.Add(newBounty);
        _adminLogger.Add(LogType.Action, LogImpact.Low, $"Added salvage bounty \"{bounty.ID}\" (id:{component.TotalBounties}) to station {ToPrettyString(uid)}");
        component.TotalBounties++;
        return true;
    }

    public bool TryRemoveBounty(Entity<StationSalvageBountyDatabaseComponent?> ent,
        SalvageBountyData data,
        bool skipped,
        EntityUid? actor = null)
    {
        if (!Resolve(ent, ref ent.Comp))
            return false;

        for (var i = 0; i < ent.Comp.Bounties.Count; i++)
        {
            if (ent.Comp.Bounties[i].Id != data.Id)
                continue;

            string? actorName = null;
            if (actor != null)
            {
                var ev = new TryGetIdentityShortInfoEvent(ent.Owner, actor.Value);
                RaiseLocalEvent(ev);
                actorName = ev.Title;
            }

            ent.Comp.History.Add(new SalvageBountyHistoryData(data,
                skipped
                    ? SalvageBountyHistoryData.BountyResult.Skipped
                    : SalvageBountyHistoryData.BountyResult.Completed,
                _timing.CurTime,
                actorName));
            ent.Comp.Bounties.RemoveAt(i);
            return true;
        }

        return false;
    }

    public bool TryGetBountyFromId(
        EntityUid uid,
        string id,
        [NotNullWhen(true)] out SalvageBountyData? bounty,
        StationSalvageBountyDatabaseComponent? component = null)
    {
        bounty = null;
        if (!Resolve(uid, ref component))
            return false;

        foreach (var data in component.Bounties)
        {
            if (data.Id != id)
                continue;
            bounty = data;
            break;
        }

        return bounty != null;
    }

    public void UpdateBountyConsoles()
    {
        var query = EntityQueryEnumerator<SalvageBountyConsoleComponent, UserInterfaceComponent>();
        while (query.MoveNext(out var uid, out _, out var ui))
        {
            if (_station.GetOwningStation(uid) is not { } station ||
                !TryComp<StationSalvageBountyDatabaseComponent>(station, out var db))
                continue;

            var untilNextSkip = db.NextSkipTime - _timing.CurTime;
            _ui.SetUiState((uid, ui), SalvageBountyConsoleUiKey.Bounty, new SalvageBountyConsoleState(db.Bounties, db.History, untilNextSkip));
        }
    }

    /// <summary>
    /// Walks the contents of <paramref name="container"/> recursively and returns
    /// the set of entities that should be considered when checking bounty completion.
    /// Skips entities that themselves carry a bounty label so labels nested inside
    /// other crates don't cross-pollinate.
    /// </summary>
    public HashSet<EntityUid> GetBountyEntities(EntityUid uid)
    {
        var entities = new HashSet<EntityUid> { uid };

        if (!TryComp<ContainerManagerComponent>(uid, out var containers))
            return entities;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var ent in container.ContainedEntities)
            {
                if (_bountyLabelQuery.HasComponent(ent))
                    continue;

                foreach (var child in GetBountyEntities(ent))
                    entities.Add(child);
            }
        }

        return entities;
    }

    public bool IsValidBountyEntry(EntityUid entity, SalvageBountyItemEntry entry)
    {
        if (!_whitelistSys.IsValid(entry.Whitelist, entity))
            return false;

        if (entry.Blacklist != null && _whitelistSys.IsValid(entry.Blacklist, entity))
            return false;

        return true;
    }

    public bool IsBountyComplete(EntityUid container, SalvageBountyData data)
    {
        if (!_protoMan.Resolve(data.Bounty, out var proto))
            return false;

        return IsBountyComplete(GetBountyEntities(container), proto.Entries);
    }

    public bool IsBountyComplete(HashSet<EntityUid> entities, IEnumerable<SalvageBountyItemEntry> entries)
    {
        foreach (var entry in entries)
        {
            var count = 0;
            var temp = new HashSet<EntityUid>();
            foreach (var entity in entities)
            {
                if (!IsValidBountyEntry(entity, entry))
                    continue;

                count += _stackQuery.CompOrNull(entity)?.Count ?? 1;
                temp.Add(entity);

                if (count >= entry.Amount)
                    break;
            }

            if (count < entry.Amount)
                return false;

            foreach (var ent in temp)
                entities.Remove(ent);
        }

        return true;
    }

    /// <summary>
    /// Looks up the bounty label attached to a crate via the standard label container.
    /// </summary>
    public bool TryGetBountyLabel(EntityUid uid,
        [NotNullWhen(true)] out EntityUid? labelEnt,
        [NotNullWhen(true)] out SalvageBountyLabelComponent? labelComp)
    {
        labelEnt = null;
        labelComp = null;

        if (!_containerQuery.TryGetComponent(uid, out var containerMan))
            return false;

        if (!_container.TryGetContainer(uid, LabelSystem.ContainerName, out var container, containerMan))
            return false;

        if (container.ContainedEntities.FirstOrNull() is not { } label ||
            !_bountyLabelQuery.TryGetComponent(label, out var component))
            return false;

        labelEnt = label;
        labelComp = component;
        return true;
    }
}
