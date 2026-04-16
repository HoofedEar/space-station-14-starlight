#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using RobustMapEditor.Core.Session;

namespace RobustMapEditor.Core.Commands;

/// <summary>
/// Linear undo/redo stack for one <see cref="Documents.MapDocument"/>. Tracks the
/// cursor position in a list of applied commands and remembers which position
/// corresponds to the last on-disk save so the document can report clean state
/// when the user undoes all their edits back to the save point.
/// </summary>
/// <remarks>
/// Executing a new command after undoing discards the redo tail. If the discarded
/// tail contained the saved cursor, the saved state becomes unreachable and
/// <see cref="IsDirty"/> stays true forever (until the user saves again) — any path
/// back to clean has been truncated.
/// </remarks>
public sealed class MapHistory
{
    private readonly List<IEditCommand> _commands = new();

    /// <summary>Number of commands currently applied. Points to the next redo slot.</summary>
    private int _cursor;

    /// <summary>Cursor position that matches what's on disk. -1 means unreachable
    /// (the saved state was truncated by a post-undo execute).</summary>
    private int _savedCursor;

    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _commands.Count;

    public bool IsDirty => _cursor != _savedCursor;

    /// <summary>Description of the command that undo would reverse, or null if none.</summary>
    public string? UndoDescription => CanUndo ? _commands[_cursor - 1].Description : null;

    /// <summary>Description of the command that redo would re-apply, or null if none.</summary>
    public string? RedoDescription => CanRedo ? _commands[_cursor].Description : null;

    /// <summary>Apply a command and append it to history. Truncates any redo tail.</summary>
    public async Task ExecuteAsync(IEditCommand cmd, DocumentSession session)
    {
        await cmd.ApplyAsync(session);

        // Drop the redo tail. If saved state lived in that tail, it's now unreachable.
        if (_cursor < _commands.Count)
        {
            if (_savedCursor > _cursor)
                _savedCursor = -1;
            _commands.RemoveRange(_cursor, _commands.Count - _cursor);
        }

        _commands.Add(cmd);
        _cursor++;
    }

    /// <summary>Reverse the most recent command. No-op when <see cref="CanUndo"/> is false.</summary>
    public async Task<IEditCommand?> UndoAsync(DocumentSession session)
    {
        if (!CanUndo) return null;
        var cmd = _commands[_cursor - 1];
        await cmd.UndoAsync(session);
        _cursor--;
        return cmd;
    }

    /// <summary>Re-apply the next command in the redo tail. No-op when <see cref="CanRedo"/> is false.</summary>
    public async Task<IEditCommand?> RedoAsync(DocumentSession session)
    {
        if (!CanRedo) return null;
        var cmd = _commands[_cursor];
        await cmd.ApplyAsync(session);
        _cursor++;
        return cmd;
    }

    /// <summary>Snap the saved-cursor to the current position. Call after a successful save.</summary>
    public void MarkSaved() => _savedCursor = _cursor;
}
