namespace Fixture.E9;

/// <summary>Generation writes local files only; probing crawl targets happens in the crawler.</summary>
public sealed class LlmsTxtGenerator
{
    public void WriteLocal(string outputDirectory, string content) =>
        File.WriteAllText(Path.Combine(outputDirectory, "llms.txt"), content);
}
