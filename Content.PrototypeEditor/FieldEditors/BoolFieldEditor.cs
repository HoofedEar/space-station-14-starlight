using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Checkbox editor backed by <see cref="ValueDataNode"/>. Emits the same
/// <c>"True"</c>/<c>"False"</c> spelling Robust's <c>BooleanSerializer</c>
/// produces so that edited nodes compare byte-equal to freshly-serialized
/// ones (required for the revert-to-inherited detection in the editor row).
/// </summary>
public sealed class BoolFieldEditor : IPrototypeFieldEditor
{
    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var check = new CheckBox
        {
            Pressed = Parse(current),
        };

        check.OnToggled += args =>
        {
            onChanged(new ValueDataNode(args.Pressed ? bool.TrueString : bool.FalseString));
        };

        return check;
    }

    private static bool Parse(DataNode? node)
    {
        if (node is not ValueDataNode value)
            return false;
        return bool.TryParse(value.Value, out var parsed) && parsed;
    }
}
