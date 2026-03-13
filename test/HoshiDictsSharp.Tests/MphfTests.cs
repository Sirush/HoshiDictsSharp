using System.Text;

namespace HoshiDictsSharp.Tests;

public class MphfTests
{
    [Fact]
    public void Build_And_Lookup_FindsAllKeys()
    {
        var keys = new byte[][]
        {
            Encoding.UTF8.GetBytes("alpha"),
            Encoding.UTF8.GetBytes("beta"),
            Encoding.UTF8.GetBytes("gamma"),
            Encoding.UTF8.GetBytes("delta"),
        };

        var mphf = new Mphf();
        mphf.Build(keys);

        var seenIndices = new HashSet<ulong>();
        for (int i = 0; i < keys.Length; i++)
        {
            ulong idx = mphf.Lookup(keys[i]);
            Assert.True(idx < (ulong)keys.Length, $"Index {idx} out of range for key {i}");
            seenIndices.Add(idx);
        }

        Assert.Equal(keys.Length, seenIndices.Count);
    }

    [Fact]
    public void Lookup_ReturnsConsistentResults()
    {
        var keys = new byte[][] { Encoding.UTF8.GetBytes("test") };
        var mphf = new Mphf();
        mphf.Build(keys);

        ulong first = mphf.Lookup(Encoding.UTF8.GetBytes("test"));
        ulong second = mphf.Lookup(Encoding.UTF8.GetBytes("test"));
        Assert.Equal(first, second);
    }

    [Fact]
    public void SaveLoad_Roundtrip_PreservesData()
    {
        var keys = new byte[][]
        {
            Encoding.UTF8.GetBytes("読む"),
            Encoding.UTF8.GetBytes("食べる"),
            Encoding.UTF8.GetBytes("飲む"),
        };

        var mphf = new Mphf();
        mphf.Build(keys);

        var expected = new ulong[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            expected[i] = mphf.Lookup(keys[i]);

        string tmpPath = Path.GetTempFileName();
        try
        {
            mphf.Save(tmpPath);

            var loaded = new Mphf();
            loaded.Load(tmpPath);

            for (int i = 0; i < keys.Length; i++)
            {
                ulong result = loaded.Lookup(keys[i]);
                Assert.Equal(expected[i], result);
            }
        }
        finally
        {
            File.Delete(tmpPath);
        }
    }

    [Fact]
    public void Type_Returns2()
    {
        var mphf = new Mphf();
        Assert.Equal(2, mphf.Type);
    }

    [Fact]
    public void Build_LargeKeySet_NoCollisions()
    {
        var keys = new byte[1000][];
        for (int i = 0; i < 1000; i++)
            keys[i] = Encoding.UTF8.GetBytes($"key_{i:D4}");

        var mphf = new Mphf();
        mphf.Build(keys);

        var seen = new HashSet<ulong>();
        for (int i = 0; i < keys.Length; i++)
        {
            ulong idx = mphf.Lookup(keys[i]);
            seen.Add(idx);
        }

        Assert.Equal(1000, seen.Count);
    }
}
