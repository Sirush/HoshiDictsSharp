namespace HoshiDictsSharp.Tests;

public class DeconjugatorTests
{
    private readonly Deconjugator _deconjugator = new();

    [Fact]
    public void Deconjugate_EmptyString_ReturnsEmpty()
    {
        var results = _deconjugator.Deconjugate("");
        Assert.Empty(results);
    }

    [Fact]
    public void Deconjugate_NullString_ReturnsEmpty()
    {
        var results = _deconjugator.Deconjugate(null!);
        Assert.Empty(results);
    }

    [Fact]
    public void Deconjugate_DictionaryForm_ContainsItself()
    {
        var results = _deconjugator.Deconjugate("読む");
        Assert.Contains(results, f => f.Text == "読む");
    }

    [Fact]
    public void Deconjugate_PastTense_Godan_ContainsDictionaryForm()
    {
        // 読んだ (past tense of 読む, godan v5m)
        var results = _deconjugator.Deconjugate("読んだ");
        Assert.Contains(results, f => f.Text == "読む");
    }

    [Fact]
    public void Deconjugate_PastTense_Ichidan_ContainsDictionaryForm()
    {
        // 食べた (past tense of 食べる, ichidan v1)
        var results = _deconjugator.Deconjugate("食べた");
        Assert.Contains(results, f => f.Text == "食べる");
    }

    [Fact]
    public void Deconjugate_TeForm_ContainsDictionaryForm()
    {
        // 飲んで (te form of 飲む)
        var results = _deconjugator.Deconjugate("飲んで");
        Assert.Contains(results, f => f.Text == "飲む");
    }

    [Fact]
    public void Deconjugate_Negative_ContainsDictionaryForm()
    {
        // 食べない (negative of 食べる)
        var results = _deconjugator.Deconjugate("食べない");
        Assert.Contains(results, f => f.Text == "食べる");
    }

    [Fact]
    public void Deconjugate_Masu_ContainsDictionaryForm()
    {
        // 食べます (polite of 食べる)
        var results = _deconjugator.Deconjugate("食べます");
        Assert.Contains(results, f => f.Text == "食べる");
    }

    [Fact]
    public void Deconjugate_PastTeiru_ContainsDictionaryForm()
    {
        // 読んでいる (progressive of 読む)
        var results = _deconjugator.Deconjugate("読んでいる");
        Assert.Contains(results, f => f.Text == "読む");
    }

    [Fact]
    public void Deconjugate_ColloquialPast_ContainsDictionaryForm()
    {
        // 食べちゃった (colloquial past: ～ちゃう from ～てしまう)
        var results = _deconjugator.Deconjugate("食べちゃった");
        Assert.Contains(results, f => f.Text == "食べる");
    }

    [Fact]
    public void Deconjugate_Shimatta_ContainsDictionaryForm()
    {
        // 終わってしまった
        var results = _deconjugator.Deconjugate("終わってしまった");
        Assert.Contains(results, f => f.Text == "終わる");
    }

    [Fact]
    public void Deconjugate_IAdjective_ContainsStem()
    {
        // 高かった (past tense of 高い, i-adj)
        var results = _deconjugator.Deconjugate("高かった");
        Assert.Contains(results, f => f.Text == "高い");
    }

    [Fact]
    public void Deconjugate_NaAdjective_ContainsStem()
    {
        // 和やかな (attributive na-adj)
        var results = _deconjugator.Deconjugate("和やかな");
        Assert.Contains(results, f => f.Text == "和やか");
    }

    [Fact]
    public void Deconjugate_ResultsSortedByLengthDescending()
    {
        var results = _deconjugator.Deconjugate("食べた");
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(
                results[i].Text.Length <= results[i - 1].Text.Length ||
                (results[i].Text.Length == results[i - 1].Text.Length &&
                 string.Compare(results[i].Text, results[i - 1].Text, StringComparison.Ordinal) >= 0));
        }
    }

    [Fact]
    public void Deconjugate_ProcessSteps_RecordedCorrectly()
    {
        var results = _deconjugator.Deconjugate("読んだ");
        var dictForm = results.First(f => f.Text == "読む");
        Assert.True(dictForm.Process.Count > 0);
        Assert.True(dictForm.Tags.Count > 0);
    }

    [Fact]
    public void Deconjugate_OriginalText_PreservedInAllForms()
    {
        var results = _deconjugator.Deconjugate("食べない");
        foreach (var form in results)
            Assert.Equal("食べない", form.OriginalText);
    }

    [Fact]
    public void Deconjugate_SlurredNegative_ContainsDictionaryForm()
    {
        // わかんない → わかる (slurred negative)
        var results = _deconjugator.Deconjugate("わかんない");
        Assert.Contains(results, f => f.Text == "わかる");
    }

    [Fact]
    public void Deconjugate_ColloquialConditional_ContainsDictionaryForm()
    {
        // 食べなくちゃ → 食べる
        var results = _deconjugator.Deconjugate("食べなくちゃ");
        Assert.Contains(results, f => f.Text == "食べる");
    }

    [Fact]
    public void Deconjugate_Potential_ContainsDictionaryForm()
    {
        // 読める (potential of 読む)
        var results = _deconjugator.Deconjugate("読める");
        Assert.Contains(results, f => f.Text == "読む");
    }

    [Fact]
    public void Deconjugate_Passive_ContainsDictionaryForm()
    {
        // 読まれる (passive of 読む)
        var results = _deconjugator.Deconjugate("読まれる");
        Assert.Contains(results, f => f.Text == "読む");
    }

    [Fact]
    public void Deconjugate_Causative_ContainsDictionaryForm()
    {
        // 食べさせる (causative of 食べる)
        var results = _deconjugator.Deconjugate("食べさせる");
        Assert.Contains(results, f => f.Text == "食べる");
    }
}
