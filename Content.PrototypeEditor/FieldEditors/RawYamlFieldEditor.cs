using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Utility;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Fallback editor: renders the field's <see cref="DataNode"/> as raw YAML in a
/// multi-line text box. Every field is therefore always editable, even when no
/// typed editor has been registered. Parse-on-commit is deferred — this first
/// pass is read-only; the registry will accept overrides for types we care about.
/// </summary>
public sealed class RawYamlFieldEditor : IPrototypeFieldEditor
{
    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var edit = new TextEdit
        {
            HorizontalExpand = true,
            MinHeight = 60,
            Placeholder = new Rope.Leaf($"<{fieldType.Name}> YAML"),
        };

        if (current != null)
            edit.TextRope = new Rope.Leaf(current.ToString());

        return edit;
    }
}
