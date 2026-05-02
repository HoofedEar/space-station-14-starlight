using System.Diagnostics.CodeAnalysis;

namespace Content.Client.Lobby
{
    public sealed partial class LobbyState
    {
        // Hold the late-join button for a moment after the round starts so the dropship-planet
        // generator (dungeons, ore/mob markers, landing-zone clear) has time to settle before
        // players slot in.
        private static readonly TimeSpan JoinDelay = TimeSpan.FromSeconds(10);

        private bool IsJoinDelayActive(out TimeSpan remaining)
        {
            remaining = TimeSpan.Zero;
            if (!_gameTicker.IsGameStarted)
                return false;

            var elapsed = _gameTiming.CurTime - _gameTicker.RoundStartTimeSpan;
            if (elapsed >= JoinDelay)
                return false;

            remaining = JoinDelay - elapsed;
            return true;
        }

        private bool TryGetJoinDelayTooltip([NotNullWhen(true)] out string? tooltip)
        {
            if (IsJoinDelayActive(out var remaining))
            {
                tooltip = Loc.GetString(
                    "ui-lobby-ready-button-tooltip-join-delay",
                    ("seconds", (int)Math.Ceiling(remaining.TotalSeconds)));
                return true;
            }

            tooltip = null;
            return false;
        }

        // Returns true if we wrote StartTime / disabled the ready button ourselves; the caller
        // should leave StartTime alone in that case. When the delay has elapsed we restore the
        // button — but only if late-joining isn't otherwise blocked.
        private bool UpdateJoinDelayFrame()
        {
            if (IsJoinDelayActive(out var remaining))
            {
                Lobby!.ReadyButton.Disabled = true;
                Lobby!.StartTime.Text = Loc.GetString(
                    "lobby-state-join-delay-countdown",
                    ("seconds", (int)Math.Ceiling(remaining.TotalSeconds)));
                return true;
            }

            if (Lobby!.ReadyButton.Disabled && !_gameTicker.DisallowedLateJoin)
                Lobby!.ReadyButton.Disabled = false;

            return false;
        }

        private bool ShouldDisableReadyOnGameStart() => IsJoinDelayActive(out _);
    }
}
