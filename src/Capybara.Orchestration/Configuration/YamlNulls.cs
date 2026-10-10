using System.Collections;
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// YAML <c>~</c>, <c>null</c> or an empty value deserializes as null even where a config model declares a non-nullable
/// type (<c>tools: ~</c>, <c>grants: [~]</c>), and the loader would dereference it. This finds such nulls by the models'
/// nullable annotations, so a field added later is covered without another check. Collection entries are never
/// nullable, including the entries of a top-level list.
/// </summary>
/// <remarks>
/// An explicit null is an error rather than a synonym for an absent key: the author wrote the key, and for protected
/// fields such as <c>grants</c> or <c>trifecta</c> silently reading it as empty would change what they meant.
/// </remarks>
internal static class YamlNulls
{
    /// <summary>Paths of disallowed nulls in <paramref name="model"/> (camelCase, as written in YAML), in document order.</summary>
    public static IReadOnlyList<string> Find(object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var found = new List<string>();

        // NullabilityInfoContext is not thread-safe; config loads are rare, so one per call.
        Visit(model, null, string.Empty, new NullabilityInfoContext(), found);
        return found;
    }

    /// <summary><paramref name="info"/> is the declaring property's nullability, or null for a collection entry, which must never be null.</summary>
    private static void Visit(object? value, NullabilityInfo? info, string path, NullabilityInfoContext context, List<string> found)
    {
        if (value is null)
        {
            if (info is null || info.ReadState == NullabilityState.NotNull)
            {
                found.Add(path);
            }

            return;
        }

        switch (value)
        {
            case string:
                return;
            case IDictionary map:
                foreach (DictionaryEntry entry in map)
                {
                    Visit(entry.Value, null, $"{path}.{entry.Key}", context, found);
                }

                return;
            case IList list:
                for (var i = 0; i < list.Count; i++)
                {
                    Visit(list[i], null, $"{path}[{i}]", context, found);
                }

                return;
        }

        if (value.GetType().Assembly != typeof(YamlNulls).Assembly)
        {
            return;
        }

        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var name = property.GetCustomAttribute<YamlMemberAttribute>()?.Alias ?? CamelCaseNamingConvention.Instance.Apply(property.Name);
            Visit(property.GetValue(value), context.Create(property), path.Length == 0 ? name : $"{path}.{name}", context, found);
        }
    }
}
