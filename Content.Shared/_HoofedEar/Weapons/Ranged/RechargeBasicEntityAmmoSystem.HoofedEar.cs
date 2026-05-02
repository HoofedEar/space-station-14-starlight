using Content.Shared.Weapons.Ranged.Components;

namespace Content.Shared.Weapons.Ranged.Systems;

public sealed partial class RechargeBasicEntityAmmoSystem
{
    // Scales the recharge cooldown by the gun's fire-rate modifier ratio so the recharge
    // sound stays in sync with the gun's ready-to-fire cadence when fire-rate-affecting
    // upgrades (e.g. PKA fire rate modkit) are applied.
    private float GetCooldown(EntityUid uid, RechargeBasicEntityAmmoComponent recharge)
    {
        if (TryComp<GunComponent>(uid, out var gun) && gun.FireRate > 0f && gun.FireRateModified > 0f)
            return recharge.RechargeCooldown * (gun.FireRate / gun.FireRateModified);
        return recharge.RechargeCooldown;
    }
}
