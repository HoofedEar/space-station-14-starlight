using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Dedicated editor for <c>List&lt;PrototypeLayerData&gt;</c> — the sprite component's
/// <c>layers</c> field. Each layer renders with a texture thumbnail, the common
/// set of per-layer fields (RSI path, state, visible, color, scale…), and
/// reorder/delete buttons. A generic list editor can't do this because layer
/// entries are mappings, not scalars, and the thumbnail pulls live textures
/// out of the running sprite system.
/// </summary>
public sealed class SpriteLayerListFieldEditor : IPrototypeFieldEditor
{
    private readonly FieldEditorRegistry _registry;
    private readonly IEntityManager _entMan;
    private readonly ISawmill _sawmill;

    // Set by PrototypeEditorWindow before building the sprite component's layers
    // field, so per-layer thumbnails can fall back to the component-level `sprite:`
    // when a layer only has `state:`. Captured into Build's closure so later edits
    // (via Refresh) keep seeing the same resolver even after it's cleared.
    private Func<string?>? _pendingComponentRsiResolver;

    private static readonly Color RowColorEven = Color.FromHex("#2b2b2b");
    private static readonly Color RowColorOdd = Color.FromHex("#333333");
    private static readonly Color BorderColor = Color.FromHex("#1a1a1a");
    private static readonly Color PreviewBgColor = Color.FromHex("#1f1f1f");

    public static bool Matches(Type t)
        => t.IsGenericType
           && t.GetGenericTypeDefinition() == typeof(List<>)
           && t.GetGenericArguments()[0] == typeof(PrototypeLayerData);

    public SpriteLayerListFieldEditor(FieldEditorRegistry registry, IEntityManager entMan, ISawmill sawmill)
    {
        _registry = registry;
        _entMan = entMan;
        _sawmill = sawmill;
    }

    /// <summary>
    /// Scope-set a resolver that returns the sprite component's current top-level
    /// <c>sprite:</c> RSI path. Per-layer thumbnails use it as a fallback when a
    /// layer row itself doesn't specify <c>sprite:</c>. Dispose the returned
    /// handle (or just let it go out of scope) to clear.
    /// </summary>
    public IDisposable UseComponentRsiResolver(Func<string?> resolver)
    {
        _pendingComponentRsiResolver = resolver;
        return new ResolverScope(() => _pendingComponentRsiResolver = null);
    }

    private sealed class ResolverScope : IDisposable
    {
        private readonly Action _dispose;
        public ResolverScope(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        var working = current is SequenceDataNode seq
            ? (SequenceDataNode)seq.Copy()
            : new SequenceDataNode();

        // Snapshot the resolver at Build time so subsequent Refresh() calls (after
        // the window clears the shared state) still see the correct fallback.
        var componentRsiResolver = _pendingComponentRsiResolver;

        var container = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        // Composed preview at the top: every visible layer drawn stacked. Updates
        // whenever Refresh fires (which is every mutation), so the user gets a
        // live read of what the sprite looks like as they edit.
        var previewHost = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = PreviewBgColor,
                BorderColor = BorderColor,
                BorderThickness = new Thickness(1),
            },
        };

