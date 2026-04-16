using System.Globalization;
using System.Linq;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Editor for <see cref="DamageSpecifier"/>. Renders two sections — <c>types</c>
/// (direct <see cref="DamageTypePrototype"/> IDs) and <c>groups</c>
/// (<see cref="DamageGroupPrototype"/> IDs, which distribute their value across
/// their constituent types on load). Each section has a dropdown-driven add
/// bar so users can only pick IDs the game actually knows about.
/// </summary>
/// <remarks>
/// Keys are immutable (delete + re-add to rename) — matches
/// <see cref="DictionaryFieldEditor"/>. Emitted YAML matches the canonical
/// shape <c>damage: { types: {...}, groups: {...} }</c>, so revert-to-inherited
/// byte-compare works for the common case where the inherited node follows
/// the same ordering and value formatting.
/// </remarks>
public sealed class DamageSpecifierFieldEditor : IPrototypeFieldEditor
{
    private readonly IPrototypeManager _protos;

    private static readonly Color RowColorEven = Color.FromHex("#2b2b2b");
    private static readonly Color RowColorOdd = Color.FromHex("#333333");
    private static readonly Color BorderColor = Color.FromHex("#1a1a1a");

    public DamageSpecifierFieldEditor(IPrototypeManager protos) => _protos = protos;

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var src = current as MappingDataNode;
        var types = ExtractChildMap(src, "types");
        var groups = ExtractChildMap(src, "groups");

        var container = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 6,
        };

        void Emit()
        {
            // Only include a section key if it has entries; empty sub-mappings
            // would differ from the inherited shape and block revert detection.
            var combined = new MappingDataNode();
            if (types.Count > 0) combined["types"] = types;
            if (groups.Count > 0) combined["groups"] = groups;

            if (combined.Count == 0)
            {
                onChanged(null);
                return;
            }

            onChanged(combined);
        }

        container.AddChild(BuildSection("types", types, typeof(DamageTypePrototype), Emit));
        container.AddChild(BuildSection("groups", groups, typeof(DamageGroupPrototype), Emit));

        return container;
    }

    private static MappingDataNode ExtractChildMap(MappingDataNode? parent, string key)
    {
        if (parent == null || !parent.Has(key))
            return new MappingDataNode();
        if (parent[key] is not MappingDataNode existing)
            return new MappingDataNode();
        return existing.Copy();
    }

    private Control BuildSection(string title, MappingDataNode dict, Type protoKind, Action onChanged)
    {
        var section = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 2,
        };

        section.AddChild(new Label
        {
            Text = title,
            StyleClasses = { "monospace" },
            Modulate = Color.FromHex("#a0a0a0"),
            Margin = new Thickness(2, 0),
        });

        var rows = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 0,
        };

        void Refresh()
        {
            rows.DisposeAllChildren();

            if (dict.Count == 0)
            {
                rows.AddChild(new Label
                {
                    Text = "(none)",
                    Modulate = Color.Gray,
                    Margin = new Thickness(6, 3),
                });
                return;
            }

            var index = 0;
            foreach (var pair in dict)
            {
                var entryKey = pair.Key;
                var rowIndex = index++;
                rows.AddChild(BuildEntryRow(dict, entryKey, rowIndex,
                    onDelete: () =>
                    {
                        dict.Remove(entryKey);
                        onChanged();
                        Refresh();
                    },
                    onChange: onChanged));
            }
        }

        var rowsPanel = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = RowColorEven,
                BorderColor = BorderColor,
                BorderThickness = new Thickness(1),
            },
        };
        rowsPanel.AddChild(rows);

        section.AddChild(rowsPanel);
        section.AddChild(BuildAddBar(dict, protoKind, () =>
        {
            onChanged();
            Refresh();
        }));

        Refresh();
        return section;
    }

    private Control BuildEntryRow(MappingDataNode dict, string key, int rowIndex,
        Action onDelete, Action onChange)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6,
            VerticalAlignment = Control.VAlignment.Center,
        };

        row.AddChild(new Label
        {
            Text = key,
            MinWidth = 140,
            ClipText = true,
            StyleClasses = { "monospace" },
        });

        var valueEdit = new LineEdit
        {
            HorizontalExpand = true,
            Text = (dict[key] as ValueDataNode)?.Value ?? string.Empty,
            PlaceHolder = "amount",
        };
        valueEdit.OnTextChanged += args =>
        {
            var text = args.Text.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return;

            dict[key] = new ValueDataNode(text);
            onChange();
        };
        row.AddChild(valueEdit);

        var delBtn = new Button { Text = "×", MinWidth = 24 };
        delBtn.OnPressed += _ => onDelete();
        row.AddChild(delBtn);

        var bg = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = rowIndex % 2 == 0 ? RowColorEven : RowColorOdd,
                ContentMarginLeftOverride = 6,
                ContentMarginRightOverride = 6,
                ContentMarginTopOverride = 3,
                ContentMarginBottomOverride = 3,
            },
        };
        bg.AddChild(row);
        return bg;
    }

    private Control BuildAddBar(MappingDataNode dict, Type protoKind, Action onAdd)
    {
        var bar = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        var ids = _protos.EnumeratePrototypes(protoKind)
            .Select(p => p.ID)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dropdown = new OptionButton { HorizontalExpand = true };
        dropdown.AddItem("(pick to add)", 0);
        for (var i = 0; i < ids.Count; i++)
            dropdown.AddItem(ids[i], i + 1);
        dropdown.SelectId(0);
        dropdown.OnItemSelected += args => dropdown.SelectId(args.Id);

        var addBtn = new Button { Text = "+ Add", MinWidth = 70 };
        addBtn.OnPressed += _ =>
        {
            var selected = dropdown.SelectedId;
            if (selected <= 0 || selected > ids.Count)
                return;

            var id = ids[selected - 1];
            if (dict.Has(id))
                return;

            dict[id] = new ValueDataNode("0");
            dropdown.SelectId(0);
            onAdd();
        };

        bar.AddChild(dropdown);
        bar.AddChild(addBtn);
        return bar;
    }
}
