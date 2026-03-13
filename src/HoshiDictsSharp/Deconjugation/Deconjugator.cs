using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

namespace HoshiDictsSharp;

[JsonSerializable(typeof(List<DeconjugationRule>))]
[JsonSourceGenerationOptions(
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    Converters = [typeof(StringArrayConverter)])]
internal partial class DeconjugatorJsonContext : JsonSerializerContext;

public class Deconjugator
{
    private readonly DeconjugationRule[] _rules;
    private readonly Dictionary<DeconjugationRule, DeconjugationVirtualRule[]> _virtualRulesCache = new();
    private readonly Dictionary<char, int[]> _rulesByLastChar = new();
    private readonly int[] _universalRuleIndices;

    public Deconjugator()
    {
        using var stream = typeof(Deconjugator).Assembly.GetManifestResourceStream("HoshiDictsSharp.resources.deconjugator.json")
            ?? throw new InvalidOperationException("Embedded resource deconjugator.json not found");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        _rules = (JsonSerializer.Deserialize(json, DeconjugatorJsonContext.Default.ListDeconjugationRule) ?? []).ToArray();

        foreach (var rule in _rules)
            CacheVirtualRules(rule);

        var universalIndices = new List<int>();
        var charBuckets = new Dictionary<char, List<int>>();

        for (int i = 0; i < _rules.Length; i++)
        {
            var rule = _rules[i];
            if (rule.Type == "substitution")
            {
                universalIndices.Add(i);
                continue;
            }

            bool hasEmptyConEnd = false;
            var addedChars = new HashSet<char>();
            foreach (var conEnd in rule.ConEnd)
            {
                if (conEnd.Length == 0)
                    hasEmptyConEnd = true;
                else if (addedChars.Add(conEnd[^1]))
                {
                    if (!charBuckets.TryGetValue(conEnd[^1], out var list))
                    {
                        list = [];
                        charBuckets[conEnd[^1]] = list;
                    }
                    list.Add(i);
                }
            }

            if (hasEmptyConEnd)
                universalIndices.Add(i);
        }

        _universalRuleIndices = universalIndices.ToArray();
        foreach (var (c, list) in charBuckets)
            _rulesByLastChar[c] = list.ToArray();
    }

    private void CacheVirtualRules(DeconjugationRule rule)
    {
        if (rule.DecEnd.Length <= 1) return;

        var virtualRules = new DeconjugationVirtualRule[rule.DecEnd.Length];
        for (int i = 0; i < rule.DecEnd.Length; i++)
        {
            virtualRules[i] = new DeconjugationVirtualRule(
                rule.DecEnd.ElementAtOrDefault(i) ?? rule.DecEnd[0],
                rule.ConEnd.ElementAtOrDefault(i) ?? rule.ConEnd[0],
                rule.DecTag?.ElementAtOrDefault(i) ?? rule.DecTag?[0],
                rule.ConTag?.ElementAtOrDefault(i) ?? rule.ConTag?[0],
                rule.Detail
            );
        }
        _virtualRulesCache[rule] = virtualRules;
    }

    public IReadOnlyList<DeconjugationForm> Deconjugate(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var processed = new Dictionary<(string, string), DeconjugationForm>(Math.Min(text.Length * 2, 100));
        var novel = new Dictionary<(string, string), DeconjugationForm>(20);
        novel[(text, "")] = new DeconjugationForm(text, text, [], []);

        var ruleOutput = new List<DeconjugationForm>(8);

        while (novel.Count > 0)
        {
            var newNovel = new Dictionary<(string, string), DeconjugationForm>(novel.Count * 2);

            foreach (var (_, form) in novel)
            {
                if (ShouldSkipForm(form))
                    continue;

                if (_rulesByLastChar.TryGetValue(form.Text[^1], out var suffixRules))
                {
                    foreach (int ruleIdx in suffixRules)
                    {
                        ruleOutput.Clear();
                        ApplyRule(form, _rules[ruleIdx], ruleOutput);
                        AddNovel(ruleOutput, processed, novel, newNovel);
                    }
                }

                foreach (int ruleIdx in _universalRuleIndices)
                {
                    ruleOutput.Clear();
                    ApplyRule(form, _rules[ruleIdx], ruleOutput);
                    AddNovel(ruleOutput, processed, novel, newNovel);
                }
            }

            foreach (var kv in novel)
                processed.TryAdd(kv.Key, kv.Value);
            novel = newNovel;
        }

        var result = new List<DeconjugationForm>(processed.Count);
        result.AddRange(processed.Values);
        result.Sort((a, b) =>
        {
            int cmp = b.Text.Length.CompareTo(a.Text.Length);
            return cmp != 0 ? cmp : string.Compare(a.Text, b.Text, StringComparison.Ordinal);
        });
        return result;
    }

