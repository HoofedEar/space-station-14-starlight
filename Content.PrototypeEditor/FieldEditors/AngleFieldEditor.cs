using System.Globalization;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Angle editor that exposes degrees to the user but emits radians-with-suffix
/// (<c>"{theta} rad"</c>) — Robust's <c>AngleSerializer</c> writes that form on
/// save, so mirroring it keeps the revert-to-inherited comparison working.
/// </summary>
public sealed class AngleFieldEditor : IPrototypeFieldEditor
{
    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        var initialDegrees = ParseInitialDegrees(current);

        var edit = new LineEdit
        {
            HorizontalExpand = true,
            Text = initialDegrees,
            PlaceHolder = "degrees",
        };

        edit.OnTextChanged += args =>
        {
            if (string.IsNullOrEmpty(args.Text))
            {
                onChanged(null);
                return;
            }

            if (!double.TryParse(args.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var deg))
                return;

            var radians = Angle.FromDegrees(deg).Theta;
            onChanged(new ValueDataNode($"{radians.ToString(CultureInfo.InvariantCulture)} rad"));
        };

        row.AddChild(edit);
        row.AddChild(new Label { Text = "°" });
        return row;
    }

    private static string ParseInitialDegrees(DataNode? current)
    {
        if (current is not ValueDataNode value || string.IsNullOrEmpty(value.Value))
            return string.Empty;

        var text = value.Value;
        double result;
        if (text.EndsWith("rad"))
        {
            var numeric = text.Substring(0, text.Length - 3).Trim();
            if (!double.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var rad))
                return string.Empty;
            result = new Angle(rad).Degrees;
        }
        else
        {
            if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                return string.Empty;
        }

        return result.ToString(CultureInfo.InvariantCulture);
    }
}
