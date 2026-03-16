using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;

namespace HoshiDictsSharp;

public static unsafe class DictionaryImporter
{
    private readonly struct BankEntry(int dataOffset, int compressedSize, int uncompressedSize, ushort compressionMethod)
    {
        public readonly int DataOffset = dataOffset;
        public readonly int CompressedSize = compressedSize;
        public readonly int UncompressedSize = uncompressedSize;
        public readonly ushort CompressionMethod = compressionMethod;
    }

    private readonly struct MediaEntry(int pathOffset, int pathLen, int dataOffset, int compressedSize, int uncompressedSize, ushort compressionMethod)
    {
        public readonly int PathOffset = pathOffset;
        public readonly int PathLen = pathLen;
        public readonly int DataOffset = dataOffset;
        public readonly int CompressedSize = compressedSize;
        public readonly int UncompressedSize = uncompressedSize;
        public readonly ushort CompressionMethod = compressionMethod;
    }

    private sealed class ProcessedFile
    {
        public byte[] Data = [];
        public int DataLength;
        public List<(ulong Hash, ulong Offset)> FlatOffsets = [];

        public byte[] GlossaryRaw = [];
        public int GlossaryRawLen;
        public List<(int Position, int RawOffset)> GlossaryPatches = [];

        public byte[] CompressedGlossary = [];
        public int CompressedGlossaryLen;

        public int Count;

        public void CompressGlossaries()
        {
            if (GlossaryRawLen == 0) return;

            int maxCompressed = LZ4Codec.MaximumOutputSize(GlossaryRawLen) + (GlossaryRawLen / (64 * 1024) + 1) * 8;
            CompressedGlossary = new byte[maxCompressed];
            int compPos = 0;
            int rawPos = 0;
            int blockSize = 64 * 1024;

            while (rawPos < GlossaryRawLen)
            {
                int chunkLen = Math.Min(blockSize, GlossaryRawLen - rawPos);
                int maxOut = LZ4Codec.MaximumOutputSize(chunkLen);
                EnsureCapacity(ref CompressedGlossary, compPos, maxOut + 8);

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
            GlossaryRaw = [];
        }
    }

