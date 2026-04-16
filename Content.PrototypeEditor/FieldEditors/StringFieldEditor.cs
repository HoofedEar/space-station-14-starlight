using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Single-line string editor backed by <see cref="ValueDataNode"/>. Fires
/// <c>onChanged(null)</c> when the field is cleared so the row can drop its
/// override and fall back to the inherited value.
/// </summary>
public sealed class StringFieldEditor : IPrototypeFieldEditor
{
    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var edit = new LineEdit
        {
            HorizontalExpand = true,
            Text = (current as ValueDataNode)?.Value ?? string.Empty,
        };

        edit.OnTextChanged += args =>
        {
            onChanged(string.IsNullOrEmpty(args.Text) ? null : new ValueDataNode(args.Text));
        };

        return edit;
    }
}
