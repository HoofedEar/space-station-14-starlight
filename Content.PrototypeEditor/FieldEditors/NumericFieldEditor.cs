using System.Globalization;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Single-line editor for CLR numeric primitives. One class covers every width
/// via a discriminator so the registry can hand out per-type instances.
/// Mirrors Robust's primitive serializers by parsing/formatting through
/// <see cref="CultureInfo.InvariantCulture"/> — that way a re-typed value can
/// compare byte-equal to a freshly-serialized inherited node.
/// </summary>
public sealed class NumericFieldEditor : IPrototypeFieldEditor
{
    public enum NumberType
    {
        Byte, SByte, Short, UShort, Int, UInt, Long, ULong, Float, Double, Decimal,
    }

    private readonly NumberType _type;

    public NumericFieldEditor(NumberType type) => _type = type;

    public static void RegisterAll(FieldEditorRegistry reg)
    {
        reg.RegisterExact<byte>(new NumericFieldEditor(NumberType.Byte));
        reg.RegisterExact<sbyte>(new NumericFieldEditor(NumberType.SByte));
        reg.RegisterExact<short>(new NumericFieldEditor(NumberType.Short));
        reg.RegisterExact<ushort>(new NumericFieldEditor(NumberType.UShort));
        reg.RegisterExact<int>(new NumericFieldEditor(NumberType.Int));
        reg.RegisterExact<uint>(new NumericFieldEditor(NumberType.UInt));
        reg.RegisterExact<long>(new NumericFieldEditor(NumberType.Long));
        reg.RegisterExact<ulong>(new NumericFieldEditor(NumberType.ULong));
        reg.RegisterExact<float>(new NumericFieldEditor(NumberType.Float));
        reg.RegisterExact<double>(new NumericFieldEditor(NumberType.Double));
        reg.RegisterExact<decimal>(new NumericFieldEditor(NumberType.Decimal));
    }

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var edit = new LineEdit
        {
            HorizontalExpand = true,
            Text = (current as ValueDataNode)?.Value ?? string.Empty,
        };

        edit.OnTextChanged += args =>
        {
            if (string.IsNullOrEmpty(args.Text))
            {
                onChanged(null);
                return;
            }

            // Only propagate when the text parses cleanly. Intermediate states
            // like "1." or "-" are ignored so the YAML tab and inheritance dot
            // don't churn on every keystroke.
            if (TryParseAndFormat(args.Text, _type, out var canonical))
                onChanged(new ValueDataNode(canonical));
        };

        return edit;
    }

    private static bool TryParseAndFormat(string text, NumberType type, out string formatted)
    {
        var c = CultureInfo.InvariantCulture;
        try
        {
            formatted = type switch
            {
                NumberType.Byte => byte.Parse(text, c).ToString(c),
                NumberType.SByte => sbyte.Parse(text, c).ToString(c),
                NumberType.Short => short.Parse(text, c).ToString(c),
                NumberType.UShort => ushort.Parse(text, c).ToString(c),
                NumberType.Int => int.Parse(text, c).ToString(c),
                NumberType.UInt => uint.Parse(text, c).ToString(c),
                NumberType.Long => long.Parse(text, c).ToString(c),
                NumberType.ULong => ulong.Parse(text, c).ToString(c),
                NumberType.Float => float.Parse(text, c).ToString(c),
                NumberType.Double => double.Parse(text, c).ToString(c),
                NumberType.Decimal => decimal.Parse(text, c).ToString(c),
                _ => throw new ArgumentOutOfRangeException(),
            };
            return true;
        }
        catch
        {
            formatted = string.Empty;
            return false;
        }
    }
}
