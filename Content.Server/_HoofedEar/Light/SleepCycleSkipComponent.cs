using Content.Shared.Light.Components;

namespace Content.Server._HoofedEar.Light;

/// <summary>
/// Attach to a map entity that also has <see cref="LightCycleComponent"/>. While every alive,
/// non-ghost player on the map is asleep and buckled to a bed, the cycle is held at a
/// configurable "morning" position so the planet doesn't drift through the rest of the night.
/// </summary>
[RegisterComponent]
public sealed partial class SleepCycleSkipComponent : Component
{
    /// <summary>
    /// Target position within the cycle, as a fraction of <see cref="LightCycleComponent.Duration"/>.
    /// The light curve is sin(πt/Duration)^6 — peaks at 0.5 (noon), zero at 0/1 (night).
    /// 0.25 lands on a quarter-bright dawn.
    /// </summary>
    [DataField]
    public float WakeFraction = 0.25f;

    /// <summary>
    /// Localization key for the chat message broadcast to the map when the skip kicks in.
    /// </summary>
    [DataField]
    public string MessageLocId = "sleep-cycle-skip-morning";

    /// <summary>
    /// True while the cycle is currently being held at morning. Server-only state.
    /// </summary>
    [ViewVariables]
    public bool Active;

    /// <summary>
    /// Time accumulator for throttling the per-frame check.
    /// </summary>
    [ViewVariables]
    public float Accumulator;
}
