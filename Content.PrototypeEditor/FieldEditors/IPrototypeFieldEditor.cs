using Robust.Client.UserInterface;
using Robust.Shared.Serialization.Markdown;

namespace Content.PrototypeEditor.FieldEditors;

/// <summary>
/// Produces an editor control for a single prototype field.
/// Editors speak in <see cref="DataNode"/>s rather than CLR values so that fields
/// with custom <c>ITypeSerializer</c>s don't bypass the editor pipeline.
/// </summary>
public interface IPrototypeFieldEditor
{
    /// <summary>
    /// Build an editor control bound to <paramref name="current"/>.
    /// When the user edits the field, <paramref name="onChanged"/> is invoked with
    /// the new node (or <c>null</c> to indicate "remove this field / fall back to parent/default").
    /// </summary>
    Control Build(Type fieldType, DataNode? current, Action<DataNode?> onChanged);
}
