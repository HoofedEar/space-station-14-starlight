using Content.PrototypeEditor.States;
using JetBrains.Annotations;
using Robust.Client.State;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.PrototypeEditor;

[UsedImplicitly]
public sealed class EntryPoint : GameClient
{
    [Dependency] private readonly IStateManager _stateMan = default!;
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IMapManager _mapMan = default!;
    [Dependency] private readonly ILogManager _logMan = default!;

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

        // The editor spawns dummy entities for previews. EntityManager.Startup
        // populates the entity-system dependency container (so EntMan.System<T>()
        // works) and is normally called by BaseClient only when connecting to
        // a server — which never happens here. Mirror what GameStartedSetup
        // does so all the sprite/transform systems are resolvable.
        //
        // A handful of client systems (TTS, Guidebook, …) fire network messages
        // from their Initialize, which logs an error per call since we never
        // connect. Those systems otherwise work fine here, so we just mute the
        // `net` sawmill for the duration of Startup.
        var netMill = _logMan.GetSawmill("net");
        var savedLevel = netMill.Level;
        netMill.Level = LogLevel.Fatal;
        try
        {
            _entMan.Startup();
            _mapMan.Startup();
        }
        finally
        {
            netMill.Level = savedLevel;
        }

        // Content.Client's PostInit synchronously switches to MainScreen, so any
        // state request we make during PostInit is overwritten. Waiting for the
        // first frame ensures all mods' PostInit have run before we claim the state.
        _stateMan.RequestStateChange<PrototypeEditorState>();
        _editorStateRequested = true;
    }
}
