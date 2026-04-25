using Content.Shared._Starlight.Trigger.Components.Effects;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Trigger;
using Content.Shared.Trigger.Systems;

namespace Content.Shared._Starlight.Trigger.Systems;

public sealed class HealAreaOnTriggerSystem : XOnTriggerSystem<HealAreaOnTriggerComponent>
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    private readonly HashSet<Entity<DamageableComponent>> _targets = new();

    protected override void OnTrigger(Entity<HealAreaOnTriggerComponent> ent, EntityUid target, ref TriggerEvent args)
    {
        var coords = Transform(ent.Owner).Coordinates;

        _targets.Clear();
        _lookup.GetEntitiesInRange(coords, ent.Comp.Range, _targets);

        foreach (var found in _targets)
        {
            _damageable.TryChangeDamage((found.Owner, found.Comp), ent.Comp.Damage, ent.Comp.IgnoreResistances, origin: ent.Owner);
        }

        args.Handled = true;
    }
}
