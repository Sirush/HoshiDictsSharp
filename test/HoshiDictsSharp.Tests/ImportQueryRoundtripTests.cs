using System.IO.Compression;
using System.Text;

namespace HoshiDictsSharp.Tests;

public class ImportQueryRoundtripTests : IDisposable
{
    private readonly string _tempDir;

    public ImportQueryRoundtripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoshidicts_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string CreateTestZip(
        string title = "TestDict",
        string? termBankJson = null,
        string? metaBankJson = null,
        string? styles = null,
        Dictionary<string, byte[]>? mediaFiles = null)
    {
        string zipPath = Path.Combine(_tempDir, "test.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = archive.CreateEntry("index.json");
            using (var w = new StreamWriter(indexEntry.Open()))
                w.Write($"{{\"title\":\"{title}\",\"format\":3,\"revision\":\"1\"}}");

            if (termBankJson != null)
            {
                var termEntry = archive.CreateEntry("term_bank_1.json");
                using (var w = new StreamWriter(termEntry.Open()))
                    w.Write(termBankJson);
            }

            if (metaBankJson != null)
            {
                var metaEntry = archive.CreateEntry("term_meta_bank_1.json");
                using (var w = new StreamWriter(metaEntry.Open()))
                    w.Write(metaBankJson);
            }

            if (styles != null)
            {
                var stylesEntry = archive.CreateEntry("styles.css");
                using (var w = new StreamWriter(stylesEntry.Open()))
                    w.Write(styles);
            }

            if (mediaFiles != null)
            {
                foreach (var (name, data) in mediaFiles)
                {
                    var entry = archive.CreateEntry(name);
                    using var s = entry.Open();
                    s.Write(data);
                }
            }
        }
        return zipPath;
    }

