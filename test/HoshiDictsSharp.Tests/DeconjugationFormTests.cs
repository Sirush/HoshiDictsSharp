namespace HoshiDictsSharp.Tests;

public class DeconjugationFormTests
{
    [Fact]
    public void Equals_SameFields_ReturnsTrue()
    {
        var a = new DeconjugationForm("読む", "読んだ", ["v5m"], new HashSet<string>(StringComparer.Ordinal) { "読んだ", "読む" }, ["past"]);
        var b = new DeconjugationForm("読む", "読んだ", ["v5m"], new HashSet<string>(StringComparer.Ordinal) { "読んだ", "読む" }, ["past"]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentText_ReturnsFalse()
    {
        var a = new DeconjugationForm("読む", "読んだ", [], new HashSet<string>(StringComparer.Ordinal), []);
        var b = new DeconjugationForm("食べる", "読んだ", [], new HashSet<string>(StringComparer.Ordinal), []);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentTags_ReturnsFalse()
    {
        var a = new DeconjugationForm("読む", "読んだ", ["v5m"], new HashSet<string>(StringComparer.Ordinal), []);
        var b = new DeconjugationForm("読む", "読んだ", ["v1"], new HashSet<string>(StringComparer.Ordinal), []);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentProcess_ReturnsFalse()
    {
        var a = new DeconjugationForm("読む", "読んだ", [], new HashSet<string>(StringComparer.Ordinal), ["past"]);
        var b = new DeconjugationForm("読む", "読んだ", [], new HashSet<string>(StringComparer.Ordinal), ["negative"]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var a = new DeconjugationForm("読む", "読んだ", [], new HashSet<string>(StringComparer.Ordinal), []);
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        var a = new DeconjugationForm("読む", "読んだ", [], new HashSet<string>(StringComparer.Ordinal), []);
        Assert.True(a.Equals(a));
    }

    [Fact]
    public void Equals_SeenTextOrderIndependent()
    {
        var a = new DeconjugationForm("読む", "読んだ", [], new HashSet<string>(StringComparer.Ordinal) { "A", "B", "C" }, []);
        var b = new DeconjugationForm("読む", "読んだ", [], new HashSet<string>(StringComparer.Ordinal) { "C", "B", "A" }, []);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Properties_ReturnCorrectValues()
    {
        var form = new DeconjugationForm("読む", "読んだ", ["v5m", "past"], new HashSet<string>(StringComparer.Ordinal) { "X" }, ["step1"]);
        Assert.Equal("読む", form.Text);
        Assert.Equal("読んだ", form.OriginalText);
        Assert.Equal(2, form.Tags.Count);
        Assert.Equal("v5m", form.Tags[0]);
        Assert.Equal("past", form.Tags[1]);
        Assert.Single(form.Process);
        Assert.Equal("step1", form.Process[0]);
        Assert.Contains("X", form.SeenText);
    }
}
