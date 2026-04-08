using System.Text;
using DocControl.Core.Configuration;
using DocControl.Core.Models;

namespace DocControl.Infrastructure.Services;

public sealed class CodeGenerator
{
    private readonly DocumentConfig config;

    public CodeGenerator(DocumentConfig config)
    {
        this.config = config;
    }

    public string BuildCode(CodeSeriesKey key, int number)
    {
        var parts = new List<string>();
        var levelCount = Math.Clamp(config.LevelCount, 1, 6);
        if (levelCount >= 1) parts.Add(key.Level1);
        if (levelCount >= 2) parts.Add(key.Level2);
        if (levelCount >= 3) parts.Add(key.Level3);
        if (levelCount >= 4 && !string.IsNullOrWhiteSpace(key.Level4)) parts.Add(key.Level4);
        if (levelCount >= 5 && !string.IsNullOrWhiteSpace(key.Level5)) parts.Add(key.Level5);
        if (levelCount >= 6 && !string.IsNullOrWhiteSpace(key.Level6)) parts.Add(key.Level6);
        parts.Add(number.ToString().PadLeft(config.PaddingLength, '0'));
        return string.Join(config.Separator, parts);
    }

    public string BuildFileName(CodeSeriesKey key, int number, string freeText, string? extension)
    {
        var baseCode = BuildCode(key, number);
        var sb = new StringBuilder(baseCode);

        var trimmedFree = freeText?.Trim().Replace(' ', '_');
        if (!string.IsNullOrWhiteSpace(trimmedFree))
        {
            sb.Append('_');
            sb.Append(trimmedFree);
        }

        if (!string.IsNullOrWhiteSpace(extension))
        {
            var sanitized = extension.StartsWith('.') ? extension : $".{extension}";
            sb.Append(sanitized);
        }
        return sb.ToString();
    }
}
