using System.Globalization;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// TimeSpan editor working in seconds — the spelling <c>TimespanSerializer</c>
/// produces on save. The parse side still accepts richer forms like "5ms" via
/// the raw-YAML fallback, but the canonical edit path is a plain number of
/// seconds so that edits round-trip byte-equal to inherited nodes.
/// </summary>
public sealed class TimeSpanFieldEditor : IPrototypeFieldEditor
{
    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        var edit = new LineEdit
        {
            HorizontalExpand = true,
            Text = (current as ValueDataNode)?.Value ?? string.Empty,
            PlaceHolder = "seconds",
        };

        edit.OnTextChanged += args =>
        {
            if (string.IsNullOrEmpty(args.Text))
            {
                onChanged(null);
                return;
            }

            if (!double.TryParse(args.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
                return;

            onChanged(new ValueDataNode(seconds.ToString(CultureInfo.InvariantCulture)));
        };

        row.AddChild(edit);
        row.AddChild(new Label { Text = "s" });
        return row;
    }
}
