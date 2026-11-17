namespace Fixture.Nr07;

/// <summary>All reports render offline from local aggregates; no viewing authorization.</summary>
public sealed class LocalReportAccess
{
    public bool CanView(string reportId) => true;

    public string LoadAggregate(string reportId) => File.ReadAllText(Path.Combine("reports", reportId));
}
