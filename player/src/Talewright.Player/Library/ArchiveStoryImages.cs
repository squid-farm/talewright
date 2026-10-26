using System.IO.Compression;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.Library;

/// <summary>A retained, compressed story archive. Expanded image bytes are read only on demand.</summary>
public sealed class ArchiveStoryImages : IStoryImageSource
{
    private readonly object _sync = new();
    private readonly Stream _input;
    private readonly ZipArchive _archive;
    private readonly IReadOnlyDictionary<string, ZipArchiveEntry> _images;
    private readonly Dictionary<string, long> _highWater = new(StringComparer.Ordinal);
    private readonly HashSet<ImageStream> _streams = [];
    private long _expandedBytes;
    private bool _disposed;

    internal ArchiveStoryImages(Stream input, ZipArchive archive,
        IReadOnlyDictionary<string, ZipArchiveEntry> images, long expandedTextBytes)
    {
        _input = input;
        _archive = archive;
        _images = images;
        _expandedBytes = expandedTextBytes;
    }

    /// <summary>Takes ownership of a readable, seekable ZIP stream, including on failure.</summary>
    public static LoadedStory Open(Stream input)
    {
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var source = StorySource.FromZip(archive, out var images, out var expandedTextBytes);
            var remaining = StorySource.MaxArchiveExpandedBytes - expandedTextBytes;
            foreach (var entry in images.Values)
            {
                if (entry.Length > remaining)
                    throw new InvalidDataException("The story archive exceeds the 50 MiB total expanded-content limit.");
                remaining -= entry.Length;
            }
            var owner = new ArchiveStoryImages(input, archive, images, expandedTextBytes);
            return new(source, StoryImages.FromSource(source, owner));
        }
        catch
        {
            archive?.Dispose();
            input.Dispose();
            throw;
        }
    }

    public ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_images.TryGetValue(path, out var entry))
                throw new FileNotFoundException($"Image '{path}' is unavailable in the story archive.", path);
            var stream = new ImageStream(this, path, entry.Open(), cancellationToken);
            _streams.Add(stream);
            return ValueTask.FromResult<Stream>(stream);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var stream in _streams.ToArray()) stream.Dispose();
            try { _archive.Dispose(); }
            finally { _input.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImageStream(ArchiveStoryImages owner, string path, Stream input,
        CancellationToken cancellationToken) : Stream
    {
        private long _position;
        private bool _disposed;
        private bool _failed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // ZipArchive entry streams share a seekable compressed input. Serialize bounded read chunks, not
            // whole transfers, so multiple visible images can progress without racing its position.
            lock (owner._sync)
            {
                ObjectDisposedException.ThrowIf(_disposed || owner._disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_failed) throw new InvalidDataException($"Image '{path}' exceeded the archive expansion bound.");
                if (buffer.IsEmpty) return 0;
                var previous = owner._highWater.GetValueOrDefault(path);
                var remaining = StorySource.MaxArchiveExpandedBytes - owner._expandedBytes;
                var allowed = previous - _position + remaining;
                var requested = (int)Math.Min(Math.Min(buffer.Length, 64 * 1024), allowed + 1);
                var count = input.Read(buffer[..requested]);
                var position = _position + count;
                var additional = Math.Max(0, position - previous);
                if (additional > remaining)
                {
                    _failed = true;
                    throw new InvalidDataException("The story archive exceeds the 50 MiB total expanded-content limit.");
                }
                _position = position;
                if (additional > 0)
                {
                    owner._highWater[path] = position;
                    owner._expandedBytes += additional;
                }
                return count;
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (owner._sync)
                {
                    if (_disposed) return;
                    _disposed = true;
                    owner._streams.Remove(this);
                    input.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
