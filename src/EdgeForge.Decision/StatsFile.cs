using System.Text;
using System.Text.Json;

namespace EdgeForge.Decision;

/// <summary>
/// The current state of the day, as a small JSON file beside the binary.
/// </summary>
/// <remarks>
/// This is what the Twitch overlay reads: an OBS Browser Source polls the file
/// from local disk. No HTTP server is involved in the overlay, and none is needed
/// to move a number onto a stream.
///
/// The shape lives in <see cref="StatsSnapshot"/>, which the optional web view also
/// serves, so the file and the endpoint cannot drift apart.
///
/// It carries counts and timestamps only. No frames, no boxes, no image data:
/// only events leave this file.
///
/// Written atomically, via a temporary file and a replace. OBS polls on its own
/// schedule and will eventually read exactly while a write is in progress; a
/// truncated file would show the overlay a parse error instead of a number.
/// </remarks>
public sealed class StatsFile
{
    private readonly string _path;

    public StatsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    /// <summary>Writes the current snapshot, replacing whatever was there.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void Write(DailyMealLog log, bool currentlyFeeding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(log);

        var snapshot = StatsSnapshot.Build(log, currentlyFeeding, now);

        WriteAtomically(JsonSerializer.Serialize(snapshot, StatsSnapshot.SerializerOptions));
    }

    /// <summary>
    /// Write to a sibling temp file, flush it to disk, then move it into place.
    /// </summary>
    /// <remarks>
    /// The move is the atomic step: a reader sees either the whole old file or the
    /// whole new one, never a half-written one. The temp file is a sibling because
    /// a move is only atomic within one volume - via the system temp directory it
    /// would degrade into a copy, which is exactly the tear being avoided.
    /// </remarks>
    private void WriteAtomically(string json)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = _path + ".tmp";

        using (var stream = new FileStream(
                   temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }
}
