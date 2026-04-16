using System.Linq;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Dropdown editor for <see cref="ProtoId{T}"/>, <see cref="EntProtoId"/>, and
/// <see cref="EntProtoId{T}"/>. Populates the list from
/// <c>IPrototypeManager.EnumeratePrototypes(kind)</c> so edits are always
/// restricted to IDs the game actually knows about.
/// </summary>
/// <remarks>
/// <see cref="EntProtoId{T}"/> carries a "must have component T" constraint; we
/// honor it by filtering the entity prototype list to only those with the
/// component registered. Plain <see cref="EntProtoId"/> still shows every
/// entity — there's no type-level hint to filter by.
/// </remarks>
public sealed class ProtoIdFieldEditor : IPrototypeFieldEditor
{
    private readonly IPrototypeManager _protos;
    private readonly IComponentFactory _compFactory;

    public ProtoIdFieldEditor(IPrototypeManager protos, IComponentFactory compFactory)
    {
        _protos = protos;
        _compFactory = compFactory;
    }

    public static bool IsProtoId(Type t) => TryGetProtoKind(t, out _, out _);

    private static bool TryGetProtoKind(Type t, out Type protoKind, out Type? componentFilter)
    {
        protoKind = typeof(IPrototype);
        componentFilter = null;

        if (t == typeof(EntProtoId))
        {
            protoKind = typeof(EntityPrototype);
            return true;
        }

        if (!t.IsGenericType)
            return false;

        var def = t.GetGenericTypeDefinition();
        if (def == typeof(ProtoId<>))
        {
            protoKind = t.GetGenericArguments()[0];
            return true;
        }

        if (def == typeof(EntProtoId<>))
        {
            protoKind = typeof(EntityPrototype);
            componentFilter = t.GetGenericArguments()[0];
            return true;
        }

        return false;
    }

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
    {
        if (!TryGetProtoKind(fieldType, out var protoKind, out var componentFilter))
        {
            return new Label
            {
                Text = $"<not a proto id: {fieldType.Name}>",
                Modulate = Color.OrangeRed,
            };
        }

        var currentId = (current as ValueDataNode)?.Value ?? string.Empty;

        IEnumerable<IPrototype> candidates = _protos.EnumeratePrototypes(protoKind);

        // EntProtoId<TComponent> narrows the list to entity prototypes whose
        // ComponentRegistry includes TComponent — mirrors the runtime guarantee
        // the generic form exists to express.
        if (componentFilter != null)
        {
            var compName = _compFactory.GetComponentName(componentFilter);
            candidates = candidates
                .OfType<EntityPrototype>()
                .Where(e => e.Components.ContainsKey(compName));
        }

        var ids = candidates
            .Select(p => p.ID)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dropdown = new OptionButton { HorizontalExpand = true };

        // ID 0 is "(none)" — maps to onChanged(null), which at the component-
        // field level drops the override and reverts to inherited.
        dropdown.AddItem("(none)", 0);

        var selected = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            var optionId = i + 1;
            dropdown.AddItem(ids[i], optionId);
            if (string.Equals(ids[i], currentId, StringComparison.Ordinal))
                selected = optionId;
        }

        // Inherited/saved value refers to a prototype that no longer exists.
        // Surface it verbatim so the user can see what's there before picking
        // a replacement — silently dropping it would hide breakage.
        var staleOptionId = -1;
        if (!string.IsNullOrEmpty(currentId) && selected == 0)
        {
            staleOptionId = ids.Count + 1;
            dropdown.AddItem($"{currentId} (not found)", staleOptionId);
            selected = staleOptionId;
        }

        dropdown.SelectId(selected);

        dropdown.OnItemSelected += args =>
        {
            dropdown.SelectId(args.Id);

            if (args.Id == 0)
            {
                onChanged(null);
                return;
            }

            if (args.Id == staleOptionId)
            {
                onChanged(new ValueDataNode(currentId));
                return;
            }

            var idx = args.Id - 1;
            if (idx < 0 || idx >= ids.Count)
                return;

            onChanged(new ValueDataNode(ids[idx]));
        };

        return dropdown;
    }
}
