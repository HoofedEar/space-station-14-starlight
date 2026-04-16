using System.Collections.Immutable;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Generic sequence editor. Supports <see cref="List{T}"/>,
/// <see cref="HashSet{T}"/>, <see cref="ImmutableHashSet{T}"/>,
/// <see cref="IReadOnlyList{T}"/>, <see cref="IList{T}"/>, and
/// <see cref="ICollection{T}"/>. Each entry renders with the registry-resolved
/// child editor for the element type — so a <c>List&lt;ProtoId&lt;T&gt;&gt;</c>
/// gets a prototype dropdown per row for free.
/// </summary>
/// <remarks>
/// The set-flavored types (<c>HashSet</c>, <c>ImmutableHashSet</c>) accept duplicates
/// at edit time — Robust's serializer normalizes on save, so we don't block.
/// </remarks>
public sealed class ListFieldEditor : IPrototypeFieldEditor
{
    private readonly FieldEditorRegistry _registry;

    private static readonly Color RowColorEven = Color.FromHex("#2b2b2b");
    private static readonly Color RowColorOdd = Color.FromHex("#333333");
    private static readonly Color BorderColor = Color.FromHex("#1a1a1a");

    public ListFieldEditor(FieldEditorRegistry registry) => _registry = registry;

    public static bool IsList(Type t) => TryGetElementType(t, out _);

    private static bool TryGetElementType(Type t, out Type elementType)
    {
        elementType = typeof(object);
        if (!t.IsGenericType)
            return false;

        var def = t.GetGenericTypeDefinition();
        if (def != typeof(List<>)
            && def != typeof(HashSet<>)
            && def != typeof(ImmutableHashSet<>)
            && def != typeof(IList<>)
            && def != typeof(IReadOnlyList<>)
            && def != typeof(ICollection<>)
            && def != typeof(IReadOnlyCollection<>)
            && def != typeof(IEnumerable<>))
            return false;

        elementType = t.GetGenericArguments()[0];
        return true;
    }

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        if (!TryGetElementType(fieldType, out var elementType))
        {
            return new Label
            {
                Text = $"<not a list: {fieldType.Name}>",
                Modulate = Color.OrangeRed,
            };
        }

        // Clone so intermediate edits don't bleed into the parent mapping
        // until onChanged commits upward.
        var working = current is SequenceDataNode src ? (SequenceDataNode)src.Copy() : new SequenceDataNode();

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
            SeparationOverride = 0,
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

            for (var i = 0; i < working.Count; i++)
            {
                var index = i;
                rows.AddChild(BuildEntryRow(working[index], elementType, index,
                    onDelete: () =>
                    {
                        working.RemoveAt(index);
                        onChanged(working);
                        Refresh();
                    },
                    onValueChange: newValue =>
                    {
                        working[index] = newValue;
                        onChanged(working);
                    }));
            }
        }

        var addBtn = new Button
        {
            Text = "+ Add",
            HorizontalAlignment = Control.HAlignment.Left,
            MinWidth = 80,
        };
        addBtn.OnPressed += _ =>
        {
            // Start new entries with an empty value — the registry-resolved
            // child editor will render something reasonable (dropdown selects
            // "(none)", LineEdit is blank, etc.) and the user fills it in.
            working.Add(new ValueDataNode(string.Empty));
            onChanged(working);
            Refresh();
        };

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
        container.AddChild(addBtn);
        Refresh();
        return container;
    }

    private Control BuildEntryRow(
        DataNode value,
        Type elementType,
        int index,
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
            Text = index.ToString(),
            MinWidth = 28,
            HorizontalAlignment = Control.HAlignment.Right,
            Modulate = Color.Gray,
            StyleClasses = { "monospace" },
        });

        // Null from a sub-editor means "revert to inherited" at the top level;
        // inside a list there's no inherited element to fall back to, so we
        // swallow null. User must press × to remove.
        var valueEditor = _registry.Build(elementType, value, newNode =>
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
                BackgroundColor = index % 2 == 0 ? RowColorEven : RowColorOdd,
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
