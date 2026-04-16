using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Generic dictionary editor. Renders each entry as <c>[key : value-editor × ]</c>,
/// with an "Add" row at the bottom. Value editors come from the same
/// <see cref="FieldEditorRegistry"/> so a <c>Dictionary&lt;string, Color&gt;</c>
/// automatically gets the color swatch editor for its values.
/// </summary>
/// <remarks>
/// Keys are immutable: to rename an entry, delete it and re-add with the new
/// key. This sidesteps in-place rename complications (collision handling,
/// preserving value across key change) and matches the user-agreed scope.
/// </remarks>
public sealed class DictionaryFieldEditor : IPrototypeFieldEditor
{
    private readonly FieldEditorRegistry _registry;

    // Kept in sync with PrototypeEditorWindow's row palette so nested dict
    // rows read as part of the same visual language as component fields.
    private static readonly Color RowColorEven = Color.FromHex("#2b2b2b");
    private static readonly Color RowColorOdd = Color.FromHex("#333333");
    private static readonly Color BorderColor = Color.FromHex("#1a1a1a");

    public DictionaryFieldEditor(FieldEditorRegistry registry) => _registry = registry;

    public static bool IsDictionary(Type t)
        => TryGetDictionaryArgs(t, out _, out _);

    private static bool TryGetDictionaryArgs(Type t, out Type keyType, out Type valueType)
    {
        keyType = valueType = typeof(object);
        if (!t.IsGenericType)
            return false;

        var def = t.GetGenericTypeDefinition();
        if (def != typeof(Dictionary<,>)
            && def != typeof(SortedDictionary<,>)
            && def != typeof(IDictionary<,>)
            && def != typeof(IReadOnlyDictionary<,>))
            return false;

        var args = t.GetGenericArguments();
        keyType = args[0];
        valueType = args[1];
        return true;
    }

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        if (!TryGetDictionaryArgs(fieldType, out _, out var valueType))
        {
            return new Label
            {
                Text = $"<not a dictionary: {fieldType.Name}>",
                Modulate = Color.OrangeRed,
            };
        }

        // Work on a clone so intermediate state doesn't leak into the parent
        // mapping until we emit. onChanged is what commits our changes upward.
        var working = current is MappingDataNode src ? src.Copy() : new MappingDataNode();

        var container = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 2,
        };

        var rows = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 1,
        };

        void Refresh()
        {
            rows.DisposeAllChildren();

            if (working.Count == 0)
            {
                rows.AddChild(new Label
                {
                    Text = "(empty)",
                    Modulate = Color.Gray,
                    Margin = new Thickness(6, 3),
                });
                return;
            }

            var index = 0;
            foreach (var pair in working)
            {
                var entryKey = pair.Key;
                var rowIndex = index++;
                rows.AddChild(BuildEntryRow(entryKey, working[entryKey], valueType, rowIndex,
                    onDelete: () =>
                    {
                        working.Remove(entryKey);
                        onChanged(working);
                        Refresh();
                    },
                    onValueChange: newValue =>
                    {
                        working[entryKey] = newValue;
                        onChanged(working);
                    }));
            }
        }

        var addBar = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };
        var keyInput = new LineEdit
        {
            HorizontalExpand = true,
            PlaceHolder = "new key",
        };
        var addBtn = new Button { Text = "+ Add", MinWidth = 60 };
        addBtn.OnPressed += _ =>
        {
            var key = keyInput.Text.Trim();
            if (string.IsNullOrEmpty(key) || working.Has(key))
                return;

            working[key] = new ValueDataNode(string.Empty);
            keyInput.Text = string.Empty;
            onChanged(working);
            Refresh();
        };
        addBar.AddChild(keyInput);
        addBar.AddChild(addBtn);

        // Thin border around the rows section so the dictionary body reads as
        // a distinct group even when nested inside an already-tinted field row.
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

        container.AddChild(rowsPanel);
        container.AddChild(addBar);
        Refresh();
        return container;
    }

    private Control BuildEntryRow(
        string key,
        DataNode value,
        Type valueType,
        int rowIndex,
        Action onDelete,
        Action<DataNode> onValueChange)
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
            MinWidth = 120,
            ClipText = true,
            StyleClasses = { "monospace" },
        });

        // Null from a sub-editor means "revert to inherited" at the top level,
        // but inside a dict we have no inherited value to fall back to — a dict
        // entry always has a value. So we swallow null and keep the current
        // node unchanged. User must press × to remove the entry.
        var valueEditor = _registry.Build(valueType, value, newNode =>
        {
            if (newNode == null)
                return;
            onValueChange(newNode);
        });
        valueEditor.HorizontalExpand = true;
        row.AddChild(valueEditor);

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
}
