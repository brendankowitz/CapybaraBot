using System.Text;
using System.Text.Json;

namespace Capybara.Contracts.Storage;

/// <summary>
/// Append-only JSON-lines persistence shared by the durable stores (NFR-02): one compact JSON value per line,
/// appended with a single write and flushed to disk. A crash can only ever tear the <em>last</em> line, so
/// <see cref="Replay{T}"/> drops and truncates an unparseable final line, while corruption anywhere else fails loudly.
/// </summary>
/// <remarks>
/// Not thread-safe: each file must have exactly one owner that serializes its calls.
/// The serializer options must not enable indentation, or one value would span several lines.
/// </remarks>
public static class JsonlFile
{
    private const byte Newline = (byte)'\n';

    /// <summary>
    /// Reads every value and repairs the file so the next append starts on a fresh line.
    /// Returns an empty list when the file does not exist.
    /// </summary>
    /// <exception cref="InvalidDataException">A line other than the last is not valid JSON for <typeparamref name="T"/>.</exception>
    public static IReadOnlyList<T> Replay<T>(string path, JsonSerializerOptions options)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(path))
        {
            return [];
        }

        var bytes = File.ReadAllBytes(path);
        var values = new List<T>();
        var lineStart = 0;
        var lineNumber = 0;
        while (lineStart < bytes.Length)
        {
            lineNumber++;
            var newline = Array.IndexOf(bytes, Newline, lineStart);
            var terminated = newline >= 0;
            var end = terminated ? newline : bytes.Length;
            var line = Encoding.UTF8.GetString(bytes, lineStart, end - lineStart);
            if (!string.IsNullOrWhiteSpace(line))
            {
                var value = TryParse<T>(line, options);
                if (value is not null)
                {
                    values.Add(value);
                }
                else if (terminated)
                {
                    throw new InvalidDataException($"Corrupt record at line {lineNumber} of '{path}'.");
                }
                else
                {
                    // Torn tail from a crash mid-append: discard it so the next append starts cleanly.
                    using var torn = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                    torn.SetLength(lineStart);
                    return values;
                }
            }

            lineStart = end + 1;
        }

        if (bytes.Length > 0 && bytes[^1] != Newline)
        {
            // The last record is complete but lacks its terminator.
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.WriteByte(Newline);
        }

        return values;
    }

    /// <summary>Appends one value as a single line and flushes it to disk.</summary>
    public static void Append<T>(string path, T value, JsonSerializerOptions options)
    {
        using var stream = OpenForAppend(path);
        stream.Write(Encode(value, options));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Async variant of <see cref="Append{T}"/>.</summary>
    public static async ValueTask AppendAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        await using var stream = OpenForAppend(path);
        await stream.WriteAsync(Encode(value, options), cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Atomically replaces the file's contents (compaction): write a temp file, then move it over.</summary>
    public static void Rewrite<T>(string path, IEnumerable<T> values, JsonSerializerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(values);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var value in values)
            {
                stream.Write(Encode(value, options));
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static FileStream OpenForAppend(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private static byte[] Encode<T>(T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, options) + "\n");
    }

    private static T? TryParse<T>(string line, JsonSerializerOptions options)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(line, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
