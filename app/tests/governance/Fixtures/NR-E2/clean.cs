namespace Fixture.E2;

/// <summary>Bundled assets only; user additions are local. No runtime fetch.</summary>
public sealed class BundledSignatureCatalog
{
    public string LoadSignatures(string assetsRoot) =>
        File.ReadAllText(Path.Combine(assetsRoot, "waf-signatures", "signatures.json"));

    public string LoadStopWords(string assetsRoot, string language) =>
        File.ReadAllText(Path.Combine(assetsRoot, "stopwords", $"{language}.txt"));
}
