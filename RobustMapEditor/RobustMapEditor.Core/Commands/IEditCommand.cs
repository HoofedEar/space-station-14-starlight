#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using RobustMapEditor.Core.Session;
using Robust.Shared.GameObjects;

namespace RobustMapEditor.Core.Commands;

/// <summary>
/// A reversible mutation of a <see cref="DocumentSession"/>'s live entity state.
/// Commands are immutable after <see cref="ApplyAsync"/> has completed the first
/// time — they cache whatever "before" state they need for <see cref="UndoAsync"/>
/// at apply-time, so they can be undone and re-applied freely thereafter.
/// </summary>
/// <remarks>
/// All apply/undo work must happen inside <c>server.WaitPost</c> so it runs on
/// the engine's tick loop with locks held. Implementors are responsible for that.
/// </remarks>
public interface IEditCommand
{
    /// <summary>Short phrase for UI ("Paint 12 tiles"). Displayed in status bar / undo menu.</summary>
    string Description { get; }

    /// <summary>Grids whose visual output may have changed as a result of applying or
    /// undoing this command. Drives Phase 2C's partial re-render — only these grids
    /// get repainted; untouched grids keep their prior <c>RenderedGrid</c> instance.
    /// Empty collection means "no grids changed" (e.g. a metadata-only command) and
    /// triggers no re-render at all. Null is not allowed.</summary>
    IReadOnlyCollection<EntityUid> AffectedGrids { get; }

    Task ApplyAsync(DocumentSession session);
    Task UndoAsync(DocumentSession session);
}
