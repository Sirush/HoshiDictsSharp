using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using K4os.Compression.LZ4;

namespace HoshiDictsSharp;

public static class DictionaryImporter
{
    private sealed class ProcessedFile
    {
        public byte[] Data = [];
        public int DataLength;
        public List<(ulong Hash, ulong Offset)> FlatOffsets = [];

        public byte[] GlossaryRaw = [];
        public int GlossaryRawLen;
        public Dictionary<ulong, (int Offset, int Length)> GlossaryIndex = new();
        public List<(int Position, int RawOffset)> GlossaryPatches = [];

        public byte[] CompressedGlossary = [];
        public int CompressedGlossaryLen;

        public int Count;

        public byte[]? RentedContent;

        public void ReturnAll()
        {
            var pool = ArrayPool<byte>.Shared;
            if (RentedContent != null) { pool.Return(RentedContent); RentedContent = null; }
            ReturnIfPooled(pool, ref Data);
            ReturnIfPooled(pool, ref GlossaryRaw);
            ReturnIfPooled(pool, ref CompressedGlossary);
        }

        private static void ReturnIfPooled(ArrayPool<byte> pool, ref byte[] buf)
        {
            if (buf.Length > 0) { pool.Return(buf); buf = []; }
        }

        public void CompressGlossaries()
        {
            if (GlossaryRawLen == 0) return;

            int maxCompressed = LZ4Codec.MaximumOutputSize(GlossaryRawLen) + (GlossaryRawLen / (64 * 1024) + 1) * 8;
            CompressedGlossary = ArrayPool<byte>.Shared.Rent(maxCompressed);
            int compPos = 0;
            int rawPos = 0;
            int blockSize = 64 * 1024;

            while (rawPos < GlossaryRawLen)
            {
                int chunkLen = Math.Min(blockSize, GlossaryRawLen - rawPos);
                int maxOut = LZ4Codec.MaximumOutputSize(chunkLen);
                EnsurePooled(ref CompressedGlossary, compPos, maxOut + 8);

                BinaryPrimitives.WriteInt32LittleEndian(CompressedGlossary.AsSpan(compPos), chunkLen);
                compPos += 4;

                int compLen = LZ4Codec.Encode(
                    GlossaryRaw.AsSpan(rawPos, chunkLen),
                    CompressedGlossary.AsSpan(compPos + 4));

                BinaryPrimitives.WriteInt32LittleEndian(CompressedGlossary.AsSpan(compPos), compLen);
                compPos += 4 + compLen;

                rawPos += chunkLen;
            }

            CompressedGlossaryLen = compPos;

            ArrayPool<byte>.Shared.Return(GlossaryRaw);
            GlossaryRaw = [];
        }
    }