    public static ImportResult Import(string zipPath, string outputDir)
    {
        var result = new ImportResult();

        try
        {
            byte[] indexContent = [];
            byte[] styles = [];
            var termBanks = new List<BankEntry>();
            var metaBanks = new List<BankEntry>();
            var mediaEntries = new List<MediaEntry>();
            string dictPath = "";
            FileStream? blobsStream = null;
            var allFlatOffsets = new List<(ulong Hash, ulong GlobalOffset)>();
            ulong writeOffset = 0;

            {
                int zipLength = (int)new FileInfo(zipPath).Length;
                var zipMmf = MemoryMappedFile.CreateFromFile(zipPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                var zipAccessor = zipMmf.CreateViewAccessor(0, zipLength, MemoryMappedFileAccess.Read);
                byte* zipBase = null;
                zipAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref zipBase);
                zipBase += zipAccessor.PointerOffset;

                ClassifyZipEntries(zipBase, zipLength, ref indexContent, ref styles, termBanks, metaBanks, mediaEntries);

                if (indexContent.Length == 0)
                    throw new InvalidOperationException("could not find or read index.json");

                result.Title = YomitanParser.ParseIndexTitle(indexContent);
                if (string.IsNullOrEmpty(result.Title))
                    throw new InvalidOperationException("failed to parse index.json title");

                dictPath = Path.Combine(outputDir, result.Title);
                Directory.CreateDirectory(dictPath);

                File.WriteAllText(Path.Combine(dictPath, "index.json"), YomitanParser.SerializeIndex(indexContent));

                if (styles.Length > 0)
                    File.WriteAllBytes(Path.Combine(dictPath, "styles.css"), styles);

                Task? mediaTask = mediaEntries.Count > 0
                    ? Task.Run(() => WriteMedia(dictPath, mediaEntries, zipBase, result))
                    : null;

                long totalBankBytes = 0;
                for (int i = 0; i < termBanks.Count; i++) totalBankBytes += termBanks[i].UncompressedSize;
                for (int i = 0; i < metaBanks.Count; i++) totalBankBytes += metaBanks[i].UncompressedSize;
                allFlatOffsets = new List<(ulong Hash, ulong GlobalOffset)>((int)(totalBankBytes / 130));

                long availableMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                int totalBanks = termBanks.Count + metaBanks.Count;
                int avgBankSize = totalBanks > 0 ? (int)(totalBankBytes / totalBanks) : 1;
                long memBudget = Math.Min(availableMem / 8, 300_000_000L);
                int batchSize = Math.Clamp((int)(memBudget / Math.Max(1, avgBankSize * 2)), 1, Environment.ProcessorCount);
                bool useTempFiles = totalBankBytes > memBudget;

                blobsStream = new FileStream(Path.Combine(dictPath, "blobs.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                writeOffset = WriteTermBanks(blobsStream, allFlatOffsets, termBanks, zipBase, writeOffset, result, batchSize, useTempFiles);
                writeOffset = WriteMetaBanks(blobsStream, allFlatOffsets, metaBanks, zipBase, writeOffset, result, batchSize);

                mediaTask?.Wait();
                zipAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                zipAccessor.Dispose();
                zipMmf.Dispose();

                if (zipLength > 50_000_000)
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true);
            }

            if (allFlatOffsets.Count == 0)
                throw new InvalidOperationException("empty dictionary");

            RadixSortByHash(CollectionsMarshal.AsSpan(allFlatOffsets));

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

            blobsStream!.Write(offsetData, 0, offsetPos);
            blobsStream.Flush();
            blobsStream.Dispose();

            var phf = new Mphf();
            phf.BuildFromSortedHashes(uniqueHashes);
            phf.Save(Path.Combine(dictPath, "hash.mph"));

            using (var offsStream = new FileStream(Path.Combine(dictPath, "offsets.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                offsStream.Write(MemoryMarshal.AsBytes(keyOffsets.AsSpan()));
            }

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

    private static void ClassifyZipEntries(byte* zip, int zipLength, ref byte[] indexContent, ref byte[] styles,
        List<BankEntry> termBanks, List<BankEntry> metaBanks, List<MediaEntry> mediaEntries)
    {
        int eocdOffset = -1;
        for (int i = zipLength - 22; i >= Math.Max(0, zipLength - 65557); i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(zip + i, 4)) == 0x06054b50)
            {
                eocdOffset = i;
                break;
            }
        }
        if (eocdOffset < 0) throw new InvalidOperationException("invalid zip: EOCD not found");

        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(zip + eocdOffset + 16, 4));
        long cdEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(zip + eocdOffset + 10, 2));

        if (cdOffset == 0xFFFFFFFF || cdEntryCount == 0xFFFF)
        {
            int locatorOffset = eocdOffset - 20;
            if (locatorOffset >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(zip + locatorOffset, 4)) == 0x07064b50)
            {
                long zip64EocdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(zip + locatorOffset + 8, 8));
                cdEntryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(zip + (int)zip64EocdOffset + 32, 8));
                cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(zip + (int)zip64EocdOffset + 48, 8));
            }
        }

        int pos = (int)cdOffset;

        for (long e = 0; e < cdEntryCount; e++)
        {
            ushort compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(zip + pos + 10, 2));
            int compressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(zip + pos + 20, 4));
            int uncompressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(zip + pos + 24, 4));
            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(zip + pos + 28, 2));
            ushort extraLen = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(zip + pos + 30, 2));
            ushort commentLen = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(zip + pos + 32, 2));
            long localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(zip + pos + 42, 4));

            if (IsZip64Sentinel(compressedSize) || IsZip64Sentinel(uncompressedSize) || localHeaderOffset == 0xFFFFFFFF)
            {
                ParseZip64Extra(new ReadOnlySpan<byte>(zip + pos + 46 + nameLen, extraLen),
                    ref uncompressedSize, ref compressedSize, ref localHeaderOffset);
            }

            int lhPos = (int)localHeaderOffset;
            ushort localNameLen = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(zip + lhPos + 26, 2));
            ushort localExtraLen = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(zip + lhPos + 28, 2));
            int dataOffset = lhPos + 30 + localNameLen + localExtraLen;

            ReadOnlySpan<byte> fullNameBytes = new ReadOnlySpan<byte>(zip + pos + 46, nameLen);
            int lastSlash = fullNameBytes.LastIndexOf((byte)'/');
            ReadOnlySpan<byte> fileName = lastSlash >= 0 ? fullNameBytes.Slice(lastSlash + 1) : fullNameBytes;

            if (fileName.Length == 0 || uncompressedSize == 0)
            {
                pos += 46 + nameLen + extraLen + commentLen;
                continue;
            }

            if (fileName.SequenceEqual("index.json"u8))
            {
                indexContent = ReadEntryDirect(zip, dataOffset, compressedSize, uncompressedSize, compressionMethod);
            }
            else if (fileName.SequenceEqual("styles.css"u8))
            {
                styles = ReadEntryDirect(zip, dataOffset, compressedSize, uncompressedSize, compressionMethod);
            }
            else if (fileName.StartsWith("term_bank_"u8))
            {
                termBanks.Add(new BankEntry(dataOffset, compressedSize, uncompressedSize, compressionMethod));
            }
            else if (fileName.StartsWith("term_meta_bank_"u8))
            {
                metaBanks.Add(new BankEntry(dataOffset, compressedSize, uncompressedSize, compressionMethod));
            }
            else if (!fileName.StartsWith("tag_bank_"u8))
            {
                mediaEntries.Add(new MediaEntry(pos + 46, nameLen, dataOffset, compressedSize, uncompressedSize, compressionMethod));
            }

            pos += 46 + nameLen + extraLen + commentLen;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsZip64Sentinel(int value) => value == unchecked((int)0xFFFFFFFF);

    private static void ParseZip64Extra(ReadOnlySpan<byte> extra, ref int uncompressedSize, ref int compressedSize, ref long localHeaderOffset)
    {
        int ep = 0;
        while (ep + 4 <= extra.Length)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(ep));
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(ep + 2));
            if (tag == 0x0001)
            {
                int fp = ep + 4;
                if (IsZip64Sentinel(uncompressedSize))
                {
                    uncompressedSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(extra.Slice(fp));
                    fp += 8;
                }
                if (IsZip64Sentinel(compressedSize))
                {
                    compressedSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(extra.Slice(fp));
                    fp += 8;
                }
                if (localHeaderOffset == 0xFFFFFFFF)
                {
                    localHeaderOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.Slice(fp));
                }
                return;
            }
            ep += 4 + size;
        }
    }

    private static byte[] ReadEntryDirect(byte* zip, int dataOffset, int compressedSize, int uncompressedSize, ushort compressionMethod)
    {
        if (compressionMethod == 0)
        {
            var buf = new byte[uncompressedSize];
            new ReadOnlySpan<byte>(zip + dataOffset, uncompressedSize).CopyTo(buf);
            return buf;
        }

        var result = new byte[uncompressedSize];
        using var ds = new DeflateStream(
            new UnmanagedMemoryStream(zip + dataOffset, compressedSize),
            CompressionMode.Decompress);
        ReadFully(ds, result, uncompressedSize);
        return result;
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
        if (contentLength == 0) return processed;

        int estimatedTerms = Math.Max(64, contentLength / 200);

        var data = new byte[Math.Max(1024, contentLength / 4)];
        int pos = 0;

        var glossaryRaw = new byte[Math.Max(1024, contentLength / 4)];
        int glossaryRawPos = 0;

        var flatOffsets = new List<(ulong Hash, ulong Offset)>(estimatedTerms);
        var glossaryIndex = new Dictionary<ulong, (int Offset, int Length)>(estimatedTerms / 2);
        var glossaryPatches = new List<(int Position, int RawOffset)>(estimatedTerms);

        YomitanParser.ParseTermBank(content, contentLength, (in YomitanParser.TermFields term) =>
        {
            ReadOnlySpan<byte> glossary = content.AsSpan(term.GlossaryStart, term.GlossaryLen);
            ulong glossaryHash = XxHash3.HashToUInt64(glossary);

            ref var glossaryEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(
                glossaryIndex, glossaryHash, out bool glossaryExists);

            if (!glossaryExists)
            {
                EnsureCapacity(ref glossaryRaw, glossaryRawPos, glossary.Length);
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
            EnsureCapacity(ref data, pos, binaryNeeded);

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
        if (contentLength == 0) return processed;

        int estimatedTerms = Math.Max(64, contentLength / 200);
        var data = new byte[Math.Max(1024, contentLength / 4)];
        int pos = 0;

        var flatOffsets = new List<(ulong Hash, ulong Offset)>(estimatedTerms);

        YomitanParser.ParseMetaBank(content, contentLength, (in YomitanParser.MetaFields meta) =>
        {
            ReadOnlySpan<byte> expr = content.AsSpan(meta.ExprStart, meta.ExprLen);
            ReadOnlySpan<byte> mode = content.AsSpan(meta.ModeStart, meta.ModeLen);
            ReadOnlySpan<byte> metaData = content.AsSpan(meta.DataStart, meta.DataLen);

            int needed = 1 + 2 + expr.Length + 1 + mode.Length + 4 + metaData.Length;
            EnsureCapacity(ref data, pos, needed);

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

    private static Stream CreateTempStream(bool useTempFiles)
    {
        if (useTempFiles)
        {
            string path = Path.GetTempFileName();
            return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose);
        }
        return new MemoryStream();
    }

    private static ulong WriteTermBanks(FileStream file, List<(ulong Hash, ulong GlobalOffset)> allFlatOffsets,
        List<BankEntry> banks, byte* zipBytes, ulong writeOffset, ImportResult result, int batchSize, bool useTempFiles)
    {
        if (banks.Count == 0) return writeOffset;

        using var glossaryBuf = CreateTempStream(useTempFiles);
        using var dataBuf = CreateTempStream(useTempFiles);
        ulong cumulativeUncompressedLen = 0;
        bool periodicGc = banks.Count > 8;

        for (int batchStart = 0; batchStart < banks.Count; batchStart += batchSize)
        {
            int batchEnd = Math.Min(batchStart + batchSize, banks.Count);
            int count = batchEnd - batchStart;
            var batchProcessed = new ProcessedFile[count];

            Parallel.For(0, count, i =>
            {
                var bank = banks[batchStart + i];
                byte[] buf = new byte[bank.UncompressedSize];
                if (bank.CompressionMethod == 0)
                {
                    new ReadOnlySpan<byte>(zipBytes + bank.DataOffset, bank.UncompressedSize).CopyTo(buf);
                }
                else
                {
                    using var ds = new DeflateStream(
                        new UnmanagedMemoryStream(zipBytes + bank.DataOffset, bank.CompressedSize),
                        CompressionMode.Decompress);
                    ReadFully(ds, buf, bank.UncompressedSize);
                }
                batchProcessed[i] = ProcessTermBank(buf, bank.UncompressedSize);
            });

            for (int i = 0; i < count; i++)
            {
                var p = batchProcessed[i];
                if (p.DataLength == 0) continue;

                var patches = CollectionsMarshal.AsSpan(p.GlossaryPatches);
                for (int j = 0; j < patches.Length; j++)
                {
                    var (patchPos, rawOffset) = patches[j];
                    BinaryPrimitives.WriteUInt64LittleEndian(p.Data.AsSpan(patchPos), cumulativeUncompressedLen + (ulong)rawOffset);
                }
                cumulativeUncompressedLen += (ulong)p.GlossaryRawLen;

                glossaryBuf.Write(p.CompressedGlossary, 0, p.CompressedGlossaryLen);

                ulong dataPos = (ulong)dataBuf.Length;
                dataBuf.Write(p.Data, 0, p.DataLength);

                int baseIdx = allFlatOffsets.Count;
                var flatSpan = CollectionsMarshal.AsSpan(p.FlatOffsets);
                CollectionsMarshal.SetCount(allFlatOffsets, baseIdx + flatSpan.Length);
                var allSpan = CollectionsMarshal.AsSpan(allFlatOffsets);
                for (int j = 0; j < flatSpan.Length; j++)
                    allSpan[baseIdx + j] = (flatSpan[j].Hash, flatSpan[j].Offset + dataPos);

                result.TermCount += p.Count;
                batchProcessed[i] = null!;
            }

            if (periodicGc)
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Default, false);
        }

        ulong totalCompressedSize = (ulong)glossaryBuf.Length;
        Span<byte> headerBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(headerBuf, totalCompressedSize);
        file.Write(headerBuf);
        writeOffset += 8;

        glossaryBuf.Position = 0;
        glossaryBuf.CopyTo(file);
        writeOffset += totalCompressedSize;

        ulong dataStartOffset = writeOffset;
        var finalSpan = CollectionsMarshal.AsSpan(allFlatOffsets);
        for (int i = 0; i < finalSpan.Length; i++)
            finalSpan[i] = (finalSpan[i].Hash, finalSpan[i].GlobalOffset + dataStartOffset);

        ulong dataLen = (ulong)dataBuf.Length;
        dataBuf.Position = 0;
        dataBuf.CopyTo(file);
        writeOffset += dataLen;

        return writeOffset;
    }

    private static ulong WriteMetaBanks(FileStream file, List<(ulong Hash, ulong GlobalOffset)> allFlatOffsets,
        List<BankEntry> banks, byte* zipBytes, ulong writeOffset, ImportResult result, int batchSize)
    {
        if (banks.Count == 0) return writeOffset;

        bool periodicGc = banks.Count > 8;
        for (int batchStart = 0; batchStart < banks.Count; batchStart += batchSize)
        {
            int batchEnd = Math.Min(batchStart + batchSize, banks.Count);
            int count = batchEnd - batchStart;
            var batchProcessed = new ProcessedFile[count];

            Parallel.For(0, count, i =>
            {
                var bank = banks[batchStart + i];
                byte[] buf = new byte[bank.UncompressedSize];
                if (bank.CompressionMethod == 0)
                {
                    new ReadOnlySpan<byte>(zipBytes + bank.DataOffset, bank.UncompressedSize).CopyTo(buf);
                }
                else
                {
                    using var ds = new DeflateStream(
                        new UnmanagedMemoryStream(zipBytes + bank.DataOffset, bank.CompressedSize),
                        CompressionMode.Decompress);
                    ReadFully(ds, buf, bank.UncompressedSize);
                }
                batchProcessed[i] = ProcessMetaBank(buf, bank.UncompressedSize);
            });

            for (int i = 0; i < count; i++)
            {
                var p = batchProcessed[i];
                if (p.DataLength == 0) continue;

                file.Write(p.Data, 0, p.DataLength);

                int baseIdx = allFlatOffsets.Count;
                var flatSpan = CollectionsMarshal.AsSpan(p.FlatOffsets);
                CollectionsMarshal.SetCount(allFlatOffsets, baseIdx + flatSpan.Length);
                var allSpan = CollectionsMarshal.AsSpan(allFlatOffsets);
                for (int k = 0; k < flatSpan.Length; k++)
                    allSpan[baseIdx + k] = (flatSpan[k].Hash, flatSpan[k].Offset + writeOffset);

                writeOffset += (ulong)p.DataLength;
                result.MetaCount += p.Count;
                batchProcessed[i] = null!;
            }

            if (periodicGc)
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Default, false);
        }

        return writeOffset;
    }

    private static void WriteMedia(string dictPath, List<MediaEntry> mediaFiles, byte* zipBytes, ImportResult result)
    {
        if (mediaFiles.Count == 0) return;

        using var mediaStream = new FileStream(Path.Combine(dictPath, "media.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 4 << 20);
        Span<byte> hdr = stackalloc byte[6];

        for (int i = 0; i < mediaFiles.Count; i++)
        {
            var entry = mediaFiles[i];

            BinaryPrimitives.WriteUInt16LittleEndian(hdr, (ushort)entry.PathLen);
            mediaStream.Write(hdr.Slice(0, 2));
            mediaStream.Write(new ReadOnlySpan<byte>(zipBytes + entry.PathOffset, entry.PathLen));
            BinaryPrimitives.WriteUInt32LittleEndian(hdr, (uint)entry.UncompressedSize);
            mediaStream.Write(hdr.Slice(0, 4));

            if (entry.CompressionMethod == 0)
            {
                mediaStream.Write(new ReadOnlySpan<byte>(zipBytes + entry.DataOffset, entry.UncompressedSize));
            }
            else
            {
                byte[] buf = ArrayPool<byte>.Shared.Rent(entry.UncompressedSize);
                using var ds = new DeflateStream(
                    new UnmanagedMemoryStream(zipBytes + entry.DataOffset, entry.CompressedSize),
                    CompressionMode.Decompress);
                ReadFully(ds, buf, entry.UncompressedSize);
                mediaStream.Write(buf, 0, entry.UncompressedSize);
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        result.MediaCount = mediaFiles.Count;
    }

    private static void RadixSortByHash(Span<(ulong Hash, ulong Offset)> data)
    {
        if (data.Length <= 64)
        {
            data.Sort();
            return;
        }

        var tempArr = ArrayPool<(ulong, ulong)>.Shared.Rent(data.Length);
        var temp = tempArr.AsSpan(0, data.Length);
        Span<int> counts = stackalloc int[256];
        bool srcIsData = true;

        for (int pass = 0; pass < 8; pass++)
        {
            int shift = pass * 8;
            counts.Clear();

            var src = srcIsData ? data : temp;
            var dst = srcIsData ? temp : data;

            for (int i = 0; i < src.Length; i++)
                counts[(int)((src[i].Item1 >> shift) & 0xFF)]++;

            for (int i = 0; i < 256; i++)
            {
                if (counts[i] == src.Length)
                    goto Skip;
            }

            {
                int sum = 0;
                for (int i = 0; i < 256; i++)
                {
                    int c = counts[i];
                    counts[i] = sum;
                    sum += c;
                }

                for (int i = 0; i < src.Length; i++)
                {
                    int bucket = (int)((src[i].Item1 >> shift) & 0xFF);
                    dst[counts[bucket]++] = src[i];
                }

                srcIsData = !srcIsData;
                continue;
            }

            Skip:;
        }

        if (!srcIsData)
            temp.Slice(0, data.Length).CopyTo(data);

        ArrayPool<(ulong, ulong)>.Shared.Return(tempArr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureCapacity(ref byte[] buffer, int pos, int needed)
    {
        if (pos + needed <= buffer.Length) return;
        int newSize = Math.Max(buffer.Length * 2, pos + needed);
        var newBuf = new byte[newSize];
        buffer.AsSpan(0, pos).CopyTo(newBuf);
        buffer = newBuf;
    }
}
