using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Stack;
using Content.Shared._HoofedEar.Salvage;
using Content.Shared.Mobs.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._HoofedEar.Salvage;

public sealed class SalvageSaleConsoleSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private static readonly SoundPathSpecifier ApproveSound = new("/Audio/Effects/Cargo/ping.ogg");

    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<CargoSellBlacklistComponent> _blacklistQuery;
    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<MetaDataComponent> _metaQuery;
    private EntityQuery<SalvageSaleBlacklistComponent> _saleBlacklistQuery;

    public override void Initialize()
    {
        base.Initialize();

        _xformQuery = GetEntityQuery<TransformComponent>();
        _blacklistQuery = GetEntityQuery<CargoSellBlacklistComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
        _metaQuery = GetEntityQuery<MetaDataComponent>();
        _saleBlacklistQuery = GetEntityQuery<SalvageSaleBlacklistComponent>();

        SubscribeLocalEvent<SalvageSaleConsoleComponent, BoundUIOpenedEvent>(OnUiOpen);
        SubscribeLocalEvent<SalvageSaleConsoleComponent, SalvageSaleAppraiseMessage>(OnAppraise);
        SubscribeLocalEvent<SalvageSaleConsoleComponent, SalvageSaleSellMessage>(OnSell);
    }

    private void OnUiOpen(EntityUid uid, SalvageSaleConsoleComponent component, BoundUIOpenedEvent args)
    {
        UpdateUi(uid, component);
    }

    private void OnAppraise(EntityUid uid, SalvageSaleConsoleComponent component, SalvageSaleAppraiseMessage args)
    {
        UpdateUi(uid, component);
    }

    private void OnSell(EntityUid uid, SalvageSaleConsoleComponent component, SalvageSaleSellMessage args)
    {
        if (Transform(uid).GridUid is not { } gridUid)
            return;

        var pallets = GetSellPallets(gridUid);
        if (pallets.Count == 0)
            return;

        GatherGoods(uid, pallets, out var toSell, out var totalSpesos);

        var rate = Math.Max(1, component.SpesosPerTicket);
        var tickets = (int) (totalSpesos / rate);
        if (tickets <= 0)
            return;

        foreach (var ent in toSell)
            Del(ent);

        var dropPallet = pallets[0];
        _stack.SpawnAtPosition(tickets, component.TicketStack, Transform(dropPallet).Coordinates);

        _audio.PlayPvs(ApproveSound, uid);
        UpdateUi(uid, component);
    }

    private void UpdateUi(EntityUid uid, SalvageSaleConsoleComponent component)
    {
        var rate = Math.Max(1, component.SpesosPerTicket);

        if (Transform(uid).GridUid is not { } gridUid)
        {
            _ui.SetUiState(uid,
                SalvageSaleConsoleUiKey.Sale,
                new SalvageSaleConsoleBuiState(0, 0, 0, rate, false));
            return;
        }

        GatherGoods(uid, GetSellPallets(gridUid), out var toSell, out var totalSpesos);
        var tickets = (int) (totalSpesos / rate);

        _ui.SetUiState(uid,
            SalvageSaleConsoleUiKey.Sale,
            new SalvageSaleConsoleBuiState((int) totalSpesos, tickets, toSell.Count, rate, true));
    }

    private List<EntityUid> GetSellPallets(EntityUid gridUid)
    {
        var result = new List<EntityUid>();
        var query = AllEntityQuery<CargoPalletComponent, TransformComponent>();
        while (query.MoveNext(out var palletUid, out var pallet, out var xform))
        {
            if (xform.ParentUid != gridUid || !xform.Anchored)
                continue;
            if ((pallet.PalletType & BuySellType.Sell) == 0)
                continue;
            result.Add(palletUid);
        }
        return result;
    }

    private void GatherGoods(EntityUid console, List<EntityUid> pallets, out HashSet<EntityUid> toSell, out double totalSpesos)
    {
        toSell = new HashSet<EntityUid>();
        totalSpesos = 0;

        HashSet<EntProtoId>? protoBlacklist = null;
        if (_saleBlacklistQuery.TryGetComponent(console, out var saleBlacklist) && saleBlacklist.Blacklist.Count > 0)
            protoBlacklist = saleBlacklist.Blacklist;

        var setEnts = new HashSet<EntityUid>();
        foreach (var palletUid in pallets)
        {
            setEnts.Clear();
            _lookup.GetEntitiesIntersecting(
                palletUid,
                setEnts,
                LookupFlags.Dynamic | LookupFlags.Sundries);

            foreach (var ent in setEnts)
            {
                if (toSell.Contains(ent))
                    continue;

                if (_xformQuery.TryGetComponent(ent, out var xform) &&
                    (xform.Anchored || !CanSell(ent, xform)))
                    continue;

                if (_blacklistQuery.HasComponent(ent))
                    continue;

                if (protoBlacklist != null && IsProtoBlacklisted(ent, protoBlacklist))
                    continue;

                var price = _pricing.GetPrice(ent);
                if (price == 0)
                    continue;

                toSell.Add(ent);
                totalSpesos += price;
            }
        }
    }

    private bool IsProtoBlacklisted(EntityUid uid, HashSet<EntProtoId> blacklist)
    {
        if (_metaQuery.TryGetComponent(uid, out var meta) &&
            meta.EntityPrototype is { } proto &&
            blacklist.Contains(proto.ID))
            return true;

        if (!_xformQuery.TryGetComponent(uid, out var xform))
            return false;

        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (IsProtoBlacklisted(child, blacklist))
                return true;
        }

        return false;
    }

    private bool CanSell(EntityUid uid, TransformComponent xform)
    {
        if (_mobQuery.HasComponent(uid))
            return false;

        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (!CanSell(child, _xformQuery.GetComponent(child)))
                return false;
        }

        return true;
    }
}
