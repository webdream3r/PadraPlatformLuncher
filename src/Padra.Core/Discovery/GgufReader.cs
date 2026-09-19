using System.Buffers.Binary;
using System.Text;

namespace Padra.Core.Discovery;

/// <summary>What the GGUF header says about a model file.</summary>
public sealed record GgufMetadata(
    string? Architecture,
    int? ContextLength,
    int? BlockCount = null,
    int? EmbeddingLength = null,
    int? HeadCount = null,
    int? HeadCountKv = null,
    int? KeyLength = null,
    int? ValueLength = null,
    int? ExpertCount = null)
{
    public static readonly GgufMetadata Empty = new(null, null);

    /// <summary>True when this model routes tokens through experts.</summary>
    public bool IsMixtureOfExperts => ExpertCount is > 1;

    /// <summary>
    /// Key/value cache elements one token occupies, or null when the header does
    /// not carry enough of the shape to say.
    /// <para>
    /// Each half of the cache holds <c>blockCount × kvHeads × headDim</c> elements
    /// per token. Grouped-query models publish <c>attention.head_count_kv</c>;
    /// older ones repeat <c>attention.head_count</c>, and either is the right
    /// multiplier.
    /// </para>
    /// </summary>
    public long? KvCacheElementsPerToken()
    {
        if (BlockCount is not { } blocks || blocks <= 0)
        {
            return null;
        }

        var kvHeads = HeadCountKv ?? HeadCount;
        if (kvHeads is not { } heads || heads <= 0)
        {
            return null;
        }

        var keyDim = KeyLength ?? HeadDimension();
        var valueDim = ValueLength ?? HeadDimension();
        if (keyDim is not { } key || valueDim is not { } value || key <= 0 || value <= 0)
        {
            return null;
        }

        return (long)blocks * heads * (key + value);
    }

    private int? HeadDimension()
    {
        if (EmbeddingLength is not { } embedding || HeadCount is not { } heads ||
            embedding <= 0 || heads <= 0)
        {
            return null;
        }

        return embedding / heads;
    }
}

/// <summary>
/// Minimal GGUF metadata reader.
/// <para>
/// Only the key/value header is parsed, and only until the architecture and its
/// context length are known, so reading a 20 GB model costs a few hundred KB.
/// Nothing here throws: an unreadable or non-GGUF file yields empty metadata.
/// </para>
/// </summary>
public static class GgufReader
{
    /// <summary>
    /// Bytes actually decoded. Seeked-past payloads do not count, so a model whose
    /// tokenizer holds a million strings still costs only the eight-byte length in
    /// front of each one.
    /// </summary>
    private const long ScanLimit = 64L * 1024 * 1024;
    private static readonly byte[] Magic = "GGUF"u8.ToArray();

