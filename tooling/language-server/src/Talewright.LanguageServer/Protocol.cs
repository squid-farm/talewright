using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Talewright.LanguageServer;

internal sealed class Protocol(Stream input, Stream output)
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    public async Task<JsonElement?> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(buffer, cancellationToken) == 0)
            {
                if (header.Count != 0) throw new InvalidDataException("Incomplete LSP header.");
                return null;
            }
            header.Add(buffer[0]);
            if (header.Count > 8192) throw new InvalidDataException("LSP header exceeds 8 KiB.");
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        var lengths = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")
            .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (lengths.Length != 1 || !int.TryParse(lengths[0]["Content-Length:".Length..].Trim(),
                NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0 || length > MaxMessageBytes)
            throw new InvalidDataException("Expected one Content-Length between 1 and 16 MiB.");
        var body = new byte[length];
        await input.ReadExactlyAsync(body, cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    // The server event loop owns all writes, so frames cannot interleave.
    public async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), cancellationToken);
        await output.WriteAsync(body, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
