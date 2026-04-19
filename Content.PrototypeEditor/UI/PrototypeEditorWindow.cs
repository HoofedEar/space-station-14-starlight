using System.Linq;
using System.Numerics;
using Content.PrototypeEditor.FieldEditors;
using Content.PrototypeEditor.Reflection;
using Content.Shared.Damage;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.UI;

/// <summary>
/// Three-pane editor shell. Left: kind + search + prototype list.
/// Center: tabbed detail view (Components / YAML).
/// Right: reserved for inheritance chain and preview (stubbed).
/// </summary>
public sealed class PrototypeEditorWindow : DefaultWindow
{
    [Dependency] private readonly IPrototypeManager _protos = default!;
    [Dependency] private readonly ISerializationManager _serMan = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly ILogManager _logMan = default!;

    private readonly ISawmill _sawmill;

    private readonly OptionButton _kindDropdown;
    private readonly LineEdit _searchBar;
    private readonly BoxContainer _prototypeList;
    private readonly Label _detailHeader;
    private readonly BoxContainer _componentsList;
    private readonly Label _yamlBody;
    private readonly BoxContainer _inheritanceList;
    private readonly EntityPrototypeView _preview;
    private readonly Label _previewCaption;

    private readonly List<string> _kinds = new();
    private List<IPrototype> _currentKindEntries = new();
    private string _searchText = string.Empty;
    private IPrototype? _selectedProto;

    private readonly FieldEditorRegistry _fieldEditors;
    private readonly SpriteLayerListFieldEditor _spriteLayerEditor;
    private readonly ComponentDocResolver _docs = new();

    public PrototypeEditorWindow()
    {
        IoCManager.InjectDependencies(this);
        _sawmill = _logMan.GetSawmill("proto-editor");

        _fieldEditors = new FieldEditorRegistry(new RawYamlFieldEditor());
        _fieldEditors.RegisterExact<string>(new StringFieldEditor());
        _fieldEditors.RegisterExact<bool>(new BoolFieldEditor());
        NumericFieldEditor.RegisterAll(_fieldEditors);
        _fieldEditors.RegisterExact<Color>(new ColorFieldEditor());
        _fieldEditors.RegisterExact<Vector2>(new Vector2FieldEditor(intVec: false));
        _fieldEditors.RegisterExact<Vector2i>(new Vector2FieldEditor(intVec: true));
        _fieldEditors.RegisterExact<Angle>(new AngleFieldEditor());
        _fieldEditors.RegisterExact<TimeSpan>(new TimeSpanFieldEditor());
        _fieldEditors.RegisterExact<DamageSpecifier>(new DamageSpecifierFieldEditor(_protos));
        _fieldEditors.RegisterAssignable<SoundSpecifier>(new SoundSpecifierFieldEditor(_protos));
        _fieldEditors.RegisterAssignable<Enum>(new EnumFieldEditor(_serMan));
        _fieldEditors.RegisterPredicate(ProtoIdFieldEditor.IsProtoId, new ProtoIdFieldEditor(_protos, _compFactory));
        _fieldEditors.RegisterPredicate(DictionaryFieldEditor.IsDictionary, new DictionaryFieldEditor(_fieldEditors));
        // Sprite layers must match before the generic list editor; that one assumes
        // scalar element types and would dump raw YAML for each layer otherwise.
        _spriteLayerEditor = new SpriteLayerListFieldEditor(_fieldEditors, _entMan, _sawmill);
        _fieldEditors.RegisterPredicate(SpriteLayerListFieldEditor.Matches, _spriteLayerEditor);
        _fieldEditors.RegisterPredicate(ListFieldEditor.IsList, new ListFieldEditor(_fieldEditors));

        Title = "Prototype Editor";

        // The editor is the only UI in its state, so it fills the viewport rather
        // than behaving like a draggable sub-window. Resize and close are disabled
        // for the same reason — the app window's close button is the only exit.
        Resizable = false;
        CloseButton.Visible = false;

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 4,
        };

        root.AddChild(BuildLeftPane(out _kindDropdown, out _searchBar, out _prototypeList));
        root.AddChild(BuildCenterPane(out _detailHeader, out _componentsList, out _yamlBody));
        root.AddChild(BuildRightPane(_entMan, out _inheritanceList, out _preview, out _previewCaption));

        Contents.AddChild(root);

