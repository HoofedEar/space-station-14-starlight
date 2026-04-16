using Content.PrototypeEditor.States;
using JetBrains.Annotations;
using Robust.Client.State;
using Robust.Shared.ContentPack;
using Robust.Shared.Timing;

namespace Content.PrototypeEditor;

[UsedImplicitly]
public sealed class EntryPoint : GameClient
{
    [Dependency] private readonly IStateManager _stateMan = default!;

    private bool _editorStateRequested;

    public override void Init()
    {
        base.Init();
        Dependencies.BuildGraph();
        Dependencies.InjectDependencies(this);
    }

    public override void Update(ModUpdateLevel level, FrameEventArgs args)
    {
        if (_editorStateRequested || level != ModUpdateLevel.FramePreEngine)
            return;

        // Content.Client's PostInit synchronously switches to MainScreen, so any
        // state request we make during PostInit is overwritten. Waiting for the
        // first frame ensures all mods' PostInit have run before we claim the state.
        _stateMan.RequestStateChange<PrototypeEditorState>();
        _editorStateRequested = true;
    }
}