    public static ImportResult Import(string zipPath, string outputDir)
    {
        var result = new ImportResult();

        try
        {
            byte[] zipBytes = File.ReadAllBytes(zipPath);
            using var zipStream = new MemoryStream(zipBytes, writable: false);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            byte[] indexContent = ReadEntry(archive, "index.json");
            if (indexContent.Length == 0)
                throw new InvalidOperationException("could not find or read index.json");

            result.Title = YomitanParser.ParseIndexTitle(indexContent);
            if (string.IsNullOrEmpty(result.Title))
                throw new InvalidOperationException("failed to parse index.json title");

            string dictPath = Path.Combine(outputDir, result.Title);
            Directory.CreateDirectory(dictPath);

            string serializedIndex = YomitanParser.SerializeIndex(indexContent);
            File.WriteAllText(Path.Combine(dictPath, "index.json"), serializedIndex);

            byte[] styles = ReadEntry(archive, "styles.css");
            if (styles.Length > 0)
                File.WriteAllBytes(Path.Combine(dictPath, "styles.css"), styles);

            var termBanks = new List<ZipArchiveEntry>();
            var metaBanks = new List<ZipArchiveEntry>();
            var mediaEntries = new List<ZipArchiveEntry>();

            foreach (var entry in archive.Entries)
            {
                string name = entry.Name;
                if (entry.Length == 0 && string.IsNullOrEmpty(name))
                    continue;

                if (name.StartsWith("term_bank_", StringComparison.Ordinal))
                    termBanks.Add(entry);
                else if (name.StartsWith("term_meta_bank_", StringComparison.Ordinal))
                    metaBanks.Add(entry);
                else if (name.StartsWith("tag_bank_", StringComparison.Ordinal))
                    { }
                else if (name is not "styles.css" and not "index.json" && !string.IsNullOrEmpty(name) && entry.Length > 0)
                    mediaEntries.Add(entry);
            }

            using var blobsStream = new FileStream(Path.Combine(dictPath, "blobs.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var allFlatOffsets = new List<(ulong Hash, ulong GlobalOffset)>();
            ulong writeOffset = 0;

            writeOffset = WriteTermBanks(blobsStream, allFlatOffsets, termBanks, writeOffset, result);
            writeOffset = WriteMetaBanks(blobsStream, allFlatOffsets, metaBanks, writeOffset, result);

            if (allFlatOffsets.Count == 0)
                throw new InvalidOperationException("empty dictionary");

            CollectionsMarshal.AsSpan(allFlatOffsets).Sort();

            var flatSpan = CollectionsMarshal.AsSpan(allFlatOffsets);
            int uniqueKeys = 0;
            int totalOffsetBytes = 0;
            {
                int i = 0;
                while (i < flatSpan.Length)
                {
                    ulong hash = flatSpan[i].Hash;
                    int start = i;
                    while (i < flatSpan.Length && flatSpan[i].Hash == hash) i++;
                    uniqueKeys++;
                    totalOffsetBytes += 4 + (i - start) * 8;
                }
            }

            var uniqueHashes = new ulong[uniqueKeys];
            var keyOffsets = new ulong[uniqueKeys];
            var offsetData = new byte[totalOffsetBytes];
            int offsetPos = 0;
            int keyIdx = 0;

            {
                int i = 0;
                while (i < flatSpan.Length)
                {
                    ulong hash = flatSpan[i].Hash;
                    int start = i;
                    while (i < flatSpan.Length && flatSpan[i].Hash == hash) i++;

                    uniqueHashes[keyIdx] = hash;
                    keyOffsets[keyIdx] = writeOffset;
                    keyIdx++;

                    BinaryPrimitives.WriteUInt32LittleEndian(offsetData.AsSpan(offsetPos), (uint)(i - start));
                    offsetPos += 4;

                    for (int j = start; j < i; j++)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(offsetData.AsSpan(offsetPos), flatSpan[j].GlobalOffset);
                        offsetPos += 8;
                    }

                    writeOffset += (uint)(4 + (i - start) * 8);
                }
            }

            blobsStream.Write(offsetData, 0, offsetPos);
            blobsStream.Flush();

            var phf = new Mphf();
            phf.BuildFromSortedHashes(uniqueHashes);
            phf.Save(Path.Combine(dictPath, "hash.mph"));

            using (var offsStream = new FileStream(Path.Combine(dictPath, "offsets.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                offsStream.Write(MemoryMarshal.AsBytes(keyOffsets.AsSpan()));
            }

            WriteMedia(dictPath, mediaEntries, result);

            using (var marker = new FileStream(Path.Combine(dictPath, ".hoshidicts_1"), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                marker.WriteByte(phf.Type);
            }

            result.Success = true;
        }
        catch (Exception e)
        {
            result.Success = false;
            result.Errors.Add(e.Message);

            if (!string.IsNullOrEmpty(result.Title))
            {
                string failedPath = Path.Combine(outputDir, result.Title);
                if (Directory.Exists(failedPath))
                    Directory.Delete(failedPath, true);
            }
        }

        return result;
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return [];

        int len = (int)entry.Length;
        var buf = new byte[len];
        using var stream = entry.Open();
        ReadFully(stream, buf, len);
        return buf;
    }

    private static (byte[] Buffer, int Length) ReadEntryRented(ZipArchiveEntry entry)
    {
        int len = (int)entry.Length;
        byte[] buf = ArrayPool<byte>.Shared.Rent(len);
        using var stream = entry.Open();
        int read = ReadFully(stream, buf, len);
        return (buf, read);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadFully(Stream stream, byte[] buffer, int length)
    {
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(buffer, read, length - read);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    private static ProcessedFile ProcessTermBank(byte[] content, int contentLength)
    {
        var processed = new ProcessedFile();
        processed.RentedContent = content;
        if (contentLength == 0) return processed;

        int estimatedTerms = Math.Max(64, contentLength / 200);

        var data = ArrayPool<byte>.Shared.Rent(contentLength);
        int pos = 0;

        var glossaryRaw = ArrayPool<byte>.Shared.Rent(contentLength / 2);
        int glossaryRawPos = 0;

        var flatOffsets = new List<(ulong Hash, ulong Offset)>(estimatedTerms);
        var glossaryIndex = new Dictionary<ulong, (int Offset, int Length)>(estimatedTerms / 2);
        var glossaryPatches = new List<(int Position, int RawOffset)>(estimatedTerms);
        processed.GlossaryIndex = glossaryIndex;

        YomitanParser.ParseTermBank(content, contentLength, (in YomitanParser.TermFields term) =>
        {
            ReadOnlySpan<byte> glossary = content.AsSpan(term.GlossaryStart, term.GlossaryLen);
            ulong glossaryHash = XxHash3.HashToUInt64(glossary);

            ref var glossaryEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(
                glossaryIndex, glossaryHash, out bool glossaryExists);

            if (!glossaryExists)
            {
                EnsurePooled(ref glossaryRaw, glossaryRawPos, glossary.Length);
                glossary.CopyTo(glossaryRaw.AsSpan(glossaryRawPos));
                glossaryEntry = (glossaryRawPos, glossary.Length);
                glossaryRawPos += glossary.Length;
            }

            int glossarySize = glossaryEntry.Length;
            ReadOnlySpan<byte> expr = content.AsSpan(term.ExprStart, term.ExprLen);
            ReadOnlySpan<byte> reading = term.ReadingLen == 0 ? expr : content.AsSpan(term.ReadingStart, term.ReadingLen);
            ReadOnlySpan<byte> defTags = content.AsSpan(term.DefTagsStart, term.DefTagsLen);
            ReadOnlySpan<byte> rules = content.AsSpan(term.RulesStart, term.RulesLen);
            ReadOnlySpan<byte> termTags = content.AsSpan(term.TermTagsStart, term.TermTagsLen);

            int binaryNeeded = 1 + 2 + expr.Length + 2 + reading.Length + 8 + 4 + 1 + defTags.Length + 1 + rules.Length + 1 + termTags.Length;
            EnsurePooled(ref data, pos, binaryNeeded);

            ulong offset = (ulong)pos;

            data[pos++] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos), (ushort)expr.Length);
            pos += 2;
            expr.CopyTo(data.AsSpan(pos));
            pos += expr.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos), (ushort)reading.Length);
            pos += 2;
            reading.CopyTo(data.AsSpan(pos));
            pos += reading.Length;

            int glossaryOffsetPos = pos;
            pos += 8;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), (uint)glossarySize);
            pos += 4;
            glossaryPatches.Add((glossaryOffsetPos, glossaryEntry.Offset));

            data[pos++] = (byte)defTags.Length;
            defTags.CopyTo(data.AsSpan(pos));
            pos += defTags.Length;
            data[pos++] = (byte)rules.Length;
            rules.CopyTo(data.AsSpan(pos));
            pos += rules.Length;
            data[pos++] = (byte)termTags.Length;
            termTags.CopyTo(data.AsSpan(pos));
            pos += termTags.Length;

            ulong exprHash = XxHash3.HashToUInt64(expr);
            flatOffsets.Add((exprHash, offset));

            if (term.ReadingLen > 0 && !reading.SequenceEqual(expr))
            {
                ulong readingHash = XxHash3.HashToUInt64(reading);
                flatOffsets.Add((readingHash, offset));
            }

            processed.Count++;
        });

        processed.Data = data;
        processed.DataLength = pos;
        processed.GlossaryRaw = glossaryRaw;
        processed.GlossaryRawLen = glossaryRawPos;
        processed.FlatOffsets = flatOffsets;
        processed.GlossaryPatches = glossaryPatches;

        processed.CompressGlossaries();

        return processed;
    }