        PopulateKinds();
        PopulatePrototypeList();
    }

    private static Control BuildLeftPane(
        out OptionButton kindDropdown,
        out LineEdit searchBar,
        out BoxContainer list)
    {
        var pane = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MinWidth = 280,
            SeparationOverride = 4,
            VerticalExpand = true,
        };

        kindDropdown = new OptionButton { HorizontalExpand = true };
        searchBar = new LineEdit
        {
            PlaceHolder = "Search by ID…",
            HorizontalExpand = true,
        };

        list = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 1,
        };

        var scroll = new ScrollContainer
        {
            VerticalExpand = true,
            HorizontalExpand = true,
        };
        scroll.AddChild(list);

        pane.AddChild(kindDropdown);
        pane.AddChild(searchBar);
        pane.AddChild(scroll);
        return pane;
    }

    private static Control BuildCenterPane(
        out Label header,
        out BoxContainer componentsList,
        out Label yamlBody)
    {
        var pane = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 4,
        };

        header = new Label
        {
            Text = "(select a prototype)",
            StyleClasses = { "LabelHeading" },
        };

        var tabs = new TabContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
        };

        componentsList = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 6,
            HorizontalExpand = true,
        };
        var componentsScroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            // Disable horizontal scrolling so the inner BoxContainer is forced to
            // take the container's full width — otherwise field rows collapse to
            // their minimum size and the editors look cramped.
            HScrollEnabled = false,
        };
        componentsScroll.AddChild(componentsList);
        tabs.AddChild(componentsScroll);
        TabContainer.SetTabTitle(componentsScroll, "Components");

        yamlBody = new Label();
        var yamlScroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        yamlScroll.AddChild(yamlBody);
        tabs.AddChild(yamlScroll);
        TabContainer.SetTabTitle(yamlScroll, "YAML");

        pane.AddChild(header);
        pane.AddChild(tabs);
        return pane;
    }

    private static Control BuildRightPane(
        IEntityManager entMan,
        out BoxContainer inheritanceList,
        out EntityPrototypeView preview,
        out Label previewCaption)
    {
        var pane = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MinWidth = 260,
            SeparationOverride = 4,
            VerticalExpand = true,
        };

        pane.AddChild(new Label
        {
            Text = "Preview",
            StyleClasses = { "LabelHeading" },
        });

        // Fixed-size framed preview — keeps layout stable when switching between
        // entities with very different sprite footprints.
        preview = new EntityPrototypeView(null, entMan)
        {
            MinSize = new Vector2(128, 128),
            SetSize = new Vector2(128, 128),
            Stretch = SpriteView.StretchMode.Fit,
            HorizontalAlignment = HAlignment.Center,
        };
        var previewFrame = new PanelContainer
        {
            HorizontalAlignment = HAlignment.Center,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#1f1f1f"),
                BorderColor = Color.FromHex("#1a1a1a"),
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = 6,
                ContentMarginRightOverride = 6,
                ContentMarginTopOverride = 6,
                ContentMarginBottomOverride = 6,
            },
        };
        previewFrame.AddChild(preview);
        pane.AddChild(previewFrame);

        previewCaption = new Label
        {
            Modulate = Color.Gray,
            HorizontalAlignment = HAlignment.Center,
            Text = string.Empty,
        };
        pane.AddChild(previewCaption);

        pane.AddChild(new Label
        {
            Text = "Inheritance",
            StyleClasses = { "LabelHeading" },
        });

        inheritanceList = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 1,
            HorizontalExpand = true,
        };
        var scroll = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            HScrollEnabled = false,
        };
        scroll.AddChild(inheritanceList);
        pane.AddChild(scroll);

        return pane;
    }

    private void PopulateKinds()
    {
        _kinds.Clear();
        _kinds.AddRange(_protos.GetPrototypeKinds().OrderBy(k => k));

        _kindDropdown.Clear();
        for (var i = 0; i < _kinds.Count; i++)
        {
            _kindDropdown.AddItem(_kinds[i], i);
            if (_kinds[i] == "entity")
                _kindDropdown.SelectId(i);
        }

        _kindDropdown.OnItemSelected += args =>
        {
            _kindDropdown.SelectId(args.Id);
            PopulatePrototypeList();
        };

        _searchBar.OnTextChanged += args =>
        {
            _searchText = args.Text;
            RenderList();
        };
    }

    private void PopulatePrototypeList()
    {
        _currentKindEntries = new List<IPrototype>();
        if (_kinds.Count == 0)
        {
            RenderList();
            return;
        }

        var kind = _kinds[_kindDropdown.SelectedId];
        foreach (var proto in _protos.EnumeratePrototypes(kind))
            _currentKindEntries.Add(proto);

        _currentKindEntries = _currentKindEntries
            .OrderBy(p => p.ID, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RenderList();
    }

    private void RenderList()
    {
        _prototypeList.DisposeAllChildren();

        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? _currentKindEntries
            : _currentKindEntries
                .Where(p => p.ID.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                .ToList();

        foreach (var proto in filtered)
        {
            var captured = proto;
            var btn = new Button
            {
                Text = captured.ID,
                HorizontalAlignment = HAlignment.Stretch,
                ClipText = true,
            };
            btn.OnPressed += _ => Select(captured);
            _prototypeList.AddChild(btn);
        }

        if (filtered.Count == 0)
        {
            _prototypeList.AddChild(new Label
            {
                Text = "(no matches)",
                HorizontalAlignment = HAlignment.Center,
            });
        }
    }

    private void Select(IPrototype proto)
    {
        _selectedProto = proto;
        _detailHeader.Text = $"{proto.GetType().Name}: {proto.ID}";

        RenderComponents(proto);
        RenderYaml(proto);
        RenderInheritance(proto);
        RenderPreview(proto);
    }

    private void RenderPreview(IPrototype proto)
    {
        // Abstract prototypes can't be spawned; non-entity prototypes have
        // nothing sprite-shaped to show. Either way we clear to avoid keeping
        // a stale sprite on-screen.
        if (proto is not EntityPrototype entity || entity.Abstract)
        {
            TryClearPreview();
            _previewCaption.Text = proto is EntityPrototype { Abstract: true }
                ? "(abstract — not spawnable)"
                : "(no preview)";
            return;
        }

        try
        {
            _preview.SetPrototype(entity.ID);
            _previewCaption.Text = string.Empty;
            _previewCaption.ToolTip = null;
        }
        catch (Exception ex)
        {
            // Full stack goes to the log — the caption only has room for a
            // short hint, but hovering shows the first stack frame so the
            // user can sanity-check without opening the log file.
            _sawmill.Error($"Preview failed for prototype '{entity.ID}': {ex}");
            TryClearPreview();
            _previewCaption.Text = $"(preview failed: {ex.GetType().Name})";
            _previewCaption.ToolTip = $"{ex.GetType().FullName}: {ex.Message}\n\n{FirstFrame(ex)}";
        }
    }

    private static string FirstFrame(Exception ex)
    {
        var stack = ex.StackTrace;
        if (string.IsNullOrEmpty(stack)) return "(no stack)";
        var nl = stack.IndexOf('\n');
        return nl < 0 ? stack : stack[..nl].TrimEnd('\r');
    }

    private void TryClearPreview()
    {
        // SetPrototype(null) itself can throw if the view is in a bad state
        // (e.g. a prior spawn only half-succeeded). Swallow so that an error
        // on one prototype doesn't also break switching to the next one.
        try { _preview.SetPrototype(null); }
        catch (Exception ex) { _sawmill.Warning($"Clearing preview failed: {ex.Message}"); }
    }

    private void RenderInheritance(IPrototype proto)
    {
        _inheritanceList.DisposeAllChildren();

        if (proto is not IInheritingPrototype)
        {
            _inheritanceList.AddChild(new Label
            {
                Text = "(no inheritance)",
                Modulate = Color.Gray,
                HorizontalAlignment = HAlignment.Center,
                Margin = new Thickness(0, 8),
            });
            return;
        }

        // DFS walks depth-first through multi-inheritance; `seen` prevents
        // diamond-shape duplicates (e.g. two parents sharing an ancestor).
        var chain = new List<(int Depth, IPrototype Proto)>();
        var seen = new HashSet<string>();
        var kind = proto.GetType();

        void Walk(IPrototype p, int depth)
        {
            if (!seen.Add(p.ID))
                return;
            chain.Add((depth, p));
            if (p is not IInheritingPrototype { Parents: { } parents })
                return;
            foreach (var parentId in parents)
            {
                if (_protos.TryIndex(kind, parentId, out var parentProto))
                    Walk(parentProto, depth + 1);
            }
        }

        Walk(proto, 0);

        for (var i = 0; i < chain.Count; i++)
        {
            var (depth, p) = chain[i];
            var captured = p;
            var isSelf = i == 0;
            var btn = new Button
            {
                Text = new string(' ', depth * 2) + (isSelf ? "● " : "↑ ") + p.ID,
                HorizontalAlignment = HAlignment.Stretch,
                ClipText = true,
                Disabled = isSelf,
                StyleClasses = { "monospace" },
                ToolTip = isSelf ? "Currently selected" : "Jump to this ancestor",
            };
            if (!isSelf)
                btn.OnPressed += _ => Select(captured);
            _inheritanceList.AddChild(btn);
        }
    }

    private void RenderComponents(IPrototype proto)
    {
        _componentsList.DisposeAllChildren();

        if (proto is not EntityPrototype entity)
        {
            _componentsList.AddChild(new Label
            {
                Text = "(components view is only available for entity prototypes)",
                HorizontalAlignment = HAlignment.Center,
                Margin = new Thickness(0, 12),
            });
            return;
        }

        foreach (var (name, entry) in entity.Components.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var parentMapping = BuildParentComponentMapping(entity, name);
            var body = BuildComponentBody(entry.Component, entry.Mapping, parentMapping);
            var heading = BuildComponentHeading(name);
            var collapsible = new Collapsible(heading, body)
            {
                HorizontalExpand = true,
            };
            _componentsList.AddChild(WrapComponentCard(collapsible));
        }

        if (entity.Components.Count == 0)
        {
            _componentsList.AddChild(new Label
            {
                Text = "(no components)",
                HorizontalAlignment = HAlignment.Center,
            });
        }
    }

    private static readonly Color ComponentCardBorder = Color.FromHex("#4a4a4a");
    private static readonly Color SummaryTextColor = Color.FromHex("#a8a8a8");
    private static readonly Color SummaryStripeColor = Color.FromHex("#6fa8dc");

    // Renders the XML doc <summary> as a soft-colored, wrapped paragraph
    // inside a thin left-stripe panel. The stripe doubles as a visual cue
    // that this row is metadata and not an editable field.
    private static Control BuildSummaryLabel(string summary)
    {
        var label = new RichTextLabel
        {
            HorizontalExpand = true,
            Margin = new Thickness(0, 0, 0, 6),
        };
        label.SetMessage(summary, SummaryTextColor);

        var panel = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.Transparent,
                BorderColor = SummaryStripeColor,
                BorderThickness = new Thickness(2, 0, 0, 0),
                ContentMarginLeftOverride = 8,
                ContentMarginRightOverride = 4,
                ContentMarginTopOverride = 2,
                ContentMarginBottomOverride = 2,
            },
            Margin = new Thickness(0, 0, 0, 4),
        };
        panel.AddChild(label);
        return panel;
    }

    // A thin panel around each collapsible so neighboring components don't blur
    // together — especially when multiple are expanded at once.
    private static Control WrapComponentCard(Control collapsible)
    {
        var panel = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.Transparent,
                BorderColor = ComponentCardBorder,
                BorderThickness = new Thickness(1),
            },
        };
        panel.AddChild(collapsible);
        return panel;
    }

    // The stock CollapsibleHeading only sizes to its text, so a short component
    // name like "Transform" gives a tiny click target. Stretch the whole heading
    // across the pane and center the label in the space left of the chevron so
    // it reads as the header for everything below it.
    private static CollapsibleHeading BuildComponentHeading(string name)
    {
        var heading = new CollapsibleHeading(name)
        {
            HorizontalExpand = true,
        };
        heading.Label.HorizontalExpand = true;
        heading.Label.Align = Label.AlignMode.Center;
        if (heading.Label.Parent is BoxContainer inner)
            inner.HorizontalExpand = true;
        return heading;
    }

    private CollapsibleBody BuildComponentBody(object component, MappingDataNode mapping, MappingDataNode? parentMapping)
    {
        var body = new CollapsibleBody { HorizontalExpand = true };
        var rows = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 0,
            HorizontalExpand = true,
            Margin = new Thickness(8, 4, 4, 4),
        };

        var summary = _docs.GetSummary(component.GetType());
        if (!string.IsNullOrEmpty(summary))
            rows.AddChild(BuildSummaryLabel(summary));

        // For a SpriteComponent, tell the layer editor how to read the live
        // top-level `sprite:` so state-only layers can resolve a thumbnail.
        // Scope-bound: only this component's field builds see the resolver.
        IDisposable? spriteScope = null;
        if (component is SpriteComponent)
        {
            spriteScope = _spriteLayerEditor.UseComponentRsiResolver(() =>
                ReadSpriteRsi(mapping) ?? ReadSpriteRsi(parentMapping));
        }

        try
        {
            var fieldCount = 0;
            foreach (var field in ComponentDataFieldReader.GetFields(component))
            {
                rows.AddChild(BuildFieldRow(field, mapping, parentMapping, fieldCount));
                fieldCount++;
            }

            if (fieldCount == 0)
            {
                rows.AddChild(new Label
                {
                    Text = "(no data fields)",
                    Modulate = Color.Gray,
                });
            }
        }
        finally
        {
            spriteScope?.Dispose();
        }

        body.AddChild(rows);
        return body;
    }

    private static string? ReadSpriteRsi(MappingDataNode? mapping)
    {
        if (mapping == null)
            return null;
        return mapping.TryGet("sprite", out var node) && node is ValueDataNode v && !string.IsNullOrEmpty(v.Value)
            ? v.Value
            : null;
    }

    // Inheritance state for a single field. Expanded from 2-state to 3-state later
    // when we walk the parent chain to distinguish "set by this prototype" from
    // "set by an ancestor."
    private enum InheritanceState { Default, Set }

    private static readonly Color SetColor = Color.FromHex("#6fa8dc");
    private static readonly Color DefaultColor = Color.FromHex("#555555");

    // Alternating row backgrounds — subtle but enough that adjacent fields
    // read as distinct lines instead of a wall of controls.
    private static readonly Color RowColorEven = Color.FromHex("#2b2b2b");
    private static readonly Color RowColorOdd = Color.FromHex("#333333");

    private Control BuildFieldRow(
        ComponentDataFieldReader.DataFieldView field,
        MappingDataNode mapping,
        MappingDataNode? parentMapping,
        int rowIndex)
    {
        // A key in the child's merged mapping is a real override only if it
        // differs from the parent's value for the same key. Robust's
        // [AlwaysPushInheritance] means the parent's mapping has already been
        // merged into the child; we can't just trust `mapping.Has(...)`.
        var isInMapping = mapping.Has(field.YamlName);
        DataNode? parentValue = null;
        if (parentMapping != null && parentMapping.Has(field.YamlName))
            parentValue = parentMapping[field.YamlName];

        var isTrueOverride = isInMapping
            && (parentValue == null || !NodesEqual(mapping[field.YamlName], parentValue));

        var state = isTrueOverride ? InheritanceState.Set : InheritanceState.Default;

        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8,
            VerticalAlignment = VAlignment.Center,
        };

        // Inheritance dot. Tooltips give the user the long explanation without
        // chewing up visual space.
        var dot = new Label
        {
            Text = "●",
            MinWidth = 14,
            Modulate = state == InheritanceState.Set ? SetColor : DefaultColor,
            ToolTip = state == InheritanceState.Set
                ? "Set by this prototype (or an ancestor)"
                : "Inherits the component's default value",
        };
        row.AddChild(dot);

        var name = new Label
        {
            Text = field.YamlName,
            MinWidth = 200,
            ClipText = true,
            Modulate = state == InheritanceState.Set ? Color.White : DefaultColor,
            StyleClasses = { "monospace" },
        };
        row.AddChild(name);

        Control editor;
        try
        {
            // Prefer the parent's YAML node as the "inherited" reference — it
            // carries the original shape (e.g. SequenceDataNode for flags) and
            // casing, so byte-compare works when the user edits back to it.
            // Fall back to serializing the CLR value when we have no parent
            // mapping (e.g. root prototypes, or fields a parent didn't set).
            DataNode? initialInheritedNode = parentValue;
            if (initialInheritedNode == null && field.CurrentValue != null)
            {
                try { initialInheritedNode = _serMan.WriteValue(field.MemberType, field.CurrentValue); }
                catch { /* fall back to null */ }
            }

            var node = isTrueOverride ? mapping[field.YamlName] : initialInheritedNode;

            editor = _fieldEditors.Build(field.MemberType, node, newNode =>
            {
                var revertsToInherited = NodesEqual(newNode, initialInheritedNode);

                if (revertsToInherited || newNode == null)
                    mapping.Remove(field.YamlName);
                else
                    mapping[field.YamlName] = newNode;

                SetRowState(dot, name, revertsToInherited ? InheritanceState.Default : InheritanceState.Set);
                RenderYaml(_selectedProto!);
            });
        }
        catch (Exception ex)
        {
            editor = new Label
            {
                Text = $"<serialize failed: {ex.Message}>",
                Modulate = Color.OrangeRed,
            };
        }

        editor.HorizontalExpand = true;
        row.AddChild(editor);

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

    private static void SetRowState(Label dot, Label name, InheritanceState state)
    {
        var isSet = state == InheritanceState.Set;
        dot.Modulate = isSet ? SetColor : DefaultColor;
        dot.ToolTip = isSet
            ? "Set by this prototype (or an ancestor)"
            : "Inherits the component's default value";
        name.Modulate = isSet ? Color.White : DefaultColor;
    }

    // Compose the effective inherited mapping for a component: the union of
    // every direct parent's already-merged ComponentRegistry entry for this
    // component name. Since Robust sets [AlwaysPushInheritance] on Components,
    // each parent's entry is already merged with its own ancestors, so we
    // don't need to walk further up the chain ourselves.
    //
    // When multiple parents overlap on a key, the last one wins — mirroring
    // Robust's own merge direction (later parents override earlier ones).
    private MappingDataNode? BuildParentComponentMapping(EntityPrototype entity, string componentName)
    {
        if (entity.Parents == null || entity.Parents.Length == 0)
            return null;

        MappingDataNode? result = null;
        foreach (var parentId in entity.Parents)
        {
            if (!_protos.TryIndex<EntityPrototype>(parentId, out var parent))
                continue;
            if (!parent.Components.TryGetValue(componentName, out var entry))
                continue;

            result ??= new MappingDataNode();
            foreach (var kv in entry.Mapping)
                result[kv.Key] = kv.Value;
        }

        return result;
    }

    private static bool NodesEqual(DataNode? a, DataNode? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.ToString() == b.ToString();
    }

    private void RenderYaml(IPrototype proto)
    {
        try
        {
            // EntityPrototype gets synthesized from its in-memory ComponentRegistry
            // mappings so that edits land in the YAML view immediately. The serializer
            // path would round-trip through the component instance, which we don't
            // mutate on edit, so it would show stale values.
            DataNode node = proto is EntityPrototype entity
                ? BuildEntityYaml(entity)
                : _serMan.WriteValue(proto.GetType(), proto);
            _yamlBody.Text = node.ToString();
        }
        catch (Exception ex)
        {
            _yamlBody.Text = $"<failed to serialize prototype>\n{ex.Message}";
        }
    }

    private static MappingDataNode BuildEntityYaml(EntityPrototype entity)
    {
        var doc = new MappingDataNode();
        doc.Add("type", new ValueDataNode("entity"));
        doc.Add("id", new ValueDataNode(entity.ID));

        if (entity.Parents is { Length: > 0 } parents)
        {
            if (parents.Length == 1)
            {
                doc.Add("parent", new ValueDataNode(parents[0]));
            }
            else
            {
                var seq = new SequenceDataNode();
                foreach (var p in parents)
                    seq.Add(new ValueDataNode(p));
                doc.Add("parent", seq);
            }
        }

        if (entity.Abstract)
            doc.Add("abstract", new ValueDataNode("true"));

        if (!string.IsNullOrEmpty(entity.SetName))
            doc.Add("name", new ValueDataNode(entity.SetName));

        if (!string.IsNullOrEmpty(entity.SetDesc))
            doc.Add("description", new ValueDataNode(entity.SetDesc));

        if (entity.Components.Count > 0)
        {
            var components = new SequenceDataNode();
            foreach (var (type, entry) in entity.Components.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var compMap = new MappingDataNode();
                compMap.Add("type", new ValueDataNode(type));
                foreach (var kv in entry.Mapping)
                    compMap.Add(kv.Key, kv.Value);
                components.Add(compMap);
            }
            doc.Add("components", components);
        }

        return doc;
    }
}