    private enum ValueType
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3, UInt32 = 4, Int32 = 5,
        Float32 = 6, Bool = 7, String = 8, Array = 9, UInt64 = 10, Int64 = 11,
        Float64 = 12,
    }

    public static GgufMetadata Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 1 << 16, FileOptions.SequentialScan);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return GgufMetadata.Empty;
        }
    }

    public static GgufMetadata Read(Stream stream)
    {
        try
        {
            var reader = new Cursor(stream);
            var magic = reader.Read(4);
            if (!magic.SequenceEqual(Magic))
            {
                return GgufMetadata.Empty;
            }

            var version = reader.UInt32();
            if (version > 0xFFFF)
            {
                reader.BigEndian = true;
                version = BinaryPrimitives.ReverseEndianness(version);
            }

            if (version < 2)
            {
                return GgufMetadata.Empty;
            }

            reader.UInt64();                          // tensor count
            var pairs = (long)reader.UInt64();
            if (pairs is < 0 or > 100_000)
            {
                return GgufMetadata.Empty;
            }

            string? architecture = null;
            var numbers = new Dictionary<string, int>(StringComparer.Ordinal);

            // Suffixes are architecture-prefixed in GGUF (`qwen3.block_count`), so
            // they are collected by suffix and resolved once the architecture is known.
            string[] wanted =
            {
                ".context_length", ".block_count", ".embedding_length",
                ".attention.head_count", ".attention.head_count_kv",
                ".attention.key_length", ".attention.value_length", ".expert_count",
            };

            // The whole key/value header is walked, in order. GGUF does not promise
            // that `general.architecture` precedes the keys it prefixes, nor that the
            // tokenizer comes last, so stopping early on either assumption silently
            // loses the grouped-query shape on files that order their keys otherwise.
            // Walking all of it is cheap because the only large payloads — the
            // tokenizer's vocabulary and merge arrays — are seeked past rather than
            // decoded.
            for (var index = 0; index < pairs; index++)
            {
                var key = reader.String();
                var value = reader.Value((ValueType)reader.UInt32(),
                    keep: wanted.Any(suffix => key.EndsWith(suffix, StringComparison.Ordinal)) ||
                          key == "general.architecture");

                if (key == "general.architecture" && value is string text)
                {
                    architecture = text;
                }
                else if (value is long number && number is > 0 and <= int.MaxValue)
                {
                    numbers[key] = (int)number;
                }
            }

            int? Lookup(string suffix)
            {
                if (architecture is not null &&
                    numbers.TryGetValue(architecture + suffix, out var exact))
                {
                    return exact;
                }

                // No architecture, or a file that prefixes its keys with something
                // else: fall back to the only key carrying that suffix, deterministically.
                var candidates = numbers
                    .Where(p => p.Key.EndsWith(suffix, StringComparison.Ordinal))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .ToList();
                return candidates.Count > 0 ? candidates[0].Value : null;
            }

            return new GgufMetadata(
                architecture,
                Lookup(".context_length"),
                Lookup(".block_count"),
                Lookup(".embedding_length"),
                Lookup(".attention.head_count"),
                Lookup(".attention.head_count_kv"),
                Lookup(".attention.key_length"),
                Lookup(".attention.value_length"),
                Lookup(".expert_count"));
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or
                                       InvalidDataException or OverflowException)
        {
            return GgufMetadata.Empty;
        }
    }

    /// <summary>Sequential little/big-endian reader with a hard budget.</summary>
    private sealed class Cursor(Stream stream)
    {
        private long _consumed;

        public bool BigEndian { get; set; }

        public byte[] Read(int count)
        {
            if (_consumed + count > ScanLimit)
            {
                throw new InvalidDataException("gguf metadata exceeds the scan limit");
            }

            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk == 0)
                {
                    throw new EndOfStreamException("truncated gguf metadata");
                }

                read += chunk;
            }

            _consumed += count;
            return buffer;
        }

        /// <summary>
        /// Move past <paramref name="count"/> bytes without decoding them. On a
        /// seekable stream this is a seek and costs nothing, which is what lets the
        /// whole header be walked rather than guessed at.
        /// </summary>
        public void Skip(long count)
        {
            if (count < 0)
            {
                throw new InvalidDataException("negative gguf length");
            }

            if (stream.CanSeek)
            {
                if (stream.Position + count > stream.Length)
                {
                    throw new EndOfStreamException("truncated gguf metadata");
                }

                stream.Seek(count, SeekOrigin.Current);
                return;
            }

            var remaining = count;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(remaining, 1 << 16);
                Read(chunk);
                remaining -= chunk;
            }
        }

        public uint UInt32()
        {
            var bytes = Read(4);
            return BigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public ulong UInt64()
        {
            var bytes = Read(8);
            return BigEndian
                ? BinaryPrimitives.ReadUInt64BigEndian(bytes)
                : BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        public string String()
        {
            var length = StringLength();
            return Encoding.UTF8.GetString(Read((int)length));
        }

        /// <summary>Step over a string without allocating it.</summary>
        public void SkipString() => Skip((long)StringLength());

        private ulong StringLength()
        {
            var length = UInt64();
            if (length > 1 << 22)
            {
                throw new InvalidDataException("implausible gguf string length");
            }

            return length;
        }

        /// <param name="keep">
        /// False for a value nothing here reads, which lets the payload be seeked
        /// past instead of decoded.
        /// </param>
        public object? Value(ValueType type, bool keep = true)
        {
            switch (type)
            {
                case ValueType.UInt8: return (long)Read(1)[0];
                case ValueType.Int8: return (long)(sbyte)Read(1)[0];
                case ValueType.Bool: return Read(1)[0] != 0;
                case ValueType.UInt16:
                {
                    var bytes = Read(2);
                    return (long)(BigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(bytes)
                        : BinaryPrimitives.ReadUInt16LittleEndian(bytes));
                }

                case ValueType.Int16:
                {
                    var bytes = Read(2);
                    return (long)(BigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(bytes)
                        : BinaryPrimitives.ReadInt16LittleEndian(bytes));
                }

                case ValueType.UInt32: return (long)UInt32();
                case ValueType.Int32: return (long)(int)UInt32();
                case ValueType.Float32: Skip(4); return null;
                case ValueType.UInt64: return (long)UInt64();
                case ValueType.Int64: return (long)UInt64();
                case ValueType.Float64: Skip(8); return null;
                case ValueType.String:
                    if (!keep)
                    {
                        SkipString();
                        return null;
                    }

                    return String();

                case ValueType.Array:
                {
                    var itemType = (ValueType)UInt32();
                    var count = (long)UInt64();
                    if (count < 0)
                    {
                        throw new InvalidDataException("negative gguf array length");
                    }

                    var width = FixedWidth(itemType);
                    if (width > 0)
                    {
                        Skip(width * count);          // skip the payload wholesale
                        return null;
                    }

                    // A vocabulary is an array of strings: step over each one rather
                    // than decoding 150 000 values nothing here uses.
                    for (var index = 0L; index < count; index++)
                    {
                        Value(itemType, keep: false);
                    }

                    return null;
                }

                default:
                    throw new InvalidDataException($"unknown gguf value type {(int)type}");
            }
        }

        private static int FixedWidth(ValueType type) => type switch
        {
            ValueType.UInt8 or ValueType.Int8 or ValueType.Bool => 1,
            ValueType.UInt16 or ValueType.Int16 => 2,
            ValueType.UInt32 or ValueType.Int32 or ValueType.Float32 => 4,
            ValueType.UInt64 or ValueType.Int64 or ValueType.Float64 => 8,
            _ => 0,
        };
    }
}
