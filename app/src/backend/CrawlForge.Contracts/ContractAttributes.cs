namespace CrawlForge.Contracts;

/// <summary>
/// Marks a contract member that was added by an extension specification
/// (FS-CRAWL-002) on top of the FS-CRAWL-001 baseline. Additive members must
/// remain optional (nullable) forever: parent members are never renamed or
/// repurposed, and new capabilities arrive only as new optional members
/// (additive-only contract evolution, precedence rule 3).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class AdditiveMemberAttribute : Attribute
{
}
