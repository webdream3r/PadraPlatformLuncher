using System.Buffers.Binary;
using System.Text;
using Padra.Core.Discovery;
using Xunit;

namespace Padra.Tests;

[Collection("environment")]
public sealed class DiscoveryTests
{
    // ------------------------------------------------------------- naming #
    [Theory]
    [InlineData("Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf", "unsloth/Qwen3.6-35B-A3B-GGUF", "UD-Q4_K_XL")]
    [InlineData("Qwen3.6-35B-A3B-Q8_0.gguf", "unsloth/Qwen3.6-35B-A3B-GGUF", "Q8_0")]
    [InlineData("BF16/Qwen3.6-35B-A3B-BF16-00001-of-00002.gguf", "unsloth/Qwen3.6-35B-A3B-GGUF", "BF16")]
    [InlineData("Qwen3.5-4B-MXFP4_MOE.gguf", "unsloth/Qwen3.5-4B-GGUF", "MXFP4_MOE")]
    [InlineData("some-other-name-UD-IQ2_M.gguf", "unsloth/Whatever-GGUF", "UD-IQ2_M")]
    public void Quant_is_derived_from_the_file_layout(string relative, string repo, string expected) =>
        Assert.Equal(expected, ModelNaming.QuantFromRelativePath(relative, repo));

    [Fact]
    public void Repo_base_name_strips_the_gguf_suffix()
    {
        Assert.Equal("Qwen3.6-35B-A3B", ModelNaming.RepoBaseName("unsloth/Qwen3.6-35B-A3B-GGUF"));
        Assert.Equal("plain", ModelNaming.RepoBaseName("vendor/plain"));
    }

    // --------------------------------------------------------------- gguf #
    [Fact]
    public void Gguf_context_length_is_read()
    {
        using var environment = new TestEnvironment();
        var path = WriteGguf(Path.Combine(environment.Work, "model.gguf"), "qwen35", 262144);

        var metadata = GgufReader.Read(path);
        Assert.Equal("qwen35", metadata.Architecture);
        Assert.Equal(262144, metadata.ContextLength);
    }

    [Fact]
    public void Unreadable_files_never_throw()
    {
        using var environment = new TestEnvironment();
        var junk = Path.Combine(environment.Work, "junk.gguf");
        File.WriteAllText(junk, "not a gguf file at all");

        Assert.Equal(GgufMetadata.Empty, GgufReader.Read(junk));
        Assert.Null(GgufReader.Read(Path.Combine(environment.Work, "missing.gguf")).ContextLength);
    }

