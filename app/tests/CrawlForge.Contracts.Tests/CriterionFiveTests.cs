using System.Reflection;
using Xunit;

namespace CrawlForge.Contracts.Tests;

/// <summary>
/// Acceptance criterion #5: zero license/account/entitlement/identity members
/// anywhere in the shared contract surface. Scans every public type and member
/// name (including enum values and parameters) for forbidden concept tokens.
/// </summary>
public class CriterionFiveTests
{
    private static readonly string[] ForbiddenTokens =
    {
        "license", "licence", "entitlement", "account", "identity",
        "telemetry", "subscription", "trial", "seat", "activation",
        "signin", "signup", "password", "enrollment", "machineid",
    };

    [Fact]
    public void NoPublicTypeOrMemberNameCarriesForbiddenConcepts()
    {
        List<string> violations = new();
        foreach (Type type in ContractAssembly.PublicTypes)
        {
            CheckName(violations, "type", type.FullName ?? type.Name);

            const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (PropertyInfo property in type.GetProperties(Declared))
            {
                CheckName(violations, "property", type.Name + "." + property.Name);
            }

            foreach (MethodInfo method in type.GetMethods(Declared))
            {
                CheckName(violations, "method", type.Name + "." + method.Name);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    CheckName(violations, "parameter", type.Name + "." + method.Name + "(" + parameter.Name + ")");
                }
            }

            foreach (FieldInfo field in type.GetFields(Declared))
            {
                CheckName(violations, "field", type.Name + "." + field.Name);
            }

            foreach (EventInfo eventInfo in type.GetEvents(Declared))
            {
                CheckName(violations, "event", type.Name + "." + eventInfo.Name);
            }

            foreach (ConstructorInfo constructor in type.GetConstructors(Declared))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    CheckName(violations, "ctor-parameter", type.Name + "(" + parameter.Name + ")");
                }
            }
        }

        Assert.True(violations.Count == 0, "Criterion #5 violations: " + string.Join("; ", violations));
    }

    [Fact]
    public void HostLocalInfrastructureIsNotRedefinedInContracts()
    {
        // IHostModule / IBackendLifecycle are Host-local infrastructure and must
        // stay outside the shared contract surface (orchestrator note 3).
        Type[] hostLocal = ContractAssembly.PublicTypes
            .Where(static t => t.Name is "IHostModule" or "IBackendLifecycle")
            .ToArray();
        Assert.Empty(hostLocal);
    }

    [Fact]
    public void ContractsAssemblyReferencesOnlyBclAssemblies()
    {
        // Pure BCL mandate: zero NuGet packages in CrawlForge.Contracts.
        AssemblyName[] references = ContractAssembly.Assembly.GetReferencedAssemblies();
        List<string> nonBcl = new();
        foreach (AssemblyName reference in references)
        {
            string name = reference.Name ?? string.Empty;
            bool isBcl = name.StartsWith("System", StringComparison.Ordinal)
                || name == "netstandard"
                || name == "mscorlib";
            if (!isBcl)
            {
                nonBcl.Add(name);
            }
        }

        Assert.True(nonBcl.Count == 0, "Non-BCL references: " + string.Join("; ", nonBcl));
    }

    private static void CheckName(List<string> violations, string kind, string name)
    {
        foreach (string token in ForbiddenTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(kind + " '" + name + "' contains forbidden token '" + token + "'");
            }
        }
    }
}