        var rows = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 2,
        };

        void Refresh()
        {
            previewHost.DisposeAllChildren();
            previewHost.AddChild(BuildComposedPreview(working, componentRsiResolver));

            rows.DisposeAllChildren();

            if (working.Count == 0)
            {
                rows.AddChild(new Label
                {
                    Text = "(no layers)",
                    Modulate = Color.Gray,
                    Margin = new Thickness(6, 3),
                });
                return;
            }

            for (var i = 0; i < working.Count; i++)
            {
                var captured = i;
                var layerMap = working[i] as MappingDataNode ?? new MappingDataNode();
                // If the entry wasn't already a mapping, coerce so later edits land on the real node.
                if (!ReferenceEquals(working[i], layerMap))
                    working[i] = layerMap;

                rows.AddChild(BuildLayerRow(
                    layerMap,
                    captured,
                    isLast: captured == working.Count - 1,
                    componentRsiResolver,
                    commitMapping: () => { onChanged(working); RefreshPreview(); },
                    onDelete: () =>
                    {
                        working.RemoveAt(captured);
                        onChanged(working);
                        Refresh();
                    },
                    onMoveUp: () =>
                    {
                        if (captured <= 0) return;
                        (working[captured], working[captured - 1]) = (working[captured - 1], working[captured]);
                        onChanged(working);
                        Refresh();
                    },
                    onMoveDown: () =>
                    {
                        if (captured >= working.Count - 1) return;
                        (working[captured], working[captured + 1]) = (working[captured + 1], working[captured]);
                        onChanged(working);
                        Refresh();
                    }));
            }
        }

        void RefreshPreview()
        {
            previewHost.DisposeAllChildren();
            previewHost.AddChild(BuildComposedPreview(working, componentRsiResolver));
        }

        var addBtn = new Button
        {
            Text = "+ Add Layer",
            HorizontalAlignment = Control.HAlignment.Left,
            MinWidth = 100,
        };
        addBtn.OnPressed += _ =>
        {
            working.Add(new MappingDataNode());
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

        container.AddChild(previewHost);
        container.AddChild(rowsPanel);
        container.AddChild(addBtn);
        Refresh();
        return container;
    }

    private Control BuildLayerRow(
        MappingDataNode layer,
        int index,
        bool isLast,
        Func<string?>? componentRsiResolver,
        Action commitMapping,
        Action onDelete,
        Action onMoveUp,
        Action onMoveDown)
    {
        // Wrapper so the sprite/state fields can rebuild *this one row* on
        // focus-exit (to refresh the thumbnail and summary) without blowing
        // away the whole list and breaking focus on other rows.
        var rowWrapper = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };

        void RebuildThisRow()
        {
            rowWrapper.DisposeAllChildren();
            rowWrapper.AddChild(BuildLayerRowContent(
                layer,
                index,
                isLast,
                componentRsiResolver,
                commitMapping,
                onDelete,
                onMoveUp,
                onMoveDown,
                RebuildThisRow));
        }

        RebuildThisRow();

        var bg = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = index % 2 == 0 ? RowColorEven : RowColorOdd,
                ContentMarginLeftOverride = 6,
                ContentMarginRightOverride = 6,
                ContentMarginTopOverride = 4,
                ContentMarginBottomOverride = 4,
            },
        };
        bg.AddChild(rowWrapper);
        return bg;
    }

    private Control BuildLayerRowContent(
        MappingDataNode layer,
        int index,
        bool isLast,
        Func<string?>? componentRsiResolver,
        Action commitMapping,
        Action onDelete,
        Action onMoveUp,
        Action onMoveDown,
        Action refreshRow)
    {
        var rsiPath = GetString(layer, "sprite");
        var state = GetString(layer, "state");
        var effectiveRsi = rsiPath ?? componentRsiResolver?.Invoke();

        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        // Header row: thumbnail + summary + reorder/delete.
        var header = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8,
            VerticalAlignment = Control.VAlignment.Center,
        };

        header.AddChild(BuildThumbnail(effectiveRsi, state));

        header.AddChild(new Label
        {
            Text = $"#{index}",
            MinWidth = 28,
            HorizontalAlignment = Control.HAlignment.Right,
            Modulate = Color.Gray,
            StyleClasses = { "monospace" },
        });

        var summary = new Label
        {
            Text = SummarizeLayer(layer),
            HorizontalExpand = true,
            ClipText = true,
            StyleClasses = { "monospace" },
        };
        header.AddChild(summary);

        var upBtn = new Button { Text = "▲", MinWidth = 28, Disabled = index == 0 };
        upBtn.OnPressed += _ => onMoveUp();
        var downBtn = new Button { Text = "▼", MinWidth = 28, Disabled = isLast };
        downBtn.OnPressed += _ => onMoveDown();
        var delBtn = new Button { Text = "×", MinWidth = 28 };
        delBtn.OnPressed += _ => onDelete();
        header.AddChild(upBtn);
        header.AddChild(downBtn);
        header.AddChild(delBtn);

        content.AddChild(header);

        // Body: labeled fields.
        var body = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 2,
            Margin = new Thickness(24, 0, 0, 0),
        };

        // sprite/state changes need a row rebuild so the thumbnail and summary refresh.
        AddStringField(body, layer, "sprite", "RSI path", commitMapping, summary,
            onFocusExit: refreshRow);
        AddStringField(body, layer, "state", "State", commitMapping, summary,
            onFocusExit: refreshRow);
        AddStringField(body, layer, "texture", "Texture", commitMapping, summary);
        AddStringField(body, layer, "shader", "Shader", commitMapping, summary);
        AddRegistryField(body, layer, "visible", "Visible", typeof(bool), commitMapping, summary);
        AddRegistryField(body, layer, "color", "Color", typeof(Color), commitMapping, summary);
        AddRegistryField(body, layer, "scale", "Scale", typeof(Vector2), commitMapping, summary);
        AddRegistryField(body, layer, "offset", "Offset", typeof(Vector2), commitMapping, summary);
        AddRegistryField(body, layer, "rotation", "Rotation", typeof(Angle), commitMapping, summary);

        content.AddChild(body);
        return content;
    }

    // Custom string-field to avoid the registry's per-keystroke callback; this
    // one only refreshes the thumbnail on focus-exit so the user isn't kicked
    // out of the LineEdit mid-typing.
    private static void AddStringField(
        BoxContainer body,
        MappingDataNode layer,
        string key,
        string label,
        Action commit,
        Label summary,
        Action? onFocusExit = null)
    {
        var row = BuildLabeledRow(label);
        var initial = GetString(layer, key) ?? string.Empty;
        var edit = new LineEdit
        {
            HorizontalExpand = true,
            Text = initial,
        };

        edit.OnTextChanged += args =>
        {
            if (string.IsNullOrEmpty(args.Text))
                layer.Remove(key);
            else
                layer[key] = new ValueDataNode(args.Text);
            summary.Text = SummarizeLayer(layer);
            commit();
        };

        if (onFocusExit != null)
            edit.OnFocusExit += _ => onFocusExit();

        row.AddChild(edit);
        body.AddChild(row);
    }

    private void AddRegistryField(
        BoxContainer body,
        MappingDataNode layer,
        string key,
        string label,
        Type fieldType,
        Action commit,
        Label summary)
    {
        var row = BuildLabeledRow(label);
        var current = layer.TryGet(key, out var existing) ? existing : null;

        var editor = _registry.Build(fieldType, current, newNode =>
        {
            if (newNode == null)
                layer.Remove(key);
            else
                layer[key] = newNode;
            summary.Text = SummarizeLayer(layer);
            commit();
        });
        editor.HorizontalExpand = true;
        row.AddChild(editor);
        body.AddChild(row);
    }

    private static BoxContainer BuildLabeledRow(string label)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8,
            VerticalAlignment = Control.VAlignment.Center,
        };
        row.AddChild(new Label
        {
            Text = label,
            MinWidth = 80,
            Modulate = Color.Gray,
            StyleClasses = { "monospace" },
        });
        return row;
    }

    // Stacks every visible layer's Frame0 texture into one small preview box
    // so the user can see what the sprite composes to at a glance. Layer
    // rotation/shaders aren't rendered — v1 trades those for simplicity.
    private Control BuildComposedPreview(SequenceDataNode layers, Func<string?>? componentRsiResolver)
    {
        const float previewSize = 96f;

        var wrapper = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalAlignment = Control.HAlignment.Center,
            Margin = new Thickness(0, 6),
            SeparationOverride = 8,
            VerticalAlignment = Control.VAlignment.Center,
        };

        SpriteSystem? spriteSys = null;
        try { spriteSys = _entMan.System<SpriteSystem>(); }
        catch (Exception ex) { _sawmill.Debug($"Composed preview: sprite system unavailable: {ex.Message}"); }

        var host = new LayoutContainer
        {
            MinSize = new Vector2(previewSize, previewSize),
            SetSize = new Vector2(previewSize, previewSize),
        };

        var visibleLayerCount = 0;
        if (spriteSys != null)
        {
            foreach (var entry in layers)
            {
                if (entry is not MappingDataNode layer)
                    continue;

                var visibleStr = GetString(layer, "visible");
                if (string.Equals(visibleStr, "false", StringComparison.OrdinalIgnoreCase))
                    continue;

                var rsi = GetString(layer, "sprite") ?? componentRsiResolver?.Invoke();
                var state = GetString(layer, "state");
                if (string.IsNullOrEmpty(rsi) || string.IsNullOrEmpty(state))
                    continue;

                Texture texture;
                try
                {
                    texture = spriteSys.Frame0(new SpriteSpecifier.Rsi(new ResPath(rsi), state));
                }
                catch (Exception ex)
                {
                    _sawmill.Debug($"Composed preview layer skipped: sprite='{rsi}' state='{state}': {ex.Message}");
                    continue;
                }

                var modulate = TryParseColor(GetString(layer, "color")) ?? Color.White;
                var rect = new TextureRect
                {
                    Texture = texture,
                    Stretch = TextureRect.StretchMode.KeepAspectCentered,
                    Modulate = modulate,
                    SetSize = new Vector2(previewSize, previewSize),
                };
                LayoutContainer.SetPosition(rect, Vector2.Zero);
                host.AddChild(rect);
                visibleLayerCount++;
            }
        }

        if (visibleLayerCount == 0)
        {
            wrapper.AddChild(new Label
            {
                Text = "(nothing to preview yet — set sprite & state on a layer)",
                Modulate = Color.Gray,
                Margin = new Thickness(0, 8),
            });
            return wrapper;
        }

        wrapper.AddChild(host);
        wrapper.AddChild(new Label
        {
            Text = $"{visibleLayerCount} visible layer{(visibleLayerCount == 1 ? "" : "s")}",
            Modulate = Color.Gray,
            StyleClasses = { "monospace" },
        });
        return wrapper;
    }

    private static Color? TryParseColor(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (Color.TryFromName(value, out var named))
            return named;
        return Color.TryFromHex(value);
    }

    private Control BuildThumbnail(string? rsiPath, string? state)
    {
        const int size = 40;

        if (string.IsNullOrEmpty(rsiPath) || string.IsNullOrEmpty(state))
        {
            return new Label
            {
                Text = "?",
                MinSize = new Vector2(size, size),
                SetSize = new Vector2(size, size),
                HorizontalAlignment = Control.HAlignment.Center,
                VerticalAlignment = Control.VAlignment.Center,
                Modulate = Color.Gray,
            };
        }

        try
        {
            var spriteSys = _entMan.System<SpriteSystem>();
            var texture = spriteSys.Frame0(
                new SpriteSpecifier.Rsi(new ResPath(rsiPath), state));
            return new TextureRect
            {
                Texture = texture,
                Stretch = TextureRect.StretchMode.KeepAspectCentered,
                MinSize = new Vector2(size, size),
                SetSize = new Vector2(size, size),
            };
        }
        catch (Exception ex)
        {
            // Missing RSI/state is normal mid-edit. Log at Debug so a user can
            // flip verbose logging on and see what path/state failed, but we
            // don't spam the default log.
            _sawmill.Debug($"Layer thumbnail failed for sprite='{rsiPath}' state='{state}': {ex.Message}");
            return new Label
            {
                Text = "⚠",
                MinSize = new Vector2(size, size),
                SetSize = new Vector2(size, size),
                HorizontalAlignment = Control.HAlignment.Center,
                VerticalAlignment = Control.VAlignment.Center,
                Modulate = Color.OrangeRed,
            };
        }
    }

    private static string SummarizeLayer(MappingDataNode layer)
    {
        var rsi = GetString(layer, "sprite");
        var state = GetString(layer, "state");
        var texture = GetString(layer, "texture");
        var visible = GetString(layer, "visible");

        string head;
        if (!string.IsNullOrEmpty(rsi) || !string.IsNullOrEmpty(state))
            head = $"{rsi ?? "(default rsi)"} : {state ?? "(no state)"}";
        else if (!string.IsNullOrEmpty(texture))
            head = $"texture: {texture}";
        else
            head = "(empty layer)";

        if (string.Equals(visible, "false", StringComparison.OrdinalIgnoreCase))
            head += "  [hidden]";

        return head;
    }

    private static string? GetString(MappingDataNode map, string key)
        => map.TryGet(key, out var node) && node is ValueDataNode v ? v.Value : null;
}