    private static void AddNovel(List<DeconjugationForm> output,
        Dictionary<(string, string), DeconjugationForm> processed,
        Dictionary<(string, string), DeconjugationForm> novel,
        Dictionary<(string, string), DeconjugationForm> newNovel)
    {
        foreach (var f in output)
        {
            string lastTag = f.Tags.Count > 0 ? f.Tags[^1] : "";
            var key = (f.Text, lastTag);
            if (!processed.ContainsKey(key) && !novel.ContainsKey(key) && !newNovel.ContainsKey(key))
                newNovel[key] = f;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyRule(DeconjugationForm form, DeconjugationRule rule, List<DeconjugationForm> output)
    {
        switch (rule.Type)
        {
            case "stdrule": StdRuleDeconjugate(form, rule, output); break;
            case "rewriterule": RewriteRuleDeconjugate(form, rule, output); break;
            case "onlyfinalrule": OnlyFinalRuleDeconjugate(form, rule, output); break;
            case "neverfinalrule": NeverFinalRuleDeconjugate(form, rule, output); break;
            case "contextrule": ContextRuleDeconjugate(form, rule, output); break;
            case "substitution": SubstitutionDeconjugate(form, rule, output); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldSkipForm(DeconjugationForm form)
    {
        return string.IsNullOrEmpty(form.Text) ||
               form.Text.Length > form.OriginalText.Length + 10 ||
               form.Tags.Count > form.OriginalText.Length + 6;
    }

    private void StdRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule, List<DeconjugationForm> output)
    {
        if (string.IsNullOrEmpty(rule.Detail) && form.Tags.Count == 0)
            return;

        if (rule.DecEnd.Length == 1)
        {
            var virtualRule = new DeconjugationVirtualRule(
                rule.DecEnd[0], rule.ConEnd[0],
                rule.DecTag?[0], rule.ConTag?[0], rule.Detail);

            if (StdRuleDeconjugateInner(form, virtualRule) is { } hit)
                output.Add(hit);

            return;
        }

        if (!_virtualRulesCache.TryGetValue(rule, out var cachedVirtualRules))
            return;

        foreach (var virtualRule in cachedVirtualRules)
        {
            if (StdRuleDeconjugateInner(form, virtualRule) is { } hit)
                output.Add(hit);
        }
    }

    private DeconjugationForm? StdRuleDeconjugateInner(DeconjugationForm form, DeconjugationVirtualRule rule)
    {
        if (!form.Text.EndsWith(rule.ConEnd, StringComparison.Ordinal))
            return null;

        if (form.Tags.Count > 0 && form.Tags[^1] != rule.ConTag)
            return null;

        int prefixLength = form.Text.Length - rule.ConEnd.Length;

        Span<char> buffer = stackalloc char[prefixLength + rule.DecEnd.Length];
        form.Text.AsSpan(0, prefixLength).CopyTo(buffer);
        rule.DecEnd.AsSpan().CopyTo(buffer[prefixLength..]);
        var newText = new string(buffer);

        if (newText.Equals(form.OriginalText, StringComparison.Ordinal))
            return null;

        return CreateNewForm(form, newText, rule.ConTag, rule.DecTag, rule.Detail);
    }

    private DeconjugationForm CreateNewForm(DeconjugationForm form, string newText, string? conTag, string? decTag, string detail)
    {
        int existingTagCount = form.Tags.Count;
        bool addConTag = existingTagCount == 0 && conTag != null;
        bool addDecTag = decTag != null;
        int newTagCount = existingTagCount + (addConTag ? 1 : 0) + (addDecTag ? 1 : 0);

        var tags = new string[newTagCount];
        for (int i = 0; i < existingTagCount; i++)
            tags[i] = form.Tags[i];
        int idx = existingTagCount;
        if (addConTag) tags[idx++] = conTag!;
        if (addDecTag) tags[idx++] = decTag!;

        int procCount = form.Process.Count;
        var process = new string[procCount + 1];
        for (int i = 0; i < procCount; i++)
            process[i] = form.Process[i];
        process[procCount] = detail;

        return new DeconjugationForm(newText, form.OriginalText, tags, process);
    }

    private void SubstitutionDeconjugate(DeconjugationForm form, DeconjugationRule rule, List<DeconjugationForm> output)
    {
        if (form.Process.Count != 0 || string.IsNullOrEmpty(form.Text))
            return;

        if (rule.DecEnd.Length == 1)
        {
            if (SubstitutionInner(form, rule.ConEnd[0], rule.DecEnd[0], rule.Detail) is { } hit)
                output.Add(hit);
            return;
        }

        for (int i = 0; i < rule.DecEnd.Length; i++)
        {
            var decEnd = rule.DecEnd.ElementAtOrDefault(i) ?? rule.DecEnd[0];
            var conEnd = rule.ConEnd.ElementAtOrDefault(i) ?? rule.ConEnd[0];

            if (SubstitutionInner(form, conEnd, decEnd, rule.Detail) is { } ret)
                output.Add(ret);
        }
    }

    private DeconjugationForm? SubstitutionInner(DeconjugationForm form, string conEnd, string decEnd, string detail)
    {
        if (!form.Text.Contains(conEnd, StringComparison.Ordinal))
            return null;

        var newText = form.Text.Replace(conEnd, decEnd, StringComparison.Ordinal);

        int procCount = form.Process.Count;
        var process = new string[procCount + 1];
        for (int i = 0; i < procCount; i++)
            process[i] = form.Process[i];
        process[procCount] = detail;

        int tagCount = form.Tags.Count;
        var tags = new string[tagCount];
        for (int i = 0; i < tagCount; i++)
            tags[i] = form.Tags[i];

        return new DeconjugationForm(newText, form.OriginalText, tags, process);
    }

    private void RewriteRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule, List<DeconjugationForm> output)
    {
        if (form.Text.Equals(rule.ConEnd[0], StringComparison.Ordinal))
            StdRuleDeconjugate(form, rule, output);
    }

    private void OnlyFinalRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule, List<DeconjugationForm> output)
    {
        if (form.Tags.Count == 0)
            StdRuleDeconjugate(form, rule, output);
    }

    private void NeverFinalRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule, List<DeconjugationForm> output)
    {
        if (form.Tags.Count != 0)
            StdRuleDeconjugate(form, rule, output);
    }