    // ------------------------------------------------------------ scanning #
    [Fact]
    public void The_scan_finds_models_and_sums_real_sizes()
    {
        using var environment = new TestEnvironment();
        var cache = Environment.GetEnvironmentVariable("HF_HUB_CACHE")!;
        BuildCache(cache, "unsloth/Qwen3.5-4B-GGUF", new (string, int)[]
        {
            ("Qwen3.5-4B-UD-Q4_K_XL.gguf", 2048),
            ("Qwen3.5-4B-Q8_0.gguf", 4096),
            ("BF16/Qwen3.5-4B-BF16-00001-of-00002.gguf", 1024),
            ("BF16/Qwen3.5-4B-BF16-00002-of-00002.gguf", 1024),
            ("README.md", 10),
        });

        var models = ModelScanner.Scan(readHeaders: false);
        var byQuant = models.ToDictionary(m => m.Quant, m => m);

        Assert.Equal(new[] { "BF16", "Q8_0", "UD-Q4_K_XL" }, byQuant.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(2048, byQuant["UD-Q4_K_XL"].SizeBytes);
        Assert.Equal(2048, byQuant["BF16"].SizeBytes);
        Assert.Equal(2, byQuant["BF16"].FileCount);
        Assert.Equal("unsloth/Qwen3.5-4B-GGUF:Q8_0", byQuant["Q8_0"].Spec);
        Assert.All(models, m => Assert.Equal(ModelSource.HuggingFace, m.Source));
    }

    [Fact]
    public void Context_length_comes_from_the_header()
    {
        using var environment = new TestEnvironment();
        var cache = Environment.GetEnvironmentVariable("HF_HUB_CACHE")!;
        var snapshot = Path.Combine(cache, "models--unsloth--Tiny-GGUF", "snapshots", "s1");
        Directory.CreateDirectory(snapshot);
        WriteGguf(Path.Combine(snapshot, "Tiny-Q4_K_M.gguf"), "tiny", 8192, padding: 256);

        var model = Assert.Single(ModelScanner.Scan());
        Assert.Equal(8192, model.ContextLength);
        Assert.Equal("tiny", model.Architecture);
        Assert.Equal("Q4_K_M", model.Quant);
    }

    [Fact]
    public void Extra_folders_are_scanned()
    {
        using var environment = new TestEnvironment();
        var folder = Path.Combine(environment.Work, "models", "vendor", "Some-GGUF");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Some-Q4_K_M.gguf"), new byte[1500]);

        var model = Assert.Single(ModelScanner.Scan(
            new[] { Path.Combine(environment.Work, "models") }, readHeaders: false));
        Assert.Equal("vendor/Some-GGUF", model.Repo);
        Assert.Equal("Q4_K_M", model.Quant);
        Assert.Equal(ModelSource.Folder, model.Source);
        Assert.Equal(1500, model.SizeBytes);
    }

    [Fact]
    public void Cache_roots_follow_the_environment()
    {
        using var environment = new TestEnvironment();
        Environment.SetEnvironmentVariable("HF_HOME", Path.Combine(environment.Work, "hf"));

        var roots = ModelScanner.HuggingFaceCacheRoots();
        Assert.Equal(Environment.GetEnvironmentVariable("HF_HUB_CACHE"), roots[0]);
        Assert.Contains(Path.Combine(environment.Work, "hf", "hub"), roots);
    }

    // ------------------------------------------------------------- catalog #
    [Fact]
    public async Task Catalog_groups_variants_and_marks_downloads()
    {
        var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/models/unsloth/Demo-GGUF" =>
                """{"lastModified":"2026-01-01","downloads":5,"likes":1,"gguf":{"context_length":32768}}""",
            "/api/models/unsloth/Demo-GGUF/tree/main" =>
                """
                [{"type":"file","path":"Demo-Q4_K_M.gguf","size":100},
                 {"type":"file","path":"Demo-Q8_0.gguf","size":300},
                 {"type":"file","path":"README.md","size":5},
                 {"type":"directory","path":"BF16"},
                 {"type":"file","path":"BF16/Demo-BF16-00001-of-00002.gguf","lfs":{"size":400}},
                 {"type":"file","path":"BF16/Demo-BF16-00002-of-00002.gguf","lfs":{"size":100}}]
                """,
            _ => throw new InvalidOperationException(request.RequestUri.ToString()),
        }))
        { BaseAddress = new Uri("https://huggingface.co") };

        var record = await new HuggingFaceCatalog(http).VariantsAsync("unsloth/Demo-GGUF");

        Assert.Equal(new Dictionary<string, long> { ["Q4_K_M"] = 100, ["Q8_0"] = 300, ["BF16"] = 500 },
            record.Variants.ToDictionary(v => v.Quant, v => v.SizeBytes));
        Assert.All(record.Variants, v => Assert.Equal(32768, v.ContextLength));

        var local = new[]
        {
            new LocalModel("unsloth/Demo-GGUF", "Q8_0", ModelSource.HuggingFace, 299, 1,
                DateTimeOffset.UnixEpoch, "/tmp"),
        };
        var merged = HuggingFaceCatalog.MergeDownloaded(record, local);
        Assert.Equal(new Dictionary<string, bool> { ["Q4_K_M"] = false, ["Q8_0"] = true, ["BF16"] = false },
            merged.Variants.ToDictionary(v => v.Quant, v => v.Downloaded));
    }

    [Fact]
    public async Task Catalog_lists_repositories()
    {
        var http = new HttpClient(new StubHandler(_ =>
            """[{"id":"unsloth/Demo-GGUF","downloads":9,"likes":1}]"""))
        { BaseAddress = new Uri("https://huggingface.co") };

        var repos = await new HuggingFaceCatalog(http).RepositoriesAsync("demo");
        Assert.Equal("unsloth/Demo-GGUF", Assert.Single(repos).Repo);
    }

    [Fact]
    public async Task Catalog_errors_are_wrapped()
    {
        var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")))
        { BaseAddress = new Uri("https://huggingface.co") };

        await Assert.ThrowsAsync<CatalogException>(() =>
            new HuggingFaceCatalog(http).RepositoriesAsync(refresh: true));
    }

    // ---------------------------------------------------------- gguf shape #
    [Theory]
    [InlineData("architecture first")]
    [InlineData("architecture last")]
    [InlineData("tokenizer first")]
    public void The_grouped_query_shape_survives_any_key_order(string order)
    {
        using var environment = new TestEnvironment();

        var shape = new List<(string Key, object Value)>
        {
            ("qwen3.context_length", 262144),
            ("qwen3.block_count", 36),
            ("qwen3.embedding_length", 2560),
            ("qwen3.attention.head_count", 32),
            ("qwen3.attention.head_count_kv", 8),
            ("qwen3.attention.key_length", 128),
            ("qwen3.attention.value_length", 128),
        };

        var pairs = new List<(string Key, object Value)>();
        var vocabulary = ("tokenizer.ggml.tokens", (object)Enumerable
            .Range(0, 20_000).Select(i => $"token{i}").ToArray());

        switch (order)
        {
            case "architecture first":
                pairs.Add(("general.architecture", "qwen3"));
                pairs.AddRange(shape);
                pairs.Add(vocabulary);
                break;

            case "architecture last":
                pairs.AddRange(shape);
                pairs.Add(vocabulary);
                pairs.Add(("general.architecture", "qwen3"));
                break;

            default:
                pairs.Add(vocabulary);
                pairs.Add(("general.architecture", "qwen3"));
                pairs.AddRange(shape);
                break;
        }

        var metadata = GgufReader.Read(
            WritePairs(Path.Combine(environment.Work, "model.gguf"), pairs));

        Assert.Equal("qwen3", metadata.Architecture);
        Assert.Equal(262144, metadata.ContextLength);
        Assert.Equal(36, metadata.BlockCount);
        Assert.Equal(8, metadata.HeadCountKv);
        Assert.Equal(128, metadata.KeyLength);

        // 36 blocks × 8 kv heads × (128 + 128).
        Assert.Equal(36L * 8 * 256, metadata.KvCacheElementsPerToken());
    }

    [Fact]
    public void A_model_without_attention_metadata_still_reports_what_it_has()
    {
        using var environment = new TestEnvironment();

        var metadata = GgufReader.Read(WritePairs(Path.Combine(environment.Work, "mamba.gguf"),
            new List<(string, object)>
            {
                ("general.architecture", "mamba"),
                ("mamba.context_length", 131072),
                ("mamba.block_count", 64),
                ("tokenizer.ggml.tokens", Enumerable.Range(0, 5_000).Select(i => $"t{i}").ToArray()),
            }));

        Assert.Equal("mamba", metadata.Architecture);
        Assert.Equal(131072, metadata.ContextLength);
        Assert.Equal(64, metadata.BlockCount);

        // No head counts, so no cache figure is invented.
        Assert.Null(metadata.KvCacheElementsPerToken());
    }

    [Fact]
    public void A_large_vocabulary_is_stepped_over_not_decoded()
    {
        using var environment = new TestEnvironment();
        var path = WritePairs(Path.Combine(environment.Work, "big.gguf"),
            new List<(string, object)>
            {
                ("tokenizer.ggml.tokens", Enumerable.Range(0, 150_000)
                    .Select(i => new string('t', 40) + i).ToArray()),
                ("general.architecture", "llama"),
                ("llama.context_length", 8192),
                ("llama.block_count", 32),
                ("llama.embedding_length", 4096),
                ("llama.attention.head_count", 32),
            });

        using var counting = new CountingStream(File.OpenRead(path));
        var metadata = GgufReader.Read(counting);

        Assert.Equal(8192, metadata.ContextLength);
        Assert.Equal(32, metadata.BlockCount);

        // The vocabulary alone is over 6 MB; only its string lengths are read.
        Assert.True(counting.BytesRead < new FileInfo(path).Length / 4,
            $"read {counting.BytesRead} of {new FileInfo(path).Length} bytes");
    }

    /// <summary>Counts the bytes a reader actually pulls, as opposed to seeks past.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------ fixtures #
    /// <summary>Write a GGUF whose header carries exactly these pairs, in this order.</summary>
    internal static string WritePairs(string path, List<(string Key, object Value)> pairs)
    {
        static byte[] U32(uint value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            return buffer;
        }

        static byte[] U64(ulong value)
        {
            var buffer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            return buffer;
        }

        static byte[] Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return U64((ulong)bytes.Length).Concat(bytes).ToArray();
        }

        var payload = new List<byte>();
        foreach (var (key, value) in pairs)
        {
            payload.AddRange(Text(key));
            switch (value)
            {
                case string text:
                    payload.AddRange(U32(8));
                    payload.AddRange(Text(text));
                    break;

                case int number:
                    payload.AddRange(U32(4));
                    payload.AddRange(U32((uint)number));
                    break;

                case string[] items:
                    payload.AddRange(U32(9));
                    payload.AddRange(U32(8));            // array of strings
                    payload.AddRange(U64((ulong)items.Length));
                    foreach (var item in items)
                    {
                        payload.AddRange(Text(item));
                    }

                    break;

                default:
                    throw new NotSupportedException($"unsupported gguf value {value.GetType()}");
            }
        }

        var header = Encoding.ASCII.GetBytes("GGUF")
            .Concat(U32(3)).Concat(U64(0)).Concat(U64((ulong)pairs.Count)).ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, header.Concat(payload).ToArray());
        return path;
    }

    internal static string WriteGguf(string path, string architecture, int contextLength,
        int padding = 0)
    {
        static byte[] U32(uint value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            return buffer;
        }

        static byte[] U64(ulong value)
        {
            var buffer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            return buffer;
        }

        static byte[] Key(string name) => U64((ulong)name.Length).Concat(Encoding.UTF8.GetBytes(name)).ToArray();

        var payload = new List<byte>();
        payload.AddRange(Key("general.architecture"));
        payload.AddRange(U32(8));
        payload.AddRange(U64((ulong)architecture.Length));
        payload.AddRange(Encoding.UTF8.GetBytes(architecture));

        // A vocabulary-sized array in between, to prove the reader skips it cheaply.
        payload.AddRange(Key("tokenizer.ggml.padding"));
        payload.AddRange(U32(9));
        payload.AddRange(U32(0));
        payload.AddRange(U64(64));
        payload.AddRange(new byte[64]);

        payload.AddRange(Key($"{architecture}.context_length"));
        payload.AddRange(U32(4));
        payload.AddRange(U32((uint)contextLength));

        var header = Encoding.ASCII.GetBytes("GGUF")
            .Concat(U32(3)).Concat(U64(0)).Concat(U64(3)).ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, header.Concat(payload).Concat(new byte[padding]).ToArray());
        return path;
    }

    private static void BuildCache(string root, string repo, IEnumerable<(string Name, int Size)> files)
    {
        var folder = Path.Combine(root, "models--" + repo.Replace("/", "--"));
        var snapshot = Path.Combine(folder, "snapshots", "abc123");
        Directory.CreateDirectory(snapshot);

        foreach (var (name, size) in files)
        {
            var target = Path.Combine(snapshot, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, new byte[size]);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            });
    }
}
