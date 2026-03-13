using System.Buffers.Binary;
using System.IO.Hashing;
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

public sealed class DictionaryQuery
{
    private sealed class LoadedDict
    {
        public string Name = "";
        public string? Styles;
        public Mphf Phf = new();
        public byte[] Blobs = [];
        public ulong[] Offsets = [];
        public byte[] DecompressedGlossary = [];
        public ulong EntryDataOffset;
        public byte[]? Media;
        public Dictionary<string, (uint Size, uint Offset)>? MediaIndex;
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

        dict.Blobs = File.ReadAllBytes(Path.Combine(path, "blobs.bin"));
        DecompressGlossarySection(dict);

        string mediaPath = Path.Combine(path, "media.bin");
        if (File.Exists(mediaPath))
        {
            dict.Media = File.ReadAllBytes(mediaPath);
            dict.MediaIndex = BuildMediaIndex(dict.Media);
        }

        target.Add(dict);
    }

    private static void DecompressGlossarySection(LoadedDict dict)
    {
        if (dict.Blobs.Length < 8) return;

        ulong totalCompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(dict.Blobs);
        if (totalCompressedSize == 0)
        {
            dict.DecompressedGlossary = [];
            dict.EntryDataOffset = 8;
            return;
        }

        int pos = 8;
        int end = 8 + (int)totalCompressedSize;

        int totalDecompressed = 0;
        int scanPos = pos;
        while (scanPos < end)
        {
            int uncompSize = BinaryPrimitives.ReadInt32LittleEndian(dict.Blobs.AsSpan(scanPos));
            int compSize = BinaryPrimitives.ReadInt32LittleEndian(dict.Blobs.AsSpan(scanPos + 4));
            totalDecompressed += uncompSize;
            scanPos += 8 + compSize;
        }

        dict.DecompressedGlossary = new byte[totalDecompressed];
        int decompPos = 0;

        while (pos < end)
        {
            int uncompSize = BinaryPrimitives.ReadInt32LittleEndian(dict.Blobs.AsSpan(pos));
            int compSize = BinaryPrimitives.ReadInt32LittleEndian(dict.Blobs.AsSpan(pos + 4));
            pos += 8;

            LZ4Codec.Decode(
                dict.Blobs.AsSpan(pos, compSize),
                dict.DecompressedGlossary.AsSpan(decompPos, uncompSize));

            decompPos += uncompSize;
            pos += compSize;
        }

        dict.EntryDataOffset = (ulong)end;
    }

    private static Dictionary<string, (uint Size, uint Offset)> BuildMediaIndex(byte[] media)
    {
        var index = new Dictionary<string, (uint, uint)>();
        int pos = 0;
        while (pos < media.Length)
        {
            ushort pathLen = BinaryPrimitives.ReadUInt16LittleEndian(media.AsSpan(pos));
            pos += 2;
            string mediaPath = Encoding.UTF8.GetString(media, pos, pathLen);
            pos += pathLen;
            uint blobLen = BinaryPrimitives.ReadUInt32LittleEndian(media.AsSpan(pos));
            pos += 4;
            index[mediaPath] = (blobLen, (uint)pos);
            pos += (int)blobLen;
        }
        return index;
    }

    public List<TermResult> Query(string expression)
    {
        var termMap = new Dictionary<(string, string), TermResult>();
        byte[] exprBytes = Encoding.UTF8.GetBytes(expression);

        foreach (var dict in _termDicts)
        {
            ulong hash = dict.Phf.Lookup(exprBytes);
            if (hash >= (ulong)dict.Offsets.Length) continue;
            ulong offsetAddr = dict.Offsets[hash];
            if (offsetAddr >= (ulong)dict.Blobs.Length) continue;

            ReadEntries(dict, offsetAddr, expression, exprBytes, termMap);
        }

        var results = termMap.Values.ToList();
        QueryFreq(results);
        QueryPitch(results);
        return results;
    }

