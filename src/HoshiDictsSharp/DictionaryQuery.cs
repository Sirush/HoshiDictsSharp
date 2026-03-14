using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4;

namespace HoshiDictsSharp;

public sealed record Frequency(int Value, string DisplayValue);
public sealed record GlossaryEntry(string DictName, string Glossary, string DefinitionTags, string TermTags);
public sealed record FrequencyEntry(string DictName, List<Frequency> Frequencies);
public sealed record PitchEntry(string DictName, List<int> PitchPositions);

public sealed class TermResult
{
    public string Expression = "";
    public string Reading = "";
    public string Rules = "";
    public List<GlossaryEntry> Glossaries = [];
    public List<FrequencyEntry> Frequencies = [];
    public List<PitchEntry> Pitches = [];
}

public sealed unsafe class DictionaryQuery : IDisposable
{
    private struct GlossaryBlock
    {
        public long CumulativeRawOffset;
        public int CompressedDataOffset;
        public int CompressedSize;
        public int UncompressedSize;
    }

    private sealed unsafe class LoadedDict : IDisposable
    {
        public string Name = "";
        public string? Styles;
        public Mphf Phf = new();
        public byte* BlobsPtr;
        public long BlobsLength;
        public MemoryMappedFile? BlobsMmf;
        public MemoryMappedViewAccessor? BlobsAccessor;
        public ulong[] Offsets = [];
        public GlossaryBlock[] GlossaryBlocks = [];
        public ulong EntryDataOffset;
        public MemoryMappedFile? MediaMmf;
        public MemoryMappedViewAccessor? MediaAccessor;
        public long MediaLength;
        public Dictionary<string, (uint Size, uint Offset)>? MediaIndex;

        public const int BlockCacheCapacity = 64;
        public readonly Dictionary<int, byte[]> BlockCache = new(BlockCacheCapacity);
        public readonly Queue<int> BlockCacheOrder = new(BlockCacheCapacity);

        public ReadOnlySpan<byte> BlobSpan(int offset, int length) => new(BlobsPtr + offset, length);

        public void Dispose()
        {
            BlobsAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            BlobsAccessor?.Dispose();
            BlobsMmf?.Dispose();
            MediaAccessor?.Dispose();
            MediaMmf?.Dispose();
        }
    }

    private readonly List<LoadedDict> _termDicts = [];
    private readonly List<LoadedDict> _freqDicts = [];
    private readonly List<LoadedDict> _pitchDicts = [];

    public void AddTermDict(string path) => AddDict(path, _termDicts);
    public void AddFreqDict(string path) => AddDict(path, _freqDicts);
    public void AddPitchDict(string path) => AddDict(path, _pitchDicts);

