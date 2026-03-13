using System.IO.Compression;

namespace HoshiDictsSharp.Tests;

public class LookupEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dictPath;

    public LookupEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoshidicts_lookup_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        string termBank = string.Join(",", new[]
        {
            "[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]",
            "[\"飲む\",\"のむ\",\"\",\"v5m\",0,[\"to drink\"],0,\"\"]",
            "[\"読む\",\"よむ\",\"\",\"v5m\",0,[\"to read\"],0,\"\"]",
            "[\"高い\",\"たかい\",\"\",\"adj-i\",0,[\"high\"],0,\"\"]",
            "[\"行く\",\"いく\",\"\",\"v5k-s\",0,[\"to go\"],0,\"\"]",
        });
        string termBankJson = $"[{termBank}]";

        string zipPath = Path.Combine(_tempDir, "test.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = archive.CreateEntry("index.json");
            using (var w = new StreamWriter(indexEntry.Open()))
                w.Write("{\"title\":\"LookupTest\",\"format\":3,\"revision\":\"1\"}");

            var termEntry = archive.CreateEntry("term_bank_1.json");
            using (var w = new StreamWriter(termEntry.Open()))
                w.Write(termBankJson);
        }

        var result = DictionaryImporter.Import(zipPath, _tempDir);
        Assert.True(result.Success);
        _dictPath = Path.Combine(_tempDir, "LookupTest");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Lookup_DictionaryForm_FindsTerm()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        var results = engine.Lookup("食べる");
        Assert.True(results.Count > 0);
        Assert.Contains(results, r => r.Term.Expression == "食べる");
    }

    [Fact]
    public void Lookup_ConjugatedForm_FindsDictionaryForm()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        var results = engine.Lookup("食べた");
        Assert.True(results.Count > 0);
        Assert.Contains(results, r => r.Term.Expression == "食べる");
    }

    [Fact]
    public void Lookup_PastTenseGodan_FindsDictionaryForm()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        var results = engine.Lookup("読んだ");
        Assert.True(results.Count > 0);
        Assert.Contains(results, r => r.Term.Expression == "読む");
    }

    [Fact]
    public void Lookup_LongerText_MatchesLongestSubstring()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        // "食べている" should match 食べる with "食べている" as matched text
        var results = engine.Lookup("食べている");
        Assert.True(results.Count > 0);
        var eatResult = results.FirstOrDefault(r => r.Term.Expression == "食べる");
        Assert.NotNull(eatResult);
        Assert.True(eatResult.Matched.Length > 2);
    }

    [Fact]
    public void Lookup_MaxResults_RespectsLimit()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        var results = engine.Lookup("食べる", maxResults: 1);
        Assert.True(results.Count <= 1);
    }

    [Fact]
    public void Lookup_DeconjugationProcess_Recorded()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        var results = engine.Lookup("食べた");
        var eatResult = results.First(r => r.Term.Expression == "食べる");
        Assert.True(eatResult.Process.Count > 0);
        Assert.Equal("食べる", eatResult.Deinflected);
    }

    [Fact]
    public void Lookup_KatakanaInput_FindsViaPreprocessing()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        // "タベル" (katakana) should find 食べる via hiragana conversion → たべる reading
        var results = engine.Lookup("タベル");
        // This might or might not find it depending on reading-based lookup
        // The text processor converts to たべる, deconjugator includes it as-is
        // query("たべる") should find 食べる since reading matches
        Assert.Contains(results, r => r.Term.Expression == "食べる");
    }

    [Fact]
    public void Lookup_SortOrder_LongestMatchFirst()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        var results = engine.Lookup("食べている");
        if (results.Count >= 2)
        {
            for (int i = 1; i < results.Count; i++)
            {
                Assert.True(results[i].Matched.Length <= results[i - 1].Matched.Length);
            }
        }
    }

    [Fact]
    public void Lookup_EmptyString_ReturnsEmpty()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        var results = engine.Lookup("");
        Assert.Empty(results);
    }

    [Fact]
    public void Lookup_NegativeForm_FindsDictionaryForm()
    {
        var query = new DictionaryQuery();
        query.AddTermDict(_dictPath);
        var deconj = new Deconjugator();
        var engine = new LookupEngine(query, deconj);

        var results = engine.Lookup("食べない");
        Assert.Contains(results, r => r.Term.Expression == "食べる");
    }
}
