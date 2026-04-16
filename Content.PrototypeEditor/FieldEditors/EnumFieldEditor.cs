using System.Linq;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Enum editor with two modes.
/// <list type="bullet">
///   <item>Regular enums and <c>Nullable&lt;TEnum&gt;</c>: single-select dropdown.
///         Selections emit <c>_serMan.WriteValue(...)</c>, matching Robust's
///         <c>"enum.TypeName.MemberName"</c> spelling so revert-to-inherited
///         comparison works.</item>
///   <item>[Flags] enums: vertical list of checkboxes. SS14 YAML writes flags as a
///         sequence (<c>slots: [neck, head]</c>), so we read from
///         <see cref="SequenceDataNode"/> and emit the same form. Individual-flag
///         case preservation is not attempted — edited flags always write as the
///         enum member name (uppercase), which won't byte-compare equal to a
///         lowercase inherited YAML value. Accepted limitation for v1.</item>
/// </list>
/// </summary>
public sealed class EnumFieldEditor : IPrototypeFieldEditor
{
    private readonly ISerializationManager _serMan;

    // Match component-row banding for the flags checkbox block, so nested
    // enum editors read as a distinct group inside a larger row.
    private static readonly Color FlagsBackground = Color.FromHex("#242424");
    private static readonly Color FlagsBorder = Color.FromHex("#1a1a1a");

    public EnumFieldEditor(ISerializationManager serMan) => _serMan = serMan;

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var enumType = Nullable.GetUnderlyingType(fieldType) ?? fieldType;
        if (!enumType.IsEnum)
        {
            return new Label
            {
                Text = $"<not an enum: {fieldType.Name}>",
                Modulate = Color.OrangeRed,
            };
        }

        return enumType.IsDefined(typeof(FlagsAttribute), inherit: false)
            ? BuildFlags(enumType, current, onChanged)
            : BuildSingle(enumType, current, onChanged);
    }

    private Control BuildSingle(Type enumType, DataNode? current, Action<DataNode?> onChanged)
    {
        var button = new OptionButton { HorizontalExpand = true };
        var values = Enum.GetValues(enumType);
        var names = Enum.GetNames(enumType);

        var currentText = current?.ToString();
        var selectedId = -1;
        var serializedByIndex = new DataNode?[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            button.AddItem(names[i], i);
            try
            {
                var node = _serMan.WriteValue(enumType, values.GetValue(i));
                serializedByIndex[i] = node;
                if (currentText != null && node.ToString() == currentText)
                    selectedId = i;
            }
            catch
            {
                // Leave serializedByIndex[i] null — selecting this entry will no-op.
            }
        }

        var invalidId = values.Length;
        if (selectedId < 0 && !string.IsNullOrEmpty(currentText))
        {
            button.AddItem(currentText, invalidId);
            selectedId = invalidId;
        }

        if (selectedId >= 0)
            button.SelectId(selectedId);

        button.OnItemSelected += args =>
        {
            button.SelectId(args.Id);
            if (args.Id == invalidId)
                return;

            var node = serializedByIndex[args.Id];
            if (node != null)
                onChanged(node);
        };

        return button;
    }

    private Control BuildFlags(Type enumType, DataNode? current, Action<DataNode?> onChanged)
    {
        // Skip the zero-valued "NONE"/"All"/meta entries. For a user-facing
        // checkbox list, only the single-bit flags are meaningful — composite
        // values like "All" would toggle every other box and confuse state.
        var flagEntries = Enum.GetValues(enumType)
            .Cast<Enum>()
            .Select(v => (Value: v, Name: Enum.GetName(enumType, v)!))
            .Where(e => IsSingleBit(Convert.ToInt64(e.Value)))
            .ToList();

        var currentFlags = ParseFlagsFromNode(enumType, current);

        // Preserve the original YAML casing for each flag so revert-to-inherited
        // compares byte-equal. SS14 YAML uses lowercase ("neck") but
        // Enum.GetName returns uppercase ("NECK"); without this map, toggling
        // a flag off and on would emit "NECK" and never match the inherited
        // "neck" node.
        var casing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (current is SequenceDataNode seqCurrent)
        {
            foreach (var elem in seqCurrent)
            {
                if (elem is ValueDataNode v && !string.IsNullOrEmpty(v.Value))
                    casing[v.Value] = v.Value;
            }
        }

        var list = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 1,
        };

        var checkboxes = new List<(Enum Value, CheckBox Box)>(flagEntries.Count);

        void Emit()
        {
            long acc = 0;
            var names = new List<string>();
            foreach (var (value, box) in checkboxes)
            {
                if (!box.Pressed) continue;
                acc |= Convert.ToInt64(value);
                var canonical = Enum.GetName(enumType, value)!;
                // Use the inherited casing for flags that were in the initial
                // node; for newly-added flags, default to lowercase to match
                // SS14's YAML convention.
                names.Add(casing.TryGetValue(canonical, out var orig)
                    ? orig
                    : canonical.ToLowerInvariant());
            }

            if (acc == 0)
            {
                // Empty-sequence is ambiguous (could mean NONE or "not set").
                // Drop the override entirely — null signals revert-to-inherited
                // at the component-row level.
                onChanged(null);
                return;
            }

            var seq = new SequenceDataNode();
            foreach (var n in names)
                seq.Add(new ValueDataNode(n));
            onChanged(seq);
        }

        foreach (var (value, name) in flagEntries)
        {
            var bit = Convert.ToInt64(value);
            var box = new CheckBox
            {
                Text = name,
                Pressed = (currentFlags & bit) == bit,
            };
            box.OnToggled += _ => Emit();
            checkboxes.Add((value, box));
            list.AddChild(box);
        }

        if (flagEntries.Count == 0)
        {
            list.AddChild(new Label
            {
                Text = "(no flags)",
                Modulate = Color.Gray,
            });
        }

        var panel = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = FlagsBackground,
                BorderColor = FlagsBorder,
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = 6,
                ContentMarginRightOverride = 6,
                ContentMarginTopOverride = 4,
                ContentMarginBottomOverride = 4,
            },
        };
        panel.AddChild(list);
        return panel;
    }

    private static bool IsSingleBit(long v) => v > 0 && (v & (v - 1)) == 0;

    private static long ParseFlagsFromNode(Type enumType, DataNode? node)
    {
        switch (node)
        {
            case SequenceDataNode seq:
            {
                long acc = 0;
                foreach (var elem in seq)
                {
                    if (elem is not ValueDataNode v) continue;
                    if (Enum.TryParse(enumType, v.Value, ignoreCase: true, out var parsed))
                        acc |= Convert.ToInt64(parsed);
                }
                return acc;
            }
            case ValueDataNode val when !string.IsNullOrEmpty(val.Value):
                // Also tolerate the "FOO, BAR" spelling that WriteConvertible
                // produces for a combined flag value.
                return Enum.TryParse(enumType, val.Value, ignoreCase: true, out var combined)
                    ? Convert.ToInt64(combined)
                    : 0;
            default:
                return 0;
        }
    }
}
