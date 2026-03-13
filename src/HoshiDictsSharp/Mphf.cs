using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace HoshiDictsSharp;

public sealed class Mphf
{
    private Entry[] _table = [];

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Entry : IComparable<Entry>
    {
        public ulong Hash;
        public ulong Index;

        public int CompareTo(Entry other) => Hash.CompareTo(other.Hash);
    }

    public byte Type => 2; // single

    public ulong Lookup(ReadOnlySpan<byte> key)
    {
        ulong h = XxHash3.HashToUInt64(key);
        var table = _table.AsSpan();

        int lo = 0, hi = table.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ulong midHash = table[mid].Hash;
            if (midHash < h) lo = mid + 1;
            else if (midHash > h) hi = mid - 1;
            else return table[mid].Index;
        }

        return 0;
    }

    public void Build(ReadOnlySpan<byte[]> keys)
    {
        _table = new Entry[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            _table[i] = new Entry
            {
                Hash = XxHash3.HashToUInt64(keys[i]),
                Index = (ulong)i
            };
        }

        Array.Sort(_table);
    }

    internal void BuildFromSortedHashes(ulong[] sortedHashes)
    {
        _table = new Entry[sortedHashes.Length];
        for (int i = 0; i < sortedHashes.Length; i++)
        {
            _table[i] = new Entry
            {
                Hash = sortedHashes[i],
                Index = (ulong)i
            };
        }
    }

    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, (ulong)_table.Length);
        fs.Write(buf);

        var bytes = MemoryMarshal.AsBytes(_table.AsSpan());
        fs.Write(bytes);
    }

    public void Load(string path)
    {
        var data = File.ReadAllBytes(path);
        ulong n = BinaryPrimitives.ReadUInt64LittleEndian(data);
        _table = new Entry[n];
        var src = data.AsSpan(8);
        MemoryMarshal.Cast<byte, Entry>(src[..(int)(n * 16)]).CopyTo(_table);
    }
}