    private void ContextRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule, List<DeconjugationForm> output)
    {
        if (rule.ContextRule == "v1inftrap" && !V1InfTrapCheck(form))
            return;

        if (rule.ContextRule == "saspecial" && !SaSpecialCheck(form, rule))
            return;

        if (rule.ContextRule == "temirurule" && !TemiruCheck(form, rule))
            return;

        StdRuleDeconjugate(form, rule, output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TemiruCheck(DeconjugationForm form, DeconjugationRule rule)
    {
        var conEnd = rule.ConEnd[0];
        if (!form.Text.EndsWith(conEnd, StringComparison.Ordinal)) return false;
        var prefix = form.Text.AsSpan(0, form.Text.Length - conEnd.Length);
        return prefix.EndsWith("て") || prefix.EndsWith("で");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool V1InfTrapCheck(DeconjugationForm form)
    {
        return !(form.Tags is ["stem-ren"]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SaSpecialCheck(DeconjugationForm form, DeconjugationRule rule)
    {
        if (form.Text.Length == 0) return false;

        var conEnd = rule.ConEnd[0];
        if (!form.Text.EndsWith(conEnd, StringComparison.Ordinal)) return false;

        var prefixLength = form.Text.Length - conEnd.Length;
        return prefixLength <= 0 || !form.Text.AsSpan(prefixLength - 1, 1).SequenceEqual("さ".AsSpan());
    }
}