    [Fact]
    public void Import_ValidDictionary_Succeeds()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        var result = DictionaryImporter.Import(zipPath, _tempDir);
        Assert.True(result.Success);
        Assert.Equal("TestDict", result.Title);
        Assert.Equal(1, result.TermCount);
        Assert.Equal(0, result.MetaCount);
        Assert.Equal(0, result.MediaCount);
    }

    [Fact]
    public void Import_MultipleTerms_CorrectCount()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"],[\"飲む\",\"のむ\",\"\",\"v5\",0,[\"to drink\"],0,\"\"],[\"読む\",\"よむ\",\"\",\"v5\",0,[\"to read\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        var result = DictionaryImporter.Import(zipPath, _tempDir);
        Assert.True(result.Success);
        Assert.Equal(3, result.TermCount);
    }

    [Fact]
    public void Import_WithStyles_StylesCopied()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank, styles: "body { color: red; }");

        var result = DictionaryImporter.Import(zipPath, _tempDir);
        Assert.True(result.Success);

        string stylesPath = Path.Combine(_tempDir, "TestDict", "styles.css");
        Assert.True(File.Exists(stylesPath));
        Assert.Equal("body { color: red; }", File.ReadAllText(stylesPath));
    }

    [Fact]
    public void Import_WithMedia_MediaCounted()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        var media = new Dictionary<string, byte[]>
        {
            ["img/test.png"] = new byte[] { 0x89, 0x50, 0x4E, 0x47 },
        };
        string zipPath = CreateTestZip(termBankJson: termBank, mediaFiles: media);

        var result = DictionaryImporter.Import(zipPath, _tempDir);
        Assert.True(result.Success);
        Assert.Equal(1, result.MediaCount);
    }

    [Fact]
    public void ImportAndQuery_SingleTerm_ReturnsCorrectResult()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"def-tag\",\"v1\",0,[\"to eat\"],0,\"term-tag\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        var importResult = DictionaryImporter.Import(zipPath, _tempDir);
        Assert.True(importResult.Success);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        var results = query.Query("食べる");
        Assert.Single(results);
        Assert.Equal("食べる", results[0].Expression);
        Assert.Equal("たべる", results[0].Reading);
        Assert.Equal("v1", results[0].Rules);
        Assert.Single(results[0].Glossaries);
        Assert.Equal("TestDict", results[0].Glossaries[0].DictName);
        Assert.Contains("to eat", results[0].Glossaries[0].Glossary);
        Assert.Equal("def-tag", results[0].Glossaries[0].DefinitionTags);
        Assert.Equal("term-tag", results[0].Glossaries[0].TermTags);
    }

    [Fact]
    public void ImportAndQuery_ByReading_ReturnsResult()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        var results = query.Query("たべる");
        Assert.Single(results);
        Assert.Equal("食べる", results[0].Expression);
    }

    [Fact]
    public void ImportAndQuery_NonExistentTerm_ReturnsEmpty()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        var results = query.Query("存在しない");
        Assert.Empty(results);
    }

    [Fact]
    public void ImportAndQuery_MultipleTerms_QueriedIndependently()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"],[\"飲む\",\"のむ\",\"\",\"v5\",0,[\"to drink\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        var eatResults = query.Query("食べる");
        Assert.Single(eatResults);
        Assert.Contains("to eat", eatResults[0].Glossaries[0].Glossary);

        var drinkResults = query.Query("飲む");
        Assert.Single(drinkResults);
        Assert.Contains("to drink", drinkResults[0].Glossaries[0].Glossary);
    }

    [Fact]
    public void ImportAndQuery_DuplicateGlossaries_Deduplicated()
    {
        // Two terms with the same glossary content
        string termBank = "[[\"行く\",\"いく\",\"\",\"v5\",0,[\"to go\"],0,\"\"],[\"行く\",\"ゆく\",\"\",\"v5\",0,[\"to go\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        // Query by expression — both readings should appear as separate entries
        var results = query.Query("行く");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ImportAndQuery_GlossaryContentPreserved()
    {
        string complexGlossary = "[{\"type\":\"structured-content\",\"content\":\"test \\\"quoted\\\"\"}]";
        string termBank = $"[[\"テスト\",\"テスト\",\"\",\"\",0,{complexGlossary},0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        var results = query.Query("テスト");
        Assert.Single(results);
        Assert.Contains("structured-content", results[0].Glossaries[0].Glossary);
    }

    [Fact]
    public void ImportAndQuery_WithFrequency_FreqParsedCorrectly()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string metaBank = "[[\"食べる\",\"freq\",{\"reading\":\"たべる\",\"value\":42,\"displayValue\":\"42nd\"}]]";
        string zipPath = CreateTestZip(title: "FreqDict", termBankJson: termBank, metaBankJson: metaBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "FreqDict"));
        query.AddFreqDict(Path.Combine(_tempDir, "FreqDict"));

        var results = query.Query("食べる");
        Assert.Single(results);
        Assert.Single(results[0].Frequencies);
        Assert.Equal("FreqDict", results[0].Frequencies[0].DictName);
        Assert.Single(results[0].Frequencies[0].Frequencies);
        Assert.Equal(42, results[0].Frequencies[0].Frequencies[0].Value);
        Assert.Equal("42nd", results[0].Frequencies[0].Frequencies[0].DisplayValue);
    }

    [Fact]
    public void ImportAndQuery_FrequencyPlainInt()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string metaBank = "[[\"食べる\",\"freq\",100]]";
        string zipPath = CreateTestZip(title: "FreqInt", termBankJson: termBank, metaBankJson: metaBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "FreqInt"));
        query.AddFreqDict(Path.Combine(_tempDir, "FreqInt"));

        var results = query.Query("食べる");
        Assert.Single(results);
        Assert.Single(results[0].Frequencies);
        Assert.Equal(100, results[0].Frequencies[0].Frequencies[0].Value);
        Assert.Equal("100", results[0].Frequencies[0].Frequencies[0].DisplayValue);
    }

    [Fact]
    public void ImportAndQuery_FrequencyNested()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string metaBank = "[[\"食べる\",\"freq\",{\"reading\":\"たべる\",\"frequency\":{\"value\":55,\"displayValue\":\"55位\"}}]]";
        string zipPath = CreateTestZip(title: "FreqNested", termBankJson: termBank, metaBankJson: metaBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "FreqNested"));
        query.AddFreqDict(Path.Combine(_tempDir, "FreqNested"));

        var results = query.Query("食べる");
        Assert.Single(results);
        Assert.Equal(55, results[0].Frequencies[0].Frequencies[0].Value);
        Assert.Equal("55位", results[0].Frequencies[0].Frequencies[0].DisplayValue);
    }

    [Fact]
    public void ImportAndQuery_FrequencyNestedIntOnly()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string metaBank = "[[\"食べる\",\"freq\",{\"reading\":\"たべる\",\"frequency\":77}]]";
        string zipPath = CreateTestZip(title: "FreqNestedInt", termBankJson: termBank, metaBankJson: metaBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "FreqNestedInt"));
        query.AddFreqDict(Path.Combine(_tempDir, "FreqNestedInt"));

        var results = query.Query("食べる");
        Assert.Single(results);
        Assert.Equal(77, results[0].Frequencies[0].Frequencies[0].Value);
    }

    [Fact]
    public void ImportAndQuery_FrequencyWrongReading_Filtered()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string metaBank = "[[\"食べる\",\"freq\",{\"reading\":\"wrongreading\",\"value\":42}]]";
        string zipPath = CreateTestZip(title: "FreqWrongReading", termBankJson: termBank, metaBankJson: metaBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "FreqWrongReading"));
        query.AddFreqDict(Path.Combine(_tempDir, "FreqWrongReading"));

        var results = query.Query("食べる");
        Assert.Single(results);
        Assert.Empty(results[0].Frequencies);
    }

    [Fact]
    public void ImportAndQuery_WithPitch_PitchParsedCorrectly()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string metaBank = "[[\"食べる\",\"pitch\",{\"reading\":\"たべる\",\"pitches\":[{\"position\":2}]}]]";
        string zipPath = CreateTestZip(title: "PitchDict", termBankJson: termBank, metaBankJson: metaBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "PitchDict"));
        query.AddPitchDict(Path.Combine(_tempDir, "PitchDict"));

        var results = query.Query("食べる");
        Assert.Single(results);
        Assert.Single(results[0].Pitches);
        Assert.Equal("PitchDict", results[0].Pitches[0].DictName);
        Assert.Single(results[0].Pitches[0].PitchPositions);
        Assert.Equal(2, results[0].Pitches[0].PitchPositions[0]);
    }

    [Fact]
    public void ImportAndQuery_WithMedia_MediaRetrievable()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        byte[] imageData = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var media = new Dictionary<string, byte[]> { ["img/test.png"] = imageData };
        string zipPath = CreateTestZip(title: "MediaDict", termBankJson: termBank, mediaFiles: media);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "MediaDict"));

        var retrieved = query.GetMediaFile("MediaDict", "img/test.png");
        Assert.NotNull(retrieved);
        Assert.Equal(imageData, retrieved);
    }

    [Fact]
    public void ImportAndQuery_MediaNotFound_ReturnsNull()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        Assert.Null(query.GetMediaFile("TestDict", "nonexistent.png"));
    }

    [Fact]
    public void ImportAndQuery_Styles_ReturnedCorrectly()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank, styles: ".test { color: blue; }");

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        var styles = query.GetStyles();
        Assert.Single(styles);
        Assert.Equal("TestDict", styles[0].Name);
        Assert.Equal(".test { color: blue; }", styles[0].Css);
    }

    [Fact]
    public void ImportAndQuery_NoStyles_EmptyList()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        Assert.Empty(query.GetStyles());
    }

    [Fact]
    public void ImportAndQuery_FreqDictOrder_Preserved()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string metaBank = "[[\"食べる\",\"freq\",100]]";

        string zipPath1 = CreateTestZip(title: "FreqA", termBankJson: termBank, metaBankJson: metaBank);
        DictionaryImporter.Import(zipPath1, _tempDir);

        string zipPath2 = CreateTestZip(title: "FreqB", termBankJson: termBank, metaBankJson: metaBank);
        DictionaryImporter.Import(zipPath2, _tempDir);

        var query = new DictionaryQuery();
        query.AddFreqDict(Path.Combine(_tempDir, "FreqA"));
        query.AddFreqDict(Path.Combine(_tempDir, "FreqB"));

        var order = query.GetFreqDictOrder();
        Assert.Equal(2, order.Count);
        Assert.Equal("FreqA", order[0]);
        Assert.Equal("FreqB", order[1]);
    }

    [Fact]
    public void ImportAndQuery_OutputFilesExist()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        string dictPath = Path.Combine(_tempDir, "TestDict");
        Assert.True(File.Exists(Path.Combine(dictPath, "blobs.bin")));
        Assert.True(File.Exists(Path.Combine(dictPath, "offsets.bin")));
        Assert.True(File.Exists(Path.Combine(dictPath, "hash.mph")));
        Assert.True(File.Exists(Path.Combine(dictPath, "index.json")));
        Assert.True(File.Exists(Path.Combine(dictPath, ".hoshidicts_1")));
    }

    [Fact]
    public void ImportAndQuery_VersionMarker_ContainsPhfType()
    {
        string termBank = "[[\"食べる\",\"たべる\",\"\",\"v1\",0,[\"to eat\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        byte[] marker = File.ReadAllBytes(Path.Combine(_tempDir, "TestDict", ".hoshidicts_1"));
        Assert.Single(marker);
        Assert.Equal(2, marker[0]);
    }

    [Fact]
    public void Import_EmptyReading_ExpressionUsedAsReading()
    {
        // When reading is empty, expression is used as reading
        string termBank = "[[\"テスト\",\"\",\"\",\"\",0,[\"test\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        var results = query.Query("テスト");
        Assert.Single(results);
        Assert.Equal("テスト", results[0].Expression);
        Assert.Equal("テスト", results[0].Reading);
    }

    [Fact]
    public void ImportAndQuery_SameExprDifferentReadings_TwoResults()
    {
        string termBank = "[[\"行く\",\"いく\",\"\",\"v5\",0,[\"to go (iku)\"],0,\"\"],[\"行く\",\"ゆく\",\"\",\"v5\",0,[\"to go (yuku)\"],0,\"\"]]";
        string zipPath = CreateTestZip(termBankJson: termBank);

        DictionaryImporter.Import(zipPath, _tempDir);

        var query = new DictionaryQuery();
        query.AddTermDict(Path.Combine(_tempDir, "TestDict"));

        var results = query.Query("行く");
        Assert.Equal(2, results.Count);

        var readings = results.Select(r => r.Reading).ToHashSet();
        Assert.Contains("いく", readings);
        Assert.Contains("ゆく", readings);
    }
}