    private static ProcessedFile ProcessMetaBank(byte[] content, int contentLength)
    {
        var processed = new ProcessedFile();
        processed.RentedContent = content;
        if (contentLength == 0) return processed;

        int estimatedTerms = Math.Max(64, contentLength / 200);
        var data = ArrayPool<byte>.Shared.Rent(contentLength);
        int pos = 0;

        var flatOffsets = new List<(ulong Hash, ulong Offset)>(estimatedTerms);

        YomitanParser.ParseMetaBank(content, contentLength, (in YomitanParser.MetaFields meta) =>
        {
            ReadOnlySpan<byte> expr = content.AsSpan(meta.ExprStart, meta.ExprLen);
            ReadOnlySpan<byte> mode = content.AsSpan(meta.ModeStart, meta.ModeLen);
            ReadOnlySpan<byte> metaData = content.AsSpan(meta.DataStart, meta.DataLen);

            int needed = 1 + 2 + expr.Length + 1 + mode.Length + 4 + metaData.Length;
            EnsurePooled(ref data, pos, needed);

            ulong offset = (ulong)pos;

            data[pos++] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos), (ushort)expr.Length);
            pos += 2;
            expr.CopyTo(data.AsSpan(pos));
            pos += expr.Length;
            data[pos++] = (byte)mode.Length;
            mode.CopyTo(data.AsSpan(pos));
            pos += mode.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), (uint)metaData.Length);
            pos += 4;
            metaData.CopyTo(data.AsSpan(pos));
            pos += metaData.Length;

            ulong exprHash = XxHash3.HashToUInt64(expr);
            flatOffsets.Add((exprHash, offset));

            processed.Count++;
        });

        processed.Data = data;
        processed.DataLength = pos;
        processed.FlatOffsets = flatOffsets;
        return processed;
    }

    private static ulong WriteTermBanks(FileStream file, List<(ulong Hash, ulong GlobalOffset)> allFlatOffsets,
        List<ZipArchiveEntry> entries, ulong writeOffset, ImportResult result)
    {
        if (entries.Count == 0) return writeOffset;

        var tasks = new Task<ProcessedFile>[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var (buf, len) = ReadEntryRented(entries[i]);
            tasks[i] = Task.Run(() => ProcessTermBank(buf, len));
        }

        Task.WaitAll(tasks);

        ulong totalCompressedSize = 0;
        ulong cumulativeUncompressedLen = 0;
        var bankCumulativeBases = new ulong[tasks.Length];
        int totalFlatOffsets = 0;

        for (int i = 0; i < tasks.Length; i++)
        {
            var p = tasks[i].Result;
            bankCumulativeBases[i] = cumulativeUncompressedLen;
            totalCompressedSize += (ulong)p.CompressedGlossaryLen;
            cumulativeUncompressedLen += (ulong)p.GlossaryRawLen;
            totalFlatOffsets += p.FlatOffsets.Count;
        }

        allFlatOffsets.EnsureCapacity(allFlatOffsets.Count + totalFlatOffsets);

        Span<byte> headerBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(headerBuf, totalCompressedSize);
        file.Write(headerBuf);
        writeOffset += 8;

        for (int i = 0; i < tasks.Length; i++)
        {
            var p = tasks[i].Result;
            if (p.CompressedGlossaryLen > 0)
            {
                file.Write(p.CompressedGlossary, 0, p.CompressedGlossaryLen);
                writeOffset += (ulong)p.CompressedGlossaryLen;
            }
        }

        for (int i = 0; i < tasks.Length; i++)
        {
            var p = tasks[i].Result;
            if (p.DataLength == 0) { p.ReturnAll(); continue; }

            ulong cumulativeBase = bankCumulativeBases[i];
            var patches = CollectionsMarshal.AsSpan(p.GlossaryPatches);
            for (int j = 0; j < patches.Length; j++)
            {
                var (patchPos, rawOffset) = patches[j];
                BinaryPrimitives.WriteUInt64LittleEndian(p.Data.AsSpan(patchPos), cumulativeBase + (ulong)rawOffset);
            }

            file.Write(p.Data, 0, p.DataLength);

            ulong dataBase = writeOffset;
            var flatOffsets = CollectionsMarshal.AsSpan(p.FlatOffsets);
            for (int j = 0; j < flatOffsets.Length; j++)
                allFlatOffsets.Add((flatOffsets[j].Hash, flatOffsets[j].Offset + dataBase));

            writeOffset += (ulong)p.DataLength;
            result.TermCount += p.Count;
            p.ReturnAll();
        }

        return writeOffset;
    }

    private static ulong WriteMetaBanks(FileStream file, List<(ulong Hash, ulong GlobalOffset)> allFlatOffsets,
        List<ZipArchiveEntry> entries, ulong writeOffset, ImportResult result)
    {
        if (entries.Count == 0) return writeOffset;

        var tasks = new Task<ProcessedFile>[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var (buf, len) = ReadEntryRented(entries[i]);
            tasks[i] = Task.Run(() => ProcessMetaBank(buf, len));
        }

        Task.WaitAll(tasks);

        int totalFlatOffsets = 0;
        for (int i = 0; i < tasks.Length; i++)
            totalFlatOffsets += tasks[i].Result.FlatOffsets.Count;
        allFlatOffsets.EnsureCapacity(allFlatOffsets.Count + totalFlatOffsets);

        for (int i = 0; i < tasks.Length; i++)
        {
            var p = tasks[i].Result;
            writeOffset = FlushMetaProcessed(p, file, allFlatOffsets, writeOffset, result);
            p.ReturnAll();
        }

        return writeOffset;
    }

    private static ulong FlushMetaProcessed(ProcessedFile processed, FileStream file,
        List<(ulong Hash, ulong GlobalOffset)> allFlatOffsets,
        ulong writeOffset, ImportResult result)
    {
        if (processed.DataLength == 0) return writeOffset;

        file.Write(processed.Data, 0, processed.DataLength);

        ulong dataBase = writeOffset;
        var flatOffsets = CollectionsMarshal.AsSpan(processed.FlatOffsets);
        for (int i = 0; i < flatOffsets.Length; i++)
            allFlatOffsets.Add((flatOffsets[i].Hash, flatOffsets[i].Offset + dataBase));

        writeOffset += (ulong)processed.DataLength;
        result.MetaCount += processed.Count;
        return writeOffset;
    }

    private static void WriteMedia(string dictPath, List<ZipArchiveEntry> mediaFiles, ImportResult result)
    {
        if (mediaFiles.Count == 0) return;

        using var mediaStream = new FileStream(Path.Combine(dictPath, "media.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var pool = ArrayPool<byte>.Shared;
        byte[] headerBuf = new byte[1024];

        foreach (var entry in mediaFiles)
        {
            int entryLen = (int)entry.Length;
            byte[] blob = pool.Rent(entryLen);

            using (var stream = entry.Open())
            {
                ReadFully(stream, blob, entryLen);
            }

            string path = entry.FullName;
            int pathByteCount = Encoding.UTF8.GetByteCount(path);
            int headerLen = 2 + pathByteCount + 4;
            if (headerBuf.Length < headerLen)
                headerBuf = new byte[headerLen * 2];

            BinaryPrimitives.WriteUInt16LittleEndian(headerBuf, (ushort)pathByteCount);
            Encoding.UTF8.GetBytes(path, headerBuf.AsSpan(2));
            BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(2 + pathByteCount), (uint)entryLen);

            mediaStream.Write(headerBuf, 0, headerLen);
            mediaStream.Write(blob, 0, entryLen);
            pool.Return(blob);
            result.MediaCount++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsurePooled(ref byte[] buffer, int pos, int needed)
    {
        if (pos + needed <= buffer.Length) return;
        int newSize = Math.Max(buffer.Length * 2, pos + needed);
        var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
        buffer.AsSpan(0, pos).CopyTo(newBuf);
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = newBuf;
    }
}
