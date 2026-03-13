using System.Globalization;

namespace HoshiDictsSharp;

public sealed class LookupResult
{
    public string Matched = "";
    public string Deinflected = "";
    public List<string> Process = [];
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

        int textLen = new StringInfo(lookupString).LengthInTextElements;
        int start = Math.Min(scanLength, textLen);

        var enumerator = StringInfo.GetTextElementEnumerator(lookupString);
        var positions = new List<int>(start + 1);
        positions.Add(0);
        while (enumerator.MoveNext())
            positions.Add(enumerator.ElementIndex + enumerator.GetTextElement().Length);

        for (int i = start; i > 0; i--)
        {
            if (i >= positions.Count) continue;
            string searchStr = lookupString[..positions[i]];
            int searchStrTextLen = i;

            var variants = TextProcessor.Process(searchStr);
            foreach (var variant in variants)
            {
                var forms = _deconjugator.Deconjugate(variant.Text);

                var deduplicated = new Dictionary<(string, string), DeconjugationForm>();
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
                                Process = form.Process.ToList(),
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
        terms.RemoveAll(term =>
        {
            if (string.IsNullOrEmpty(term.Rules)) return true;
            var posTags = term.Rules.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return !posTags.Any(p => p == tag || tag.StartsWith(p, StringComparison.Ordinal));
        });
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
