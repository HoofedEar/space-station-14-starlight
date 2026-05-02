using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Shared.Bed.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle.Components;
using Content.Shared.Chat;
using Content.Shared.Ghost;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._HoofedEar.Light;

/// <summary>
/// When every alive, non-ghost player on a map with <see cref="SleepCycleSkipComponent"/>
/// is asleep and buckled to a bed, fast-forwards <see cref="LightCycleComponent"/> to
/// morning and holds it there until somebody wakes up.
/// </summary>
public sealed class SleepCycleSkipSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly MetaDataSystem _metaSystem = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedLightCycleSystem _lightCycle = default!;

    private EntityQuery<SleepingComponent> _sleepingQuery;
    private EntityQuery<BuckleComponent> _buckleQuery;
    private EntityQuery<HealOnBuckleComponent> _bedQuery;
    private EntityQuery<GhostComponent> _ghostQuery;

    private const float CheckIntervalSeconds = 1f;

    public override void Initialize()
    {
        base.Initialize();
        _sleepingQuery = GetEntityQuery<SleepingComponent>();
        _buckleQuery = GetEntityQuery<BuckleComponent>();
        _bedQuery = GetEntityQuery<HealOnBuckleComponent>();
        _ghostQuery = GetEntityQuery<GhostComponent>();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SleepCycleSkipComponent, LightCycleComponent, MapComponent>();
        while (query.MoveNext(out var uid, out var skip, out var cycle, out var map))
        {
            skip.Accumulator += frameTime;
            if (skip.Accumulator < CheckIntervalSeconds)
            {
                if (skip.Active)
                    HoldOffset(uid, skip, cycle);
                continue;
            }
            skip.Accumulator = 0f;

            if (AreAllPlayersAsleepInBeds(uid))
            {
                if (!skip.Active)
                {
                    skip.Active = true;
                    BroadcastMorning(map.MapId, skip.MessageLocId);
                }
                HoldOffset(uid, skip, cycle);
            }
            else
            {
                skip.Active = false;
            }
        }
    }

    private bool AreAllPlayersAsleepInBeds(EntityUid mapUid)
    {
        var query = AllEntityQuery<ActorComponent, MobStateComponent, TransformComponent>();
        var found = false;
        while (query.MoveNext(out var uid, out _, out var mobState, out var xform))
        {
            if (xform.MapUid != mapUid)
                continue;
            if (_ghostQuery.HasComp(uid))
                continue;
            if (_mobState.IsDead(uid, mobState))
                continue;

            found = true;

            if (!_sleepingQuery.HasComp(uid))
                return false;
            if (!_buckleQuery.TryComp(uid, out var buckle) || buckle.BuckledTo is not { } strap)
                return false;
            if (!_bedQuery.HasComp(strap))
                return false;
        }

        return found;
    }

    private void HoldOffset(EntityUid uid, SleepCycleSkipComponent skip, LightCycleComponent cycle)
    {
        // Client computes time = CurTime + Offset - RoundStart - PausedTime; solve for the
        // Offset that lands time on WakeFraction * Duration.
        var pausedTime = _metaSystem.GetPauseTime(uid);
        var desiredTime = TimeSpan.FromSeconds(cycle.Duration.TotalSeconds * skip.WakeFraction);
        var newOffset = desiredTime - _timing.CurTime + _ticker.RoundStartTimeSpan + pausedTime;
        if (newOffset != cycle.Offset)
            _lightCycle.SetOffset((uid, cycle), newOffset);
    }

    private void BroadcastMorning(MapId mapId, string messageLocId)
    {
        var msg = Loc.GetString(messageLocId);
        _chat.ChatMessageToManyFiltered(
            Filter.BroadcastMap(mapId),
            ChatChannel.Server,
            msg,
            msg,
            EntityUid.Invalid,
            false,
            true,
            null);
    }
}
