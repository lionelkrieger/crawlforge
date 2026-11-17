using System.Reflection;

namespace CrawlForge.Data.Repositories.Tests.Harness;

/// <summary>
/// Reflection-based sample factory covering every catalog DTO. Values are
/// deterministic; foreign-key parameters are bound to the seeded base chain
/// (project 1, profile 1, configuration 1, audit 1, urls 1–2, schedule 1,
/// log import 1) so samples satisfy referential integrity on a fresh database.
/// </summary>
public static class SampleFactory
{
    /// <summary>Deterministic base timestamp used by all samples (UTC).</summary>
    public static readonly DateTimeOffset BaseTime = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, long> ForeignKeyIds = new Dictionary<string, long>
    {
        ["ProjectId"] = 1,
        ["ConfigurationId"] = 1,
        ["AuditId"] = 1,
        ["UrlId"] = 1,
        ["SourceUrlId"] = 1,
        ["TargetUrlId"] = 2,
        ["ScheduleDefinitionId"] = 1,
        ["LogImportId"] = 1,
        ["ContentScopeProfileId"] = 1
    };

    /// <summary>Creates a fully populated sample of the given DTO record type.</summary>
    /// <param name="dtoType">The DTO record type.</param>
    /// <returns>A sample instance with identifier 0 and valid base-chain references.</returns>
    public static object Create(Type dtoType)
    {
        var constructor = dtoType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = constructor.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            args[i] = GenerateValue(parameters[i].ParameterType, parameters[i].Name!);
        }

        var instance = constructor.Invoke(args);

        // Populate additive init-only members that are not constructor parameters.
        var parameterNames = new HashSet<string>(parameters.Select(p => p.Name!), StringComparer.OrdinalIgnoreCase);
        foreach (var property in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || parameterNames.Contains(property.Name))
            {
                continue;
            }

            property.SetValue(instance, GenerateValue(property.PropertyType, property.Name));
        }

        return instance;
    }

    /// <summary>Creates a typed sample of the given DTO record type.</summary>
    /// <typeparam name="TDto">The DTO record type.</typeparam>
    /// <returns>A sample instance with identifier 0 and valid base-chain references.</returns>
    public static TDto Create<TDto>() where TDto : class => (TDto)Create(typeof(TDto));

    private static object? GenerateValue(Type type, string name)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return GenerateValue(underlying, name);
        }

        if (type == typeof(long))
        {
            if (string.Equals(name, "Id", StringComparison.OrdinalIgnoreCase))
            {
                return 0L;
            }

            return ForeignKeyIds.TryGetValue(name, out var reference) ? reference : 42L;
        }

        if (type == typeof(int))
        {
            return 3;
        }

        if (type == typeof(double))
        {
            return 0.75;
        }

        if (type == typeof(bool))
        {
            // Deletion/suppression markers must default to the live, unsuppressed
            // state so seeded base chains and scenario fixtures stay visible.
            return !string.Equals(name, "IsDeleted", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "IsSuppressed", StringComparison.OrdinalIgnoreCase);
        }

        if (type == typeof(DateTimeOffset))
        {
            return BaseTime;
        }

        if (type == typeof(string))
        {
            if (name.EndsWith("Json", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"sample\":true}";
            }

            if (name.Contains("Url", StringComparison.OrdinalIgnoreCase))
            {
                return "https://example.test/" + name.ToLowerInvariant();
            }

            return "sample-" + name;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).GetValue(0)!;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IList<>) ||
                definition == typeof(IEnumerable<>) || definition == typeof(List<>))
            {
                var elementType = type.GetGenericArguments()[0];
                var listType = typeof(List<>).MakeGenericType(elementType);
                var list = Activator.CreateInstance(listType)!;
                var add = listType.GetMethod("Add")!;
                add.Invoke(list, [GenerateValue(elementType, name + "Item")]);
                return list;
            }
        }

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var array = Array.CreateInstance(elementType, 1);
            array.SetValue(GenerateValue(elementType, name + "Item"), 0);
            return array;
        }

        if (type.IsClass && type != typeof(object))
        {
            return Create(type);
        }

        throw new NotSupportedException($"No sample generator for {type} ({name}).");
    }
}
