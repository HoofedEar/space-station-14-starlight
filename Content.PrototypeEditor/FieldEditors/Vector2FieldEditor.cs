using System.Globalization;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Two-field editor for <c>Vector2</c> and <c>Vector2i</c>. The serializers
/// write both as <c>"{x},{y}"</c> via <see cref="CultureInfo.InvariantCulture"/>,
/// so we mirror that spelling here — a fresh edit can compare byte-equal to an
/// inherited node for the revert-to-default detection.
/// </summary>
public sealed class Vector2FieldEditor : IPrototypeFieldEditor
{
    private readonly bool _intVec;

    public Vector2FieldEditor(bool intVec) => _intVec = intVec;

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        TryParseCurrent(current, out var initialX, out var initialY);

        var x = new LineEdit
        {
            HorizontalExpand = true,
            PlaceHolder = "X",
            Text = initialX,
        };
        var y = new LineEdit
        {
            HorizontalExpand = true,
            PlaceHolder = "Y",
            Text = initialY,
        };

        void Emit()
        {
            if (string.IsNullOrEmpty(x.Text) && string.IsNullOrEmpty(y.Text))
            {
                onChanged(null);
                return;
            }

            if (!TryFormat(x.Text, out var xs) || !TryFormat(y.Text, out var ys))
                return;

            onChanged(new ValueDataNode($"{xs},{ys}"));
        }

        x.OnTextChanged += _ => Emit();
        y.OnTextChanged += _ => Emit();

        row.AddChild(x);
        row.AddChild(y);
        return row;
    }

    private static void TryParseCurrent(DataNode? current, out string xText, out string yText)
    {
        xText = yText = string.Empty;
        if (current is not ValueDataNode value) return;

        var parts = value.Value.Split(',');
        if (parts.Length != 2) return;

        xText = parts[0].Trim();
        yText = parts[1].Trim();
    }

    private bool TryFormat(string text, out string formatted)
    {
        var c = CultureInfo.InvariantCulture;
        try
        {
            formatted = _intVec
                ? int.Parse(text, c).ToString(c)
                : float.Parse(text, c).ToString(c);
            return true;
        }
        catch
        {
            formatted = string.Empty;
            return false;
        }
    }
}
