using Content.PrototypeEditor.UI;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.PrototypeEditor.States;

public sealed class PrototypeEditorState : State
{
    [Dependency] private readonly IUserInterfaceManager _ui = default!;

    private PrototypeEditorWindow? _window;

    protected override void Startup()
    {
        _window = new PrototypeEditorWindow();
        _ui.StateRoot.AddChild(_window);

        // Anchor the window to fill the state root — no dragging, no resize,
        // no title-bar positioning games. The app's own window chrome handles exit.
        LayoutContainer.SetAnchorAndMarginPreset(_window, LayoutContainer.LayoutPreset.Wide);
    }

    protected override void Shutdown()
    {
        _window?.Dispose();
        _window = null;
    }
}
