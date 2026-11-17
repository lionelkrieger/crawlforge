using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace CrawlForge.Export.Writers;

/// <summary>
/// Offline font resolver for the PDF summary (PdfSharp 6.x core ships no
/// platform resolver). Loads TrueType fonts from the LOCAL system fonts
/// directory only — no network, no vendor service, no bundled font assets
/// required. Arial variants are preferred; any installed TrueType font is the
/// fallback so the summary renders on minimal installations.
/// </summary>
public sealed class LocalFontResolver : IFontResolver
{
    private static readonly Lazy<LocalFontResolver> Instance = new(() => new LocalFontResolver());

    private readonly string _fontsDirectory;
    private readonly Dictionary<string, string> _faceFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the resolver over the local system fonts directory.</summary>
    public LocalFontResolver()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.Fonts))
    {
    }

    /// <summary>Creates the resolver over an explicit fonts directory (tests).</summary>
    /// <param name="fontsDirectory">Directory containing TrueType font files.</param>
    public LocalFontResolver(string fontsDirectory)
    {
        _fontsDirectory = fontsDirectory;
        Register("arial", "arial.ttf");
        Register("arial|bold", "arialbd.ttf");
        Register("arial|italic", "ariali.ttf");
        Register("arial|bold|italic", "arialbi.ttf");
    }

    /// <summary>
    /// Assigns the shared resolver to PdfSharp global settings exactly once.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (GlobalFontSettings.FontResolver is null)
        {
            GlobalFontSettings.FontResolver = Instance.Value;
        }
    }

    /// <inheritdoc/>
    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var key = "arial" + (isBold ? "|bold" : string.Empty) + (isItalic ? "|italic" : string.Empty);
        if (_faceFiles.ContainsKey(key))
        {
            return new FontResolverInfo(key);
        }

        // Fall back to the regular face, then to any installed TrueType font.
        if (_faceFiles.ContainsKey("arial"))
        {
            return new FontResolverInfo("arial", isBold, isItalic);
        }

        var any = Directory.EnumerateFiles(_fontsDirectory, "*.ttf").FirstOrDefault();
        if (any is null)
        {
            throw new InvalidOperationException("No local TrueType fonts are available for the PDF summary.");
        }

        var fallbackKey = "fallback:" + Path.GetFileName(any);
        _faceFiles[fallbackKey] = any;
        return new FontResolverInfo(fallbackKey, isBold, isItalic);
    }

    /// <inheritdoc/>
    public byte[] GetFont(string faceName)
    {
        if (_faceFiles.TryGetValue(faceName, out var path))
        {
            return File.ReadAllBytes(path);
        }

        throw new InvalidOperationException($"Unknown font face '{faceName}'.");
    }

    private void Register(string key, string fileName)
    {
        var path = Path.Combine(_fontsDirectory, fileName);
        if (File.Exists(path))
        {
            _faceFiles[key] = path;
        }
    }
}
