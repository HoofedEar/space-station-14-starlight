using System.Globalization;
using System.Linq;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Audio;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Editor for <see cref="SoundSpecifier"/> fields. Toggles between
/// <see cref="SoundPathSpecifier"/> (audio file path) and
/// <see cref="SoundCollectionSpecifier"/> (pick from a
/// <see cref="SoundCollectionPrototype"/>), plus a shared params block for
/// volume/pitch/variation/loop. The polymorphic type is encoded by which
/// key is present (<c>path</c> vs <c>collection</c>) — matches Robust's
/// <c>SoundSpecifierTypeSerializer</c>, so no <c>!type:</c> tag is needed.
/// </summary>
/// <remarks>
/// AudioParams has eight fields; this editor exposes the four most commonly
/// tuned (volume, pitch, variation, loop). Any other params from the inherited
/// YAML (maxDistance, rolloffFactor, referenceDistance, playOffsetSeconds)
/// are preserved verbatim across edits so they don't get silently dropped.
/// </remarks>
public sealed class SoundSpecifierFieldEditor : IPrototypeFieldEditor
{
    private readonly IPrototypeManager _protos;

    private static readonly Color PanelColor = Color.FromHex("#242424");
    private static readonly Color BorderColor = Color.FromHex("#1a1a1a");
    private static readonly Color SectionLabelColor = Color.FromHex("#a0a0a0");

    private const int ModeUnset = 0;
    private const int ModePath = 1;
    private const int ModeCollection = 2;

    private static readonly string[] KnownParamKeys = { "volume", "pitch", "variation", "loop" };

    public SoundSpecifierFieldEditor(IPrototypeManager protos) => _protos = protos;

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        Parse(current, out var initialMode, out var initialPath, out var initialCollection,
            out var initialParams, out var preservedExtras);

