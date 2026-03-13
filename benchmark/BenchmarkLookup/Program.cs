using System.Diagnostics;
using System.Text;
using HoshiDictsSharp;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 2)
{
    Console.Error.WriteLine($"{AppDomain.CurrentDomain.FriendlyName} <dict_path> <iterations> [terms...]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  dict_path   path to an imported dictionary folder");
    Console.Error.WriteLine("  iterations  number of benchmark iterations");
    Console.Error.WriteLine("  terms       lookup terms (optional, defaults to built-in set)");
    return 1;
}

string dictPath = args[0];
int iterations = int.Parse(args[1]);

string[] terms = args.Length > 2
    ? args[2..]
    : [
        "食べる",
        "読む",
        "読んでいる",
        "美しい",
        "学校",
        "カタカナ",
        "走った",
        "食べさせられた",
        "行く",
        "大きい",
        "見る",
        "書いている",
        "飲まなかった",
        "新しい",
        "先生",
        "分かる",
    ];

var swLoad = Stopwatch.StartNew();
var query = new DictionaryQuery();
query.AddTermDict(dictPath);
var deconjugator = new Deconjugator();
var lookup = new LookupEngine(query, deconjugator);
swLoad.Stop();

Console.WriteLine($"dict: {dictPath}");
Console.WriteLine($"terms: {terms.Length}");
Console.WriteLine($"iterations: {iterations}");
Console.WriteLine($"load: {swLoad.Elapsed.TotalMilliseconds:F2}ms");
Console.WriteLine();

// -- Query benchmark (direct hash lookup, no deconjugation) --
var queryDurations = new List<double>(iterations);
int totalQueryResults = 0;

for (int i = 0; i < iterations; i++)
{
    int resultCount = 0;
    var sw = Stopwatch.StartNew();
    foreach (string term in terms)
    {
        var results = query.Query(term);
        resultCount += results.Count;
    }
    sw.Stop();
    queryDurations.Add(sw.Elapsed.TotalMilliseconds);
    if (i == 0) totalQueryResults = resultCount;
}

Console.WriteLine("[query]");
Console.WriteLine($"results: {totalQueryResults}");
PrintStats(queryDurations);
Console.WriteLine();

// -- Lookup benchmark (full pipeline: text processing + deconjugation + query) --
var lookupDurations = new List<double>(iterations);
int totalLookupResults = 0;

for (int i = 0; i < iterations; i++)
{
    int resultCount = 0;
    var sw = Stopwatch.StartNew();
    foreach (string term in terms)
    {
        var results = lookup.Lookup(term, 16, 16);
        resultCount += results.Count;
    }
    sw.Stop();
    lookupDurations.Add(sw.Elapsed.TotalMilliseconds);
    if (i == 0) totalLookupResults = resultCount;
}

Console.WriteLine("[lookup]");
Console.WriteLine($"results: {totalLookupResults}");
PrintStats(lookupDurations);

return 0;

static void PrintStats(List<double> durations)
{
    double min = durations.Min();
    double max = durations.Max();
    double total = durations.Sum();
    double avg = total / durations.Count;

    Console.WriteLine($"total: {total:F2}ms");
    Console.WriteLine($"avg: {avg:F2}ms");
    Console.WriteLine($"min: {min:F2}ms");
    Console.WriteLine($"max: {max:F2}ms");
}
