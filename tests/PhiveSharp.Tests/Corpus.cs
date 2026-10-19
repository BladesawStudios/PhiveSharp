using System.Collections.Concurrent;
using SarcSharp;
using ZsDicSharp;

namespace PhiveSharp.Tests;

// Corpus tests run only when TOTK_ROMFS points at a romfs root; otherwise they are skipped.
internal static class Corpus
{
    internal const string TotkEnv = "TOTK_ROMFS";

    internal static string? TotkRoot =>
        Environment.GetEnvironmentVariable(TotkEnv) is { } root && Directory.Exists(root) ? root : null;

    // Actor packs carry the cloth and most object shapes; reading every pack is the slow part, so it happens once.
    private static readonly Lazy<IReadOnlyList<(string Name, byte[] Data)>> PackFiles = new(ReadPacks);

    private static IReadOnlyList<(string Name, byte[] Data)> ReadPacks()
    {
        string root = TotkRoot!;
        using ZsDic zs = ZsDic.FromRomfs(root);
        ConcurrentDictionary<string, byte[]> found = new();
        Parallel.ForEach(Directory.EnumerateFiles(Path.Combine(root, "Pack"), "*.pack.zs", SearchOption.AllDirectories), Workers, path =>
        {
            SarcFile pack;
            try { pack = SarcFile.FromBinary(zs.DecompressFile(path)); }
            catch (InvalidDataException) { return; }
            foreach (SarcEntry e in pack.Entries)
            {
                if (!e.Name.Contains(".bph") || e.Name.EndsWith(".bphhb") || e.Name.EndsWith(".bphhb.zs")) continue;
                string name = e.Name.EndsWith(".zs") ? e.Name[..^3] : e.Name;
                if (File.Exists(Path.Combine(root, name + ".zs"))) continue;
                if (!found.ContainsKey(name)) found.TryAdd(name, e.Name.EndsWith(".zs") ? zs.Decompress(e.Data, e.Name) : e.Data);
            }
        });
        return [.. found.Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>
    /// Every Phive file in the romfs - loose and in packs, helper bones aside since they are AAMP -
    /// decompressed and visited in parallel with its name and extension.
    /// </summary>
    internal static int ForEachFile(Action<string, string, byte[]> visit, params string[] extensions)
    {
        string root = TotkRoot!;
        bool Wanted(string name) => extensions.Length == 0 || extensions.Any(name.EndsWith);
        int count = 0;
        void Visit(string name, byte[] data)
        {
            Interlocked.Increment(ref count);
            visit(name, Path.GetExtension(name), data);
        }

        if (extensions.Length == 0 || extensions.Any(e => e is ".bphcl" or ".bphsh"))
            Parallel.ForEach(PackFiles.Value.Where(f => Wanted(f.Name)), Workers, f => Visit(f.Name, f.Data));

        using ZsDic zs = ZsDic.FromRomfs(root);
        Parallel.ForEach(Directory.EnumerateFiles(Path.Combine(root, "Phive"), "*.bph*.zs", SearchOption.AllDirectories), Workers, path =>
        {
            string name = Path.GetRelativePath(root, path)[..^3];
            if (Wanted(name)) Visit(name, zs.DecompressFile(path));
        });
        return count;
    }

    internal static readonly ParallelOptions Workers = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };
}

/// <summary>What a corpus test found wrong: a count, and the first few to show.</summary>
internal sealed class Problems
{
    private readonly ConcurrentQueue<string> _first = new();
    private int _count;

    public void Add(string problem)
    {
        if (Interlocked.Increment(ref _count) <= 20) _first.Enqueue(problem);
    }

    public void AssertNone() => Assert.True(_count == 0, $"{_count} problems, first:\n{string.Join("\n", _first)}");
}

public sealed class TotkCorpusFactAttribute : FactAttribute
{
    public TotkCorpusFactAttribute()
    {
        if (Corpus.TotkRoot is null) Skip = $"Set {Corpus.TotkEnv} to a TotK romfs root to run the corpus tests.";
    }
}
