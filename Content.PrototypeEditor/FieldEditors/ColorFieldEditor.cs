using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Hex-string color editor with a live swatch preview. Accepts the same input
/// forms as Robust's <c>ColorSerializer</c> (named color or hex), but always
/// emits hex via <c>Color.ToHex()</c> — which matches the serializer's Write
/// output exactly so the revert-to-inherited comparison works.
/// </summary>
public sealed class ColorFieldEditor : IPrototypeFieldEditor
{
    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        var initialText = (current as ValueDataNode)?.Value ?? string.Empty;
        var initialColor = TryParse(initialText, out var c) ? c : Color.Transparent;

        var swatchStyle = new StyleBoxFlat { BackgroundColor = initialColor };
        var swatch = new PanelContainer
        {
            MinSize = new System.Numerics.Vector2(24, 0),
            PanelOverride = swatchStyle,
        };

        var edit = new LineEdit
        {
            HorizontalExpand = true,
            Text = initialText,
            PlaceHolder = "#RRGGBBAA or name",
        };

        edit.OnTextChanged += args =>
        {
            if (string.IsNullOrEmpty(args.Text))
            {
                onChanged(null);
                return;
            }

            if (!TryParse(args.Text, out var parsed))
                return;

            swatchStyle.BackgroundColor = parsed;
            onChanged(new ValueDataNode(parsed.ToHex()));
        };

        row.AddChild(swatch);
        row.AddChild(edit);
        return row;
    }

    private static bool TryParse(string text, out Color color)
    {
        if (Color.TryFromName(text, out color))
            return true;

        var hex = Color.TryFromHex(text);
        if (hex == null)
        {
            color = default;
            return false;
        }

        color = hex.Value;
        return true;
    }
}
