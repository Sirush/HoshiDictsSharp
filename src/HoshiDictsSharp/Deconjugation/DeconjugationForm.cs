namespace HoshiDictsSharp;

public sealed class DeconjugationForm : IEquatable<DeconjugationForm>
{
    private readonly int _hashCode;

    public IReadOnlyList<string> Tags { get; }
    public string Text { get; }
    public string OriginalText { get; }
    public IReadOnlyList<string> Process { get; }

    internal DeconjugationForm(
        string text,
        string originalText,
        string[] tags,
        string[] process)
    {
        Text = text;
        OriginalText = originalText;
        Tags = tags;
        Process = process;

        _hashCode = ComputeHash(text, originalText, tags, process);
    }

    private static int ComputeHash(string text, string originalText, string[] tags, string[] process)
    {
        var hash = new HashCode();
        hash.Add(text, StringComparer.Ordinal);
        hash.Add(originalText, StringComparer.Ordinal);

        foreach (var tag in tags)
            hash.Add(tag, StringComparer.Ordinal);

        foreach (var step in process)
            hash.Add(step, StringComparer.Ordinal);

        return hash.ToHashCode();
    }

    public bool Equals(DeconjugationForm? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || _hashCode != other._hashCode)
            return false;

        return Text == other.Text &&
               OriginalText == other.OriginalText &&
               SequenceEqual(Tags, other.Tags) &&
               SequenceEqual(Process, other.Process);
    }

    public override bool Equals(object? obj) => Equals(obj as DeconjugationForm);

    public override int GetHashCode() => _hashCode;

    private static bool SequenceEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
}