    private static void ReadEntries(LoadedDict dict, ulong offsetAddr, string expression, byte[] exprBytes,
        Dictionary<(string, string), TermResult> termMap)
    {
        int pos = (int)offsetAddr;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(dict.Blobs.AsSpan(pos));
        pos += 4;

        for (uint i = 0; i < count; i++)
        {
            ulong entryOffset = BinaryPrimitives.ReadUInt64LittleEndian(dict.Blobs.AsSpan(pos));
            pos += 8;

            int ep = (int)entryOffset;
            byte type = dict.Blobs[ep++];
            if (type != 0) continue;

            ushort exprLen = BinaryPrimitives.ReadUInt16LittleEndian(dict.Blobs.AsSpan(ep));
            ep += 2;
            var exprSpan = dict.Blobs.AsSpan(ep, exprLen);
            ep += exprLen;

            ushort readingLen = BinaryPrimitives.ReadUInt16LittleEndian(dict.Blobs.AsSpan(ep));
            ep += 2;
            var readingSpan = dict.Blobs.AsSpan(ep, readingLen);
            ep += readingLen;

            bool exprMatch = exprSpan.SequenceEqual(exprBytes);
            if (!exprMatch && !readingSpan.SequenceEqual(exprBytes))
                continue;

            string expr = Encoding.UTF8.GetString(exprSpan);
            string reading = Encoding.UTF8.GetString(readingSpan);

            ulong glossaryOffset = BinaryPrimitives.ReadUInt64LittleEndian(dict.Blobs.AsSpan(ep));
            ep += 8;
            uint glossaryRawSize = BinaryPrimitives.ReadUInt32LittleEndian(dict.Blobs.AsSpan(ep));
            ep += 4;

            string glossary = "";
            if (glossaryRawSize > 0 && glossaryOffset + glossaryRawSize <= (ulong)dict.DecompressedGlossary.Length)
                glossary = Encoding.UTF8.GetString(dict.DecompressedGlossary, (int)glossaryOffset, (int)glossaryRawSize);

            byte defTagsLen = dict.Blobs[ep++];
            string defTags = Encoding.UTF8.GetString(dict.Blobs, ep, defTagsLen);
            ep += defTagsLen;

            byte rulesLen = dict.Blobs[ep++];
            string rules = Encoding.UTF8.GetString(dict.Blobs, ep, rulesLen);
            ep += rulesLen;

            byte termTagsLen = dict.Blobs[ep++];
            string termTags = Encoding.UTF8.GetString(dict.Blobs, ep, termTagsLen);

            var key = (expr, reading);
            if (!termMap.TryGetValue(key, out var term))
            {
                term = new TermResult { Expression = expr, Reading = reading, Rules = rules };
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
        foreach (var term in terms)
        {
            byte[] exprBytes = Encoding.UTF8.GetBytes(term.Expression);

            foreach (var dict in _freqDicts)
            {
                if (!TryLookupOffsetAddr(dict, exprBytes, out ulong offsetAddr)) continue;

                var frequencies = new List<Frequency>();
                int pos = (int)offsetAddr;
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(dict.Blobs.AsSpan(pos));
                pos += 4;

                for (uint i = 0; i < count; i++)
                {
                    ulong entryOffset = BinaryPrimitives.ReadUInt64LittleEndian(dict.Blobs.AsSpan(pos));
                    pos += 8;

                    if (!TryReadMetaEntryHeader(dict, (int)entryOffset, term.Expression, "freq"u8, out int dataPos))
                        continue;

                    uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(dict.Blobs.AsSpan(dataPos));
                    var freqData = dict.Blobs.AsSpan(dataPos + 4, (int)dataLen);

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
        foreach (var term in terms)
        {
            byte[] exprBytes = Encoding.UTF8.GetBytes(term.Expression);

            foreach (var dict in _pitchDicts)
            {
                if (!TryLookupOffsetAddr(dict, exprBytes, out ulong offsetAddr)) continue;

                var pitchPositions = new List<int>();
                int pos = (int)offsetAddr;
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(dict.Blobs.AsSpan(pos));
                pos += 4;

                for (uint i = 0; i < count; i++)
                {
                    ulong entryOffset = BinaryPrimitives.ReadUInt64LittleEndian(dict.Blobs.AsSpan(pos));
                    pos += 8;

                    if (!TryReadMetaEntryHeader(dict, (int)entryOffset, term.Expression, "pitch"u8, out int dataPos))
                        continue;

                    uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(dict.Blobs.AsSpan(dataPos));
                    var pitchData = dict.Blobs.AsSpan(dataPos + 4, (int)dataLen);

                    if (TryParsePitch(pitchData, term.Reading, out var positions))
                        pitchPositions.AddRange(positions);
                }

                if (pitchPositions.Count > 0)
                    term.Pitches.Add(new PitchEntry(dict.Name, pitchPositions));
            }
        }
    }

    private static bool TryLookupOffsetAddr(LoadedDict dict, byte[] exprBytes, out ulong offsetAddr)
    {
        ulong hash = dict.Phf.Lookup(exprBytes);
        if (hash >= (ulong)dict.Offsets.Length) { offsetAddr = 0; return false; }
        offsetAddr = dict.Offsets[hash];
        return offsetAddr < (ulong)dict.Blobs.Length;
    }

    private static bool TryReadMetaEntryHeader(LoadedDict dict, int entryPos, string expression,
        ReadOnlySpan<byte> modeFilter, out int dataPos)
    {
        dataPos = 0;
        int ep = entryPos;
        byte type = dict.Blobs[ep++];
        if (type != 1) return false;

        ushort exprLen = BinaryPrimitives.ReadUInt16LittleEndian(dict.Blobs.AsSpan(ep));
        ep += 2;
        string expr = Encoding.UTF8.GetString(dict.Blobs, ep, exprLen);
        ep += exprLen;
        if (expr != expression) return false;

        byte modeLen = dict.Blobs[ep++];
        if (!dict.Blobs.AsSpan(ep, modeLen).SequenceEqual(modeFilter)) return false;
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
            if (dict.Name != dictName || dict.MediaIndex == null || dict.Media == null)
                continue;

            if (dict.MediaIndex.TryGetValue(mediaPath, out var entry))
            {
                var result = new byte[entry.Size];
                Array.Copy(dict.Media, entry.Offset, result, 0, entry.Size);
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
}
