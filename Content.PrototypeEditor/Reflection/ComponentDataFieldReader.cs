using System.Linq;
using System.Reflection;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Definition;

namespace Content.PrototypeEditor.Reflection;

/// <summary>
/// Reads data-field members off a component (or any DataDefinition-compatible type)
/// via plain CLR reflection.
/// <para>
/// We go through reflection rather than <c>ISerializationManager.GetDefinition</c>
/// because that API is <c>internal</c> to RobustToolbox. <see cref="DataFieldBaseAttribute"/>
/// and <see cref="DataDefinitionUtility.AutoGenerateTag"/> are both public, so we can
/// reproduce the name-resolution rule exactly.
/// </para>
/// </summary>
public static class ComponentDataFieldReader
{
    public readonly record struct DataFieldView(
        string YamlName,
        Type MemberType,
        object? CurrentValue,
        bool IsReadable,
        bool IsWritable);

    private const BindingFlags MemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<DataFieldView> GetFields(object instance)
    {
        var type = instance.GetType();

        foreach (var prop in type.GetProperties(MemberFlags))
        {
            var attr = prop.GetCustomAttribute<DataFieldBaseAttribute>();
            if (attr == null)
                continue;

            yield return new DataFieldView(
                ResolveTag(attr, prop.Name),
                prop.PropertyType,
                SafeRead(() => prop.GetValue(instance)),
                IsReadable: prop.CanRead,
                IsWritable: prop.CanWrite);
        }

        foreach (var field in type.GetFields(MemberFlags))
        {
            // Skip compiler-generated backing fields for auto-properties; the
            // property iteration above already covered those.
            if (field.Name.Contains('<'))
                continue;

            var attr = field.GetCustomAttribute<DataFieldBaseAttribute>();
            if (attr == null)
                continue;

            yield return new DataFieldView(
                ResolveTag(attr, field.Name),
                field.FieldType,
                SafeRead(() => field.GetValue(instance)),
                IsReadable: true,
                IsWritable: !field.IsInitOnly);
        }
    }

    private static string ResolveTag(DataFieldBaseAttribute attr, string memberName)
    {
        // DataFieldAttribute.Tag is null until the DataDefinition builder fills it
        // in; since we can't run that builder, resolve the tag ourselves using the
        // same rule (camelCase from member name) for consistency.
        if (attr is DataFieldAttribute df && !string.IsNullOrEmpty(df.Tag))
            return df.Tag;

        return DataDefinitionUtility.AutoGenerateTag(memberName);
    }

    private static object? SafeRead(Func<object?> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
