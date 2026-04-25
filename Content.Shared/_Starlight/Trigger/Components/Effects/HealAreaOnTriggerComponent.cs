using Content.Shared.Damage;
using Content.Shared.Trigger.Components.Effects;
using Robust.Shared.GameStates;

namespace Content.Shared._Starlight.Trigger.Components.Effects;

/// <summary>
/// Applies a damage specifier (typically with negative values to heal) to every damageable entity
/// within <see cref="Range"/> of the component owner when triggered.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HealAreaOnTriggerComponent : BaseXOnTriggerComponent
{
    /// <summary>
    /// Should the applied damage ignore resistances?
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool IgnoreResistances = true;

    /// <summary>
    /// Radius (in metres) around the component owner that will be affected.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Range = 2f;

    /// <summary>
    /// The damage dealt to every entity in range. Use negative values to heal.
    /// </summary>
    [DataField(required: true), AutoNetworkedField]
    public DamageSpecifier Damage = default!;
}
