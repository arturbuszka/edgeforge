using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>
/// Append-only history of item verdicts, one JSON object per line.
/// </summary>
/// <remarks>
/// Same format and the same reasons as <see cref="MealJournal"/>: appending a line
/// never rewrites what came before, and a power cut costs at most the last line.
/// It is the record a quality engineer reads back - "how many undersize this
/// shift, and what did they measure" - and what the limits get re-tuned against.
/// </remarks>
public sealed class ItemJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // No byte order mark; see MealJournal for the bug a BOM causes on line one.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public ItemJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }

    /// <exception cref="IOException">The line could not be written.</exception>
    public void Append(ItemVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var line = JsonSerializer.Serialize(VerdictLine.From(verdict), Json);

        File.AppendAllText(Path, line + Environment.NewLine, Utf8NoBom);
    }

    /// <summary>
    /// The on-disk shape, separate from <see cref="ItemVerdict"/> so the domain
    /// record can change without invalidating the history.
    /// </summary>
    private sealed record VerdictLine(
        string At,
        int Item,
        string Status,
        string? Reason,
        Dictionary<string, double> Measurements,
        int Frames)
    {
        public static VerdictLine From(ItemVerdict verdict) => new(
            verdict.At.ToString("O", CultureInfo.InvariantCulture),
            verdict.ItemId,
            Name(verdict.Status),
            verdict.Reason,
            verdict.Measurements.ToDictionary(m => m.Key, m => Math.Round(m.Value, 2)),
            verdict.ObservedFrames);

        private static string Name(InspectionStatus status) => status switch
        {
            InspectionStatus.Ok => "OK",
            InspectionStatus.Nok => "NOK",
            _ => "UNKNOWN"
        };
    }
}
