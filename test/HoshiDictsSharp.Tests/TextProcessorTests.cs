namespace HoshiDictsSharp.Tests;

public class TextProcessorTests
{
    [Fact]
    public void Process_PureHiragana_ReturnsOriginalAndKatakana()
    {
        var results = TextProcessor.Process("たべる");
        Assert.Equal(2, results.Count);
        Assert.Equal("たべる", results[0].Text);
        Assert.Equal(0, results[0].Steps);
        Assert.Equal("タベル", results[1].Text);
        Assert.Equal(1, results[1].Steps);
    }

    [Fact]
    public void Process_PureKatakana_ReturnsOriginalAndHiragana()
    {
        var results = TextProcessor.Process("カタカナ");
        Assert.Equal(2, results.Count);
        Assert.Equal("カタカナ", results[0].Text);
        Assert.Equal(0, results[0].Steps);
        Assert.Equal("かたかな", results[1].Text);
        Assert.Equal(1, results[1].Steps);
    }

    [Fact]
    public void Process_MixedKanjiHiragana_ReturnsOriginalAndKatakana()
    {
        var results = TextProcessor.Process("読む");
        Assert.Equal(2, results.Count);
        Assert.Equal("読む", results[0].Text);
        Assert.Equal(0, results[0].Steps);
        Assert.Equal("読ム", results[1].Text);
        Assert.Equal(1, results[1].Steps);
    }

    [Fact]
    public void Process_PureKanji_ReturnsOnlyOriginal()
    {
        var results = TextProcessor.Process("漢字");
        Assert.Single(results);
        Assert.Equal("漢字", results[0].Text);
        Assert.Equal(0, results[0].Steps);
    }

    [Fact]
    public void Process_PureAscii_ReturnsOnlyOriginal()
    {
        var results = TextProcessor.Process("hello");
        Assert.Single(results);
        Assert.Equal("hello", results[0].Text);
    }

    [Fact]
    public void Process_EmptyString_ReturnsOnlyOriginal()
    {
        var results = TextProcessor.Process("");
        Assert.Single(results);
        Assert.Equal("", results[0].Text);
    }

    [Fact]
    public void Process_ProlongedSoundMark_ConvertedToVowel()
    {
        // カー should become かあ (ー after ka-row → あ)
        var results = TextProcessor.Process("カー");
        var hiragana = results.First(r => r.Steps == 1);
        Assert.Equal("かあ", hiragana.Text);
    }

    [Fact]
    public void Process_ProlongedSoundMark_IRRow()
    {
        // キー should become きい (ー after ki-row → い)
        var results = TextProcessor.Process("キー");
        var hiragana = results.First(r => r.Steps == 1);
        Assert.Equal("きい", hiragana.Text);
    }

    [Fact]
    public void Process_ProlongedSoundMark_URRow()
    {
        // クー should become くう (ー after ku-row → う)
        var results = TextProcessor.Process("クー");
        var hiragana = results.First(r => r.Steps == 1);
        Assert.Equal("くう", hiragana.Text);
    }

    [Fact]
    public void Process_ProlongedSoundMark_ERRow()
    {
        // ケー should become けえ (ー after ke-row → え)
        // But ヶ is KatakanaSmallKe which is excluded from conversion
        // Let's use セー instead
        var results = TextProcessor.Process("セー");
        var hiragana = results.First(r => r.Steps == 1);
        Assert.Equal("せえ", hiragana.Text);
    }

    [Fact]
    public void Process_ProlongedSoundMark_ORRow()
    {
        // コー should become こう (ー after ko/o-row → う, matching C++ behavior)
        var results = TextProcessor.Process("コー");
        var hiragana = results.First(r => r.Steps == 1);
        Assert.Equal("こう", hiragana.Text);
    }

    [Fact]
    public void Process_SmallKaKe_NotConverted()
    {
        // ヵ (small ka) and ヶ (small ke) should NOT be converted to hiragana
        var results = TextProcessor.Process("ヵヶ");
        // The original contains only special katakana that shouldn't convert
        // So hiragana conversion should leave them as-is
        var hiraganaVariant = results.FirstOrDefault(r => r.Steps == 1);
        // Since ヵ and ヶ don't convert, hiragana == original, so no variant added
        Assert.Single(results);
    }

    [Fact]
    public void Process_KatakanaWithSmallKa_PartialConversion()
    {
        // アヵ — ア converts to あ, ヵ stays
        var results = TextProcessor.Process("アヵ");
        Assert.Equal(2, results.Count);
        var hiragana = results.First(r => r.Steps == 1);
        Assert.Equal("あヵ", hiragana.Text);
    }

    [Fact]
    public void Process_MixedKatakanaHiragana_ThreeVariants()
    {
        // If input has both hiragana and katakana, we get 3 variants:
        // original, hiragana (all kana→hiragana), katakana (all kana→katakana)
        // BUT only if they're all different
        var results = TextProcessor.Process("あカ");
        Assert.Equal(3, results.Count);
        Assert.Equal("あカ", results[0].Text);   // original
        Assert.Equal("あか", results[1].Text);   // hiragana
        Assert.Equal("アカ", results[2].Text);   // katakana
    }

    [Fact]
    public void Process_ProlongedSoundMarkAtStart_NotConverted()
    {
        // ー at the very start has no previous character
        var results = TextProcessor.Process("ーア");
        var hiragana = results.First(r => r.Steps == 1);
        Assert.Equal("ーあ", hiragana.Text);
    }

    [Fact]
    public void Process_MultipleProlongedSoundMarks()
    {
        // アーー should become あああ
        var results = TextProcessor.Process("アーー");
        var hiragana = results.First(r => r.Steps == 1);
        Assert.Equal("あああ", hiragana.Text);
    }
}
