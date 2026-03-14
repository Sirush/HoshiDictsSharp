using System.Globalization;

namespace HoshiDictsSharp;

public sealed class LookupResult
{
    public string Matched = "";
    public string Deinflected = "";
    public IReadOnlyList<string> Process = [];
    public TermResult Term = new();
    public int PreprocessorSteps;
    public int MatchedTextLen;
}

public sealed class LookupEngine
{
    private readonly DictionaryQuery _query;
    private readonly Deconjugator _deconjugator;

    public LookupEngine(DictionaryQuery query, Deconjugator deconjugator)
    {
        _query = query;
        _deconjugator = deconjugator;
    }

    public List<LookupResult> Lookup(string lookupString, int maxResults = 16, int scanLength = 16)
    {
        var resultMap = new Dictionary<(string, string), LookupResult>();

        bool needsGraphemeClustering = false;
        for (int ci = 0; ci < lookupString.Length; ci++)
        {
            if (char.IsHighSurrogate(lookupString[ci]))
            { needsGraphemeClustering = true; break; }
        }

        int textLen;
        int[] positions;
        if (needsGraphemeClustering)
        {
            textLen = new StringInfo(lookupString).LengthInTextElements;
            var enumerator = StringInfo.GetTextElementEnumerator(lookupString);
            var posList = new List<int>(textLen + 1) { 0 };
            while (enumerator.MoveNext())
                posList.Add(enumerator.ElementIndex + enumerator.GetTextElement().Length);
            positions = [.. posList];
        }
        else
        {
            textLen = lookupString.Length;
            positions = new int[textLen + 1];
            for (int ci = 0; ci <= textLen; ci++)
                positions[ci] = ci;
        }

        int start = Math.Min(scanLength, textLen);

        var deduplicated = new Dictionary<(string, string), DeconjugationForm>();

        for (int i = start; i > 0; i--)
        {
            if (i >= positions.Length) continue;
            string searchStr = lookupString[..positions[i]];
            int searchStrTextLen = i;

            var variants = TextProcessor.Process(searchStr);
            foreach (var variant in variants)
            {
                var forms = _deconjugator.Deconjugate(variant.Text);

                deduplicated.Clear();
                foreach (var form in forms)
                {
                    string lastTag = form.Tags.Count > 0 ? form.Tags[^1] : "";
                    var key = (form.Text, lastTag);
                    if (!deduplicated.TryGetValue(key, out var existing) || form.Process.Count < existing.Process.Count)
                        deduplicated[key] = form;
                }

                foreach (var (_, form) in deduplicated)
                {
                    var terms = _query.Query(form.Text);
                    FilterByPos(terms, form);

                    foreach (var term in terms)
                    {
                        var resultKey = (term.Expression, term.Reading);
                        if (!resultMap.TryGetValue(resultKey, out var existing)
                            || searchStrTextLen > existing.MatchedTextLen)
                        {
                            resultMap[resultKey] = new LookupResult
                            {
                                Matched = searchStr,
                                Deinflected = form.Text,
                                Process = form.Process,
                                Term = term,
                                PreprocessorSteps = variant.Steps,
                                MatchedTextLen = searchStrTextLen
                            };
                        }
                    }
                }
            }
        }

        var results = resultMap.Values.ToList();
        var freqDictOrder = _query.GetFreqDictOrder();

        results.Sort((a, b) =>
        {
            if (a.MatchedTextLen != b.MatchedTextLen) return b.MatchedTextLen.CompareTo(a.MatchedTextLen);

            if (a.PreprocessorSteps != b.PreprocessorSteps)
                return a.PreprocessorSteps.CompareTo(b.PreprocessorSteps);

            if (a.Process.Count != b.Process.Count)
                return a.Process.Count.CompareTo(b.Process.Count);

            return FreqSortOrder(a, b, freqDictOrder);
        });

        if (results.Count > maxResults)
            results.RemoveRange(maxResults, results.Count - maxResults);

        return results;
    }

    private static void FilterByPos(List<TermResult> terms, DeconjugationForm form)
    {
        if (form.Tags.Count == 0) return;

        string tag = form.Tags[^1];
        for (int i = terms.Count - 1; i >= 0; i--)
        {
            if (!MatchesPos(terms[i].Rules, tag))
                terms.RemoveAt(i);
        }
    }

    private static bool MatchesPos(string rules, string tag)
    {
        if (string.IsNullOrEmpty(rules)) return false;
        var rulesSpan = rules.AsSpan();
        var tagSpan = tag.AsSpan();
        while (rulesSpan.Length > 0)
        {
            int spaceIdx = rulesSpan.IndexOf(' ');
            ReadOnlySpan<char> p = spaceIdx < 0 ? rulesSpan : rulesSpan[..spaceIdx];
            if (p.Length > 0 && (p.SequenceEqual(tagSpan) || tagSpan.StartsWith(p)))
                return true;
            rulesSpan = spaceIdx < 0 ? default : rulesSpan[(spaceIdx + 1)..];
        }
        return false;
    }

    private static int GetFreqValueForDict(TermResult term, string dictName)
    {
        foreach (var freqEntry in term.Frequencies)
        {
            if (freqEntry.DictName != dictName) continue;
            int min = int.MaxValue;
            foreach (var freq in freqEntry.Frequencies)
            {
                if (freq.Value >= 0)
                    min = Math.Min(min, freq.Value);
            }
            return min;
        }
        return int.MaxValue;
    }

    private static int FreqSortOrder(LookupResult a, LookupResult b, List<string> freqDictOrder)
    {
        foreach (var dictName in freqDictOrder)
        {
            int freqA = GetFreqValueForDict(a.Term, dictName);
            int freqB = GetFreqValueForDict(b.Term, dictName);
            if (freqA != freqB) return freqA.CompareTo(freqB);
        }
        return 0;
    }
}
