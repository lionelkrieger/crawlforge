using System.Reflection;
using CrawlForge.Contracts.Common;

namespace CrawlForge.Contracts.Tests;

/// <summary>
/// Shared reflection helpers over the locked CrawlForge.Contracts assembly.
/// </summary>
internal static class ContractAssembly
{
    /// <summary>The contracts assembly under test.</summary>
    public static Assembly Assembly { get; } = typeof(ApiEnvelope<>).Assembly;

    /// <summary>All public types exported by the contracts assembly.</summary>
    public static Type[] PublicTypes { get; } = Assembly.GetExportedTypes();

    /// <summary>All entity DTO types (schema catalog objects).</summary>
    public static Type[] EntityTypes { get; } = PublicTypes
        .Where(static t => t.Namespace == "CrawlForge.Contracts.Entities")
        .ToArray();

    /// <summary>Tests whether a type is a C# record (has the compiler clone method).</summary>
    public static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;

    /// <summary>Tests whether a property has an init-only setter.</summary>
    public static bool IsInitOnly(PropertyInfo property)
    {
        MethodInfo? setter = property.SetMethod;
        if (setter is null)
        {
            return false;
        }

        Type[] modifiers = setter.ReturnParameter.GetRequiredCustomModifiers();
        return modifiers.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));
    }
}
