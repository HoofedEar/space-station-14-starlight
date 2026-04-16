using Robust.Client.UserInterface;
using Robust.Shared.Serialization.Markdown;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Dispatches field-type → <see cref="IPrototypeFieldEditor"/>. Resolution order:
///   1. Exact-type registrations
///   2. Assignable-from registrations (first match wins)
///   3. Predicate registrations (first match wins)
///   4. The fallback editor (always available)
/// </summary>
public sealed class FieldEditorRegistry
{
    private readonly Dictionary<Type, IPrototypeFieldEditor> _exact = new();
    private readonly List<(Type Base, IPrototypeFieldEditor Editor)> _assignable = new();
    private readonly List<(Func<Type, bool> Match, IPrototypeFieldEditor Editor)> _predicates = new();
    private readonly IPrototypeFieldEditor _fallback;

    public FieldEditorRegistry(IPrototypeFieldEditor fallback)
    {
        _fallback = fallback;
    }

    public FieldEditorRegistry RegisterExact(Type type, IPrototypeFieldEditor editor)
    {
        _exact[type] = editor;
        return this;
    }

    public FieldEditorRegistry RegisterExact<T>(IPrototypeFieldEditor editor) => RegisterExact(typeof(T), editor);

    public FieldEditorRegistry RegisterAssignable<TBase>(IPrototypeFieldEditor editor)
    {
        _assignable.Add((typeof(TBase), editor));
        return this;
    }

    public FieldEditorRegistry RegisterPredicate(Func<Type, bool> match, IPrototypeFieldEditor editor)
    {
        _predicates.Add((match, editor));
        return this;
    }

    public IPrototypeFieldEditor Resolve(Type fieldType)
    {
        if (_exact.TryGetValue(fieldType, out var exact))
            return exact;

        foreach (var (baseType, editor) in _assignable)
        {
            if (baseType.IsAssignableFrom(fieldType))
                return editor;
        }

        foreach (var (match, editor) in _predicates)
        {
            if (match(fieldType))
                return editor;
        }

        return _fallback;
    }

    public Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged)
        => Resolve(fieldType).Build(fieldType, current, onChanged);
}