        var container = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 4,
        };

        // --- Mode dropdown ---
        var typeRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6,
            VerticalAlignment = Control.VAlignment.Center,
        };
        typeRow.AddChild(new Label { Text = "type", MinWidth = 80, StyleClasses = { "monospace" } });

        var typeDropdown = new OptionButton { HorizontalExpand = true };
        typeDropdown.AddItem("(unset)", ModeUnset);
        typeDropdown.AddItem("Path", ModePath);
        typeDropdown.AddItem("Collection", ModeCollection);
        typeDropdown.SelectId(initialMode);
        typeRow.AddChild(typeDropdown);

        // --- Path row ---
        var pathRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6,
            VerticalAlignment = Control.VAlignment.Center,
        };
        pathRow.AddChild(new Label { Text = "path", MinWidth = 80, StyleClasses = { "monospace" } });
        var pathEdit = new LineEdit
        {
            HorizontalExpand = true,
            Text = initialPath,
            PlaceHolder = "/Audio/.../something.ogg",
        };
        pathRow.AddChild(pathEdit);

        // --- Collection row ---
        var collectionRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6,
            VerticalAlignment = Control.VAlignment.Center,
        };
        collectionRow.AddChild(new Label { Text = "collection", MinWidth = 80, StyleClasses = { "monospace" } });
        var (collectionDropdown, collectionIds, staleCollectionId) = BuildCollectionDropdown(initialCollection);
        collectionRow.AddChild(collectionDropdown);

        // --- Params panel ---
        var paramsPanel = BuildParamsPanel(
            initialParams,
            out var volumeEdit,
            out var pitchEdit,
            out var variationEdit,
            out var loopCheck);

        container.AddChild(typeRow);
        container.AddChild(pathRow);
        container.AddChild(collectionRow);
        container.AddChild(paramsPanel);

        void ApplyVisibility(int mode)
        {
            pathRow.Visible = mode == ModePath;
            collectionRow.Visible = mode == ModeCollection;
            paramsPanel.Visible = mode != ModeUnset;
        }

        ApplyVisibility(initialMode);

        string GetSelectedCollection()
        {
            var id = collectionDropdown.SelectedId;
            if (id == 0) return string.Empty;
            if (id == staleCollectionId) return initialCollection;
            var idx = id - 1;
            if (idx < 0 || idx >= collectionIds.Count) return string.Empty;
            return collectionIds[idx];
        }

        void Emit()
        {
            var mode = typeDropdown.SelectedId;
            if (mode == ModeUnset)
            {
                onChanged(null);
                return;
            }

            var result = new MappingDataNode();

            if (mode == ModePath)
            {
                var path = pathEdit.Text.Trim();
                if (string.IsNullOrEmpty(path))
                {
                    onChanged(null);
                    return;
                }
                result["path"] = new ValueDataNode(path);
            }
            else
            {
                var collection = GetSelectedCollection();
                if (string.IsNullOrEmpty(collection))
                {
                    onChanged(null);
                    return;
                }
                result["collection"] = new ValueDataNode(collection);
            }

            var paramsNode = BuildParamsNode(volumeEdit, pitchEdit, variationEdit, loopCheck, preservedExtras);
            if (paramsNode != null)
                result["params"] = paramsNode;

            onChanged(result);
        }

        typeDropdown.OnItemSelected += args =>
        {
            typeDropdown.SelectId(args.Id);
            ApplyVisibility(args.Id);
            Emit();
        };
        pathEdit.OnTextChanged += _ => Emit();
        collectionDropdown.OnItemSelected += args =>
        {
            collectionDropdown.SelectId(args.Id);
            Emit();
        };
        volumeEdit.OnTextChanged += _ => Emit();
        pitchEdit.OnTextChanged += _ => Emit();
        variationEdit.OnTextChanged += _ => Emit();
        loopCheck.OnToggled += _ => Emit();

        return container;
    }

    // --- Parsing ---

    private static void Parse(
        DataNode? node,
        out int mode,
        out string path,
        out string collection,
        out MappingDataNode? paramsNode,
        out MappingDataNode preservedExtras)
    {
        mode = ModeUnset;
        path = string.Empty;
        collection = string.Empty;
        paramsNode = null;
        preservedExtras = new MappingDataNode();

        switch (node)
        {
            case null:
                return;

            // Bare string shorthand: treat as path.
            case ValueDataNode value when !string.IsNullOrEmpty(value.Value):
                mode = ModePath;
                path = value.Value;
                return;

            case MappingDataNode map:
                if (map.Has("path") && map["path"] is ValueDataNode pv)
                {
                    mode = ModePath;
                    path = pv.Value;
                }
                else if (map.Has("collection") && map["collection"] is ValueDataNode cv)
                {
                    mode = ModeCollection;
                    collection = cv.Value;
                }

                if (map.Has("params") && map["params"] is MappingDataNode pm)
                {
                    paramsNode = pm;
                    foreach (var kv in pm)
                    {
                        if (!KnownParamKeys.Contains(kv.Key))
                            preservedExtras[kv.Key] = kv.Value;
                    }
                }
                return;
        }
    }

    // --- Collection dropdown ---

    private (OptionButton Dropdown, List<string> Ids, int StaleOptionId) BuildCollectionDropdown(string initial)
    {
        var ids = _protos.EnumeratePrototypes(typeof(SoundCollectionPrototype))
            .Select(p => p.ID)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dropdown = new OptionButton { HorizontalExpand = true };
        dropdown.AddItem("(pick)", 0);

        var selected = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            var optId = i + 1;
            dropdown.AddItem(ids[i], optId);
            if (string.Equals(ids[i], initial, StringComparison.Ordinal))
                selected = optId;
        }

        var staleOptionId = -1;
        if (!string.IsNullOrEmpty(initial) && selected == 0)
        {
            staleOptionId = ids.Count + 1;
            dropdown.AddItem($"{initial} (not found)", staleOptionId);
            selected = staleOptionId;
        }

        dropdown.SelectId(selected);
        return (dropdown, ids, staleOptionId);
    }

    // --- Params panel ---

    private Control BuildParamsPanel(
        MappingDataNode? source,
        out LineEdit volumeEdit,
        out LineEdit pitchEdit,
        out LineEdit variationEdit,
        out CheckBox loopCheck)
    {
        var initialVolume = ReadValueString(source, "volume");
        var initialPitch = ReadValueString(source, "pitch");
        var initialVariation = ReadValueString(source, "variation");
        var initialLoop = string.Equals(ReadValueString(source, "loop"), "true",
            StringComparison.OrdinalIgnoreCase);

        var rows = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 2,
        };

        rows.AddChild(new Label
        {
            Text = "params",
            StyleClasses = { "monospace" },
            Modulate = SectionLabelColor,
        });

        volumeEdit = AddParamRow(rows, "volume", initialVolume, "e.g. -4");
        pitchEdit = AddParamRow(rows, "pitch", initialPitch, "e.g. 1.0");
        variationEdit = AddParamRow(rows, "variation", initialVariation, "e.g. 0.25");

        var loopRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6,
            VerticalAlignment = Control.VAlignment.Center,
        };
        loopRow.AddChild(new Label { Text = "loop", MinWidth = 80, StyleClasses = { "monospace" } });
        loopCheck = new CheckBox { Pressed = initialLoop };
        loopRow.AddChild(loopCheck);
        rows.AddChild(loopRow);

        var panel = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = PanelColor,
                BorderColor = BorderColor,
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = 6,
                ContentMarginRightOverride = 6,
                ContentMarginTopOverride = 4,
                ContentMarginBottomOverride = 4,
            },
        };
        panel.AddChild(rows);
        return panel;
    }

    private static LineEdit AddParamRow(BoxContainer parent, string key, string initial, string placeholder)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6,
            VerticalAlignment = Control.VAlignment.Center,
        };
        row.AddChild(new Label { Text = key, MinWidth = 80, StyleClasses = { "monospace" } });
        var edit = new LineEdit
        {
            HorizontalExpand = true,
            Text = initial,
            PlaceHolder = placeholder,
        };
        row.AddChild(edit);
        parent.AddChild(row);
        return edit;
    }

    private static string ReadValueString(MappingDataNode? source, string key)
    {
        if (source == null || !source.Has(key))
            return string.Empty;
        return source[key] is ValueDataNode v ? v.Value : string.Empty;
    }

    private static MappingDataNode? BuildParamsNode(
        LineEdit volumeEdit,
        LineEdit pitchEdit,
        LineEdit variationEdit,
        CheckBox loopCheck,
        MappingDataNode preservedExtras)
    {
        var result = new MappingDataNode();

        TryAddNumeric(result, "volume", volumeEdit.Text);
        TryAddNumeric(result, "pitch", pitchEdit.Text);
        TryAddNumeric(result, "variation", variationEdit.Text);

        // Only emit loop when true — loop: false is the AudioParams default,
        // so emitting it would produce noise in round-tripped YAML.
        if (loopCheck.Pressed)
            result["loop"] = new ValueDataNode("true");

        foreach (var kv in preservedExtras)
            result[kv.Key] = kv.Value;

        return result.Count > 0 ? result : null;
    }

    private static void TryAddNumeric(MappingDataNode target, string key, string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;
        if (!double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return;
        target[key] = new ValueDataNode(trimmed);
    }
}