    private static void AddDict(string path, List<LoadedDict> target)
    {
        string markerPath = Path.Combine(path, ".hoshidicts_1");
        if (!File.Exists(markerPath)) return;

        var dict = new LoadedDict();

        byte[] indexJson = File.ReadAllBytes(Path.Combine(path, "index.json"));
        dict.Name = YomitanParser.ParseIndexTitle(indexJson);
        if (string.IsNullOrEmpty(dict.Name))
            dict.Name = Path.GetFileName(path);

        string stylesPath = Path.Combine(path, "styles.css");
        if (File.Exists(stylesPath))
            dict.Styles = File.ReadAllText(stylesPath);

        dict.Phf.Load(Path.Combine(path, "hash.mph"));

        byte[] offsetBytes = File.ReadAllBytes(Path.Combine(path, "offsets.bin"));
        dict.Offsets = MemoryMarshal.Cast<byte, ulong>(offsetBytes.AsSpan()).ToArray();

        string blobsPath = Path.Combine(path, "blobs.bin");
        var blobsFi = new FileInfo(blobsPath);
        if (blobsFi.Length > 0)
        {
            dict.BlobsLength = blobsFi.Length;
            dict.BlobsMmf = MemoryMappedFile.CreateFromFile(blobsPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            dict.BlobsAccessor = dict.BlobsMmf.CreateViewAccessor(0, blobsFi.Length, MemoryMappedFileAccess.Read);
            byte* ptr = null;
            dict.BlobsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            dict.BlobsPtr = ptr + dict.BlobsAccessor.PointerOffset;
        }
        BuildGlossaryBlockIndex(dict);

        string mediaPath = Path.Combine(path, "media.bin");
        if (File.Exists(mediaPath))
        {
            var fi = new FileInfo(mediaPath);
            if (fi.Length > 0)
            {
                dict.MediaLength = fi.Length;
                dict.MediaMmf = MemoryMappedFile.CreateFromFile(mediaPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                dict.MediaAccessor = dict.MediaMmf.CreateViewAccessor(0, fi.Length, MemoryMappedFileAccess.Read);
                dict.MediaIndex = BuildMediaIndex(dict.MediaAccessor, fi.Length);
            }
        }

        target.Add(dict);
    }

    private static void BuildGlossaryBlockIndex(LoadedDict dict)
    {
        if (dict.BlobsLength < 8) return;

        ulong totalCompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(dict.BlobSpan(0, 8));
        if (totalCompressedSize == 0)
        {
            dict.GlossaryBlocks = [];
            dict.EntryDataOffset = 8;
            return;
        }

        int pos = 8;
        int end = 8 + (int)totalCompressedSize;

        var blocks = new List<GlossaryBlock>();
        long cumulativeRaw = 0;
        while (pos < end)
        {
            int uncompSize = BinaryPrimitives.ReadInt32LittleEndian(dict.BlobSpan(pos, 4));
            int compSize = BinaryPrimitives.ReadInt32LittleEndian(dict.BlobSpan(pos + 4, 4));
            blocks.Add(new GlossaryBlock
            {
                CumulativeRawOffset = cumulativeRaw,
                CompressedDataOffset = pos + 8,
                CompressedSize = compSize,
                UncompressedSize = uncompSize
            });
            cumulativeRaw += uncompSize;
            pos += 8 + compSize;
        }

        dict.GlossaryBlocks = blocks.ToArray();
        dict.EntryDataOffset = (ulong)end;
    }

    private static string DecompressGlossary(LoadedDict dict, ulong glossaryOffset, uint glossaryRawSize)
    {
        if (glossaryRawSize == 0 || dict.GlossaryBlocks.Length == 0)
            return "";

        var blocks = dict.GlossaryBlocks;
        int blockIdx = FindGlossaryBlock(blocks, (long)glossaryOffset);
        if (blockIdx < 0) return "";

        ref var block = ref blocks[blockIdx];
        int offsetInBlock = (int)((long)glossaryOffset - block.CumulativeRawOffset);

        byte[] buf = GetOrDecompressBlock(dict, blockIdx);

        if (offsetInBlock + (int)glossaryRawSize <= block.UncompressedSize)
            return Encoding.UTF8.GetString(buf, offsetInBlock, (int)glossaryRawSize);

        int firstPartLen = block.UncompressedSize - offsetInBlock;
        int remaining = (int)glossaryRawSize - firstPartLen;
        byte[] result = new byte[glossaryRawSize];
        Buffer.BlockCopy(buf, offsetInBlock, result, 0, firstPartLen);

        int resultPos = firstPartLen;
        int nextBlock = blockIdx + 1;
        while (remaining > 0 && nextBlock < blocks.Length)
        {
            byte[] nbBuf = GetOrDecompressBlock(dict, nextBlock);
            ref var nb = ref blocks[nextBlock];
            int toCopy = Math.Min(remaining, nb.UncompressedSize);
            Buffer.BlockCopy(nbBuf, 0, result, resultPos, toCopy);
            resultPos += toCopy;
            remaining -= toCopy;
            nextBlock++;
        }

        return Encoding.UTF8.GetString(result);
    }

    private static byte[] GetOrDecompressBlock(LoadedDict dict, int blockIdx)
    {
        if (dict.BlockCache.TryGetValue(blockIdx, out byte[]? cached))
            return cached;

        ref var block = ref dict.GlossaryBlocks[blockIdx];
        byte[] buf = new byte[block.UncompressedSize];
        LZ4Codec.Decode(
            dict.BlobSpan(block.CompressedDataOffset, block.CompressedSize),
            buf.AsSpan(0, block.UncompressedSize));

        if (dict.BlockCache.Count >= LoadedDict.BlockCacheCapacity)
        {
            int evict = dict.BlockCacheOrder.Dequeue();
            dict.BlockCache.Remove(evict);
        }
        dict.BlockCache[blockIdx] = buf;
        dict.BlockCacheOrder.Enqueue(blockIdx);
        return buf;
    }

    private static int FindGlossaryBlock(GlossaryBlock[] blocks, long rawOffset)
    {
        int lo = 0, hi = blocks.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            long blockStart = blocks[mid].CumulativeRawOffset;
            long blockEnd = blockStart + blocks[mid].UncompressedSize;
            if (rawOffset < blockStart) hi = mid - 1;
            else if (rawOffset >= blockEnd) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    private static Dictionary<string, (uint Size, uint Offset)> BuildMediaIndex(MemoryMappedViewAccessor accessor, long length)
    {
        var index = new Dictionary<string, (uint, uint)>();
        long pos = 0;
        byte[] pathBuf = new byte[512];
        while (pos < length)
        {
            ushort pathLen = accessor.ReadUInt16(pos);
            pos += 2;
            byte[] pathBytes = pathLen <= 512 ? pathBuf : new byte[pathLen];
            accessor.ReadArray(pos, pathBytes, 0, pathLen);
            string mediaPath = Encoding.UTF8.GetString(pathBytes, 0, pathLen);
            pos += pathLen;
            uint blobLen = accessor.ReadUInt32(pos);
            pos += 4;
            index[mediaPath] = (blobLen, (uint)pos);
            pos += blobLen;
        }
        return index;
    }

    public List<TermResult> Query(string expression)
    {
        var termMap = new Dictionary<(string, string), TermResult>();

        int maxBytes = Encoding.UTF8.GetMaxByteCount(expression.Length);
        Span<byte> exprBuf = maxBytes <= 256 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        int bytesWritten = Encoding.UTF8.GetBytes(expression.AsSpan(), exprBuf);
        var exprBytes = exprBuf[..bytesWritten];

        foreach (var dict in _termDicts)
        {
            ulong hash = dict.Phf.Lookup(exprBytes);
            if (hash >= (ulong)dict.Offsets.Length) continue;
            ulong offsetAddr = dict.Offsets[hash];
            if (offsetAddr >= (ulong)dict.BlobsLength) continue;

            ReadEntries(dict, offsetAddr, expression, exprBytes, termMap);
        }

        var results = termMap.Values.ToList();
        if (_freqDicts.Count > 0) QueryFreq(results);
        if (_pitchDicts.Count > 0) QueryPitch(results);
        return results;
    }

    private static void ReadEntries(LoadedDict dict, ulong offsetAddr, string expression, ReadOnlySpan<byte> exprBytes,
        Dictionary<(string, string), TermResult> termMap)
    {
        int pos = (int)offsetAddr;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(dict.BlobSpan(pos, 4));
        pos += 4;

        ulong prevPairHash = 0;
        string? cachedExpr = null;
        string? cachedReading = null;

        for (uint i = 0; i < count; i++)
        {
            ulong entryOffset = BinaryPrimitives.ReadUInt64LittleEndian(dict.BlobSpan(pos, 8));
            pos += 8;

            int ep = (int)entryOffset;
            byte type = dict.BlobsPtr[ep++];
            if (type != 0) continue;

            ushort exprLen = BinaryPrimitives.ReadUInt16LittleEndian(dict.BlobSpan(ep, 2));
            ep += 2;
            var exprSpan = dict.BlobSpan(ep, exprLen);
            ep += exprLen;

            ushort readingLen = BinaryPrimitives.ReadUInt16LittleEndian(dict.BlobSpan(ep, 2));
            ep += 2;
            var readingSpan = dict.BlobSpan(ep, readingLen);
            ep += readingLen;

            bool exprMatch = exprSpan.SequenceEqual(exprBytes);
            if (!exprMatch && !readingSpan.SequenceEqual(exprBytes))
                continue;

            ulong pairHash = XxHash3.HashToUInt64(exprSpan) ^ (XxHash3.HashToUInt64(readingSpan) * 0x9E3779B97F4A7C15);
            if (pairHash != prevPairHash || cachedExpr == null)
            {
                cachedExpr = Encoding.UTF8.GetString(exprSpan);
                cachedReading = Encoding.UTF8.GetString(readingSpan);
                prevPairHash = pairHash;
            }

            ulong glossaryOffset = BinaryPrimitives.ReadUInt64LittleEndian(dict.BlobSpan(ep, 8));
            ep += 8;
            uint glossaryRawSize = BinaryPrimitives.ReadUInt32LittleEndian(dict.BlobSpan(ep, 4));
            ep += 4;

            string glossary = DecompressGlossary(dict, glossaryOffset, glossaryRawSize);

            byte defTagsLen = dict.BlobsPtr[ep++];
            string defTags = Encoding.UTF8.GetString(dict.BlobSpan(ep, defTagsLen));
            ep += defTagsLen;

            byte rulesLen = dict.BlobsPtr[ep++];
            string rules = Encoding.UTF8.GetString(dict.BlobSpan(ep, rulesLen));
            ep += rulesLen;

            byte termTagsLen = dict.BlobsPtr[ep++];
            string termTags = Encoding.UTF8.GetString(dict.BlobSpan(ep, termTagsLen));

            var key = (cachedExpr, cachedReading!);
            if (!termMap.TryGetValue(key, out var term))
            {
                term = new TermResult { Expression = cachedExpr, Reading = cachedReading!, Rules = rules };
                termMap[key] = term;
            }
            else if (!string.IsNullOrEmpty(rules))
            {
                if (!string.IsNullOrEmpty(term.Rules))
                    term.Rules += " ";
                term.Rules += rules;
            }

            term.Glossaries.Add(new GlossaryEntry(dict.Name, glossary, defTags, termTags));
        }
    }

    public void QueryFreq(List<TermResult> terms)
    {
        Span<byte> stackBuf = stackalloc byte[256];
        foreach (var term in terms)
        {
            int maxBytes = Encoding.UTF8.GetMaxByteCount(term.Expression.Length);
            Span<byte> exprBuf = maxBytes <= 256 ? stackBuf : new byte[maxBytes];
            int bytesWritten = Encoding.UTF8.GetBytes(term.Expression.AsSpan(), exprBuf);
            var exprBytes = exprBuf[..bytesWritten];

            foreach (var dict in _freqDicts)
            {
                if (!TryLookupOffsetAddr(dict, exprBytes, out ulong offsetAddr)) continue;

                var frequencies = new List<Frequency>();
                int pos = (int)offsetAddr;
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(dict.BlobSpan(pos, 4));
                pos += 4;

                for (uint i = 0; i < count; i++)
                {
                    ulong entryOffset = BinaryPrimitives.ReadUInt64LittleEndian(dict.BlobSpan(pos, 8));
                    pos += 8;

                    if (!TryReadMetaEntryHeader(dict, (int)entryOffset, exprBytes, "freq"u8, out int dataPos))
                        continue;

                    uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(dict.BlobSpan(dataPos, 4));
                    var freqData = dict.BlobSpan(dataPos + 4, (int)dataLen);

                    if (TryParseFrequency(freqData, term.Reading, out var freq))
                        frequencies.Add(freq);
                }

                if (frequencies.Count > 0)
                    term.Frequencies.Add(new FrequencyEntry(dict.Name, frequencies));
            }
        }
    }

    public void QueryPitch(List<TermResult> terms)
    {
        Span<byte> stackBuf = stackalloc byte[256];
        foreach (var term in terms)
        {
            int maxBytes = Encoding.UTF8.GetMaxByteCount(term.Expression.Length);
            Span<byte> exprBuf = maxBytes <= 256 ? stackBuf : new byte[maxBytes];
            int bytesWritten = Encoding.UTF8.GetBytes(term.Expression.AsSpan(), exprBuf);
            var exprBytes = exprBuf[..bytesWritten];

            foreach (var dict in _pitchDicts)
            {
                if (!TryLookupOffsetAddr(dict, exprBytes, out ulong offsetAddr)) continue;

                var pitchPositions = new List<int>();
                int pos = (int)offsetAddr;
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(dict.BlobSpan(pos, 4));
                pos += 4;

                for (uint i = 0; i < count; i++)
                {
                    ulong entryOffset = BinaryPrimitives.ReadUInt64LittleEndian(dict.BlobSpan(pos, 8));
                    pos += 8;

                    if (!TryReadMetaEntryHeader(dict, (int)entryOffset, exprBytes, "pitch"u8, out int dataPos))
                        continue;

                    uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(dict.BlobSpan(dataPos, 4));
                    var pitchData = dict.BlobSpan(dataPos + 4, (int)dataLen);

                    if (TryParsePitch(pitchData, term.Reading, out var positions))
                        pitchPositions.AddRange(positions);
                }

                if (pitchPositions.Count > 0)
                    term.Pitches.Add(new PitchEntry(dict.Name, pitchPositions));
            }
        }
    }

    private static bool TryLookupOffsetAddr(LoadedDict dict, ReadOnlySpan<byte> exprBytes, out ulong offsetAddr)
    {
        ulong hash = dict.Phf.Lookup(exprBytes);
        if (hash >= (ulong)dict.Offsets.Length) { offsetAddr = 0; return false; }
        offsetAddr = dict.Offsets[hash];
        return offsetAddr < (ulong)dict.BlobsLength;
    }

    private static bool TryReadMetaEntryHeader(LoadedDict dict, int entryPos, ReadOnlySpan<byte> exprBytes,
        ReadOnlySpan<byte> modeFilter, out int dataPos)
    {
        dataPos = 0;
        int ep = entryPos;
        byte type = dict.BlobsPtr[ep++];
        if (type != 1) return false;

        ushort exprLen = BinaryPrimitives.ReadUInt16LittleEndian(dict.BlobSpan(ep, 2));
        ep += 2;
        if (!dict.BlobSpan(ep, exprLen).SequenceEqual(exprBytes)) return false;
        ep += exprLen;

        byte modeLen = dict.BlobsPtr[ep++];
        if (!dict.BlobSpan(ep, modeLen).SequenceEqual(modeFilter)) return false;
        ep += modeLen;

        dataPos = ep;
        return true;
    }

    private static bool TryParseFrequency(ReadOnlySpan<byte> data, string termReading, out Frequency result)
    {
        result = default!;
        try
        {
            var reader = new Utf8JsonReader(data);

            if (!reader.Read()) return false;

            // Plain integer
            if (reader.TokenType == JsonTokenType.Number)
            {
                int val = reader.GetInt32();
                result = new Frequency(val, val.ToString());
                return true;
            }

            if (reader.TokenType != JsonTokenType.StartObject) return false;

            string? reading = null;
            int? value = null;
            string? displayValue = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                string prop = reader.GetString()!;
                reader.Read();

                switch (prop)
                {
                    case "reading":
                        reading = reader.GetString();
                        break;
                    case "value":
                        value = reader.GetInt32();
                        break;
                    case "displayValue":
                        displayValue = reader.GetString();
                        break;
                    case "frequency":
                        if (reader.TokenType == JsonTokenType.Number)
                        {
                            value = reader.GetInt32();
                        }
                        else if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                                string fp = reader.GetString()!;
                                reader.Read();
                                switch (fp)
                                {
                                    case "value": value = reader.GetInt32(); break;
                                    case "displayValue": displayValue = reader.GetString(); break;
                                    default: reader.Skip(); break;
                                }
                            }
                        }
                        else
                        {
                            reader.Skip();
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (!string.IsNullOrEmpty(reading) && reading != termReading)
                return false;

            int v = value ?? 0;
            result = new Frequency(v, displayValue ?? v.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParsePitch(ReadOnlySpan<byte> data, string termReading, out List<int> positions)
    {
        positions = [];
        try
        {
            var reader = new Utf8JsonReader(data);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            string? reading = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                string prop = reader.GetString()!;
                reader.Read();

                switch (prop)
                {
                    case "reading":
                        reading = reader.GetString();
                        break;
                    case "pitches":
                        if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            {
                                if (reader.TokenType == JsonTokenType.StartObject)
                                {
                                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                                    {
                                        if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "position")
                                        {
                                            reader.Read();
                                            positions.Add(reader.GetInt32());
                                        }
                                        else if (reader.TokenType == JsonTokenType.PropertyName)
                                        {
                                            reader.Read();
                                            reader.Skip();
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            reader.Skip();
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (!string.IsNullOrEmpty(reading) && reading != termReading)
            {
                positions = [];
                return false;
            }

            return positions.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public byte[]? GetMediaFile(string dictName, string mediaPath)
    {
        foreach (var dict in _termDicts)
        {
            if (dict.Name != dictName || dict.MediaIndex == null || dict.MediaAccessor == null)
                continue;

            if (dict.MediaIndex.TryGetValue(mediaPath, out var entry))
            {
                var result = new byte[entry.Size];
                dict.MediaAccessor.ReadArray(entry.Offset, result, 0, (int)entry.Size);
                return result;
            }
        }
        return null;
    }

    public List<(string Name, string Css)> GetStyles()
    {
        return _termDicts
            .Where(d => !string.IsNullOrEmpty(d.Styles))
            .Select(d => (d.Name, d.Styles!))
            .ToList();
    }

    public List<string> GetFreqDictOrder()
    {
        return _freqDicts.Select(d => d.Name).ToList();
    }

    public void Dispose()
    {
        foreach (var dict in _termDicts) dict.Dispose();
        foreach (var dict in _freqDicts) dict.Dispose();
        foreach (var dict in _pitchDicts) dict.Dispose();
    }
}
