using System.Diagnostics;
using System.Globalization;
using System.Text;
using HoshiDictsSharp;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 2)
{
    PrintUsage();
    return 1;
}

var sw = Stopwatch.StartNew();
string command = args[0];

try
{
    switch (command)
    {
        case "import" when args.Length >= 2:
            CmdImport(args[1]);
            break;
        case "deconjugate" when args.Length >= 2:
            CmdDeconjugate(args[1]);
            break;
        case "preprocess" when args.Length >= 2:
            CmdPreprocess(args[1]);
            break;
        case "query" when args.Length >= 3:
            CmdQuery(args[1], args[2]);
            break;
        case "lookup" when args.Length >= 3:
            CmdLookup(args[1..^1], args[^1]);
            break;
        case "freq" when args.Length >= 4:
            CmdFreq(args[1], args[2], args[3]);
            break;
        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

sw.Stop();
Console.WriteLine($"runtime: {sw.Elapsed.TotalMilliseconds:F1}ms");
return 0;

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  hoshidicts import <path/to/dictionary.zip>");
    Console.WriteLine("  hoshidicts deconjugate <word>");
    Console.WriteLine("  hoshidicts preprocess <word>");
    Console.WriteLine("  hoshidicts query <path/to/dictionary> <word>");
    Console.WriteLine("  hoshidicts lookup <path/to/dict1> [path/to/dict2...] <lookup_string>");
    Console.WriteLine("  hoshidicts freq <path/to/freq_dict> <expression> <reading>");
}

static void CmdImport(string zipPath)
{
    string outputDir = Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? ".";
    var result = DictionaryImporter.Import(zipPath, outputDir);

    if (result.Success)
    {
        Console.WriteLine($"title: {result.Title}");
        Console.WriteLine($"term_count: {result.TermCount}");
        Console.WriteLine($"meta_count: {result.MetaCount}");
        Console.WriteLine($"media_count: {result.MediaCount}");
    }
    else
    {
        Console.Error.WriteLine("could not import dictionary:");
        foreach (var error in result.Errors)
            Console.Error.WriteLine($" {error}");
    }
}

static void CmdDeconjugate(string text)
{
    var deconjugator = new Deconjugator();
    var results = deconjugator.Deconjugate(text);

    int charLen = new StringInfo(text).LengthInTextElements;
    Console.WriteLine($"deconjugations for: {text} length: {charLen}");
    Console.WriteLine($"found {results.Count} candidates");
    Console.WriteLine();

    foreach (var r in results)
    {
        Console.WriteLine(r.Text);
        if (r.Tags.Count > 0)
            Console.WriteLine($"  tags: {string.Join(", ", r.Tags)}");
        if (r.Process.Count > 0)
            Console.WriteLine($"  process: {string.Join(" -> ", r.Process)}");
    }
}

static void CmdPreprocess(string text)
{
    var results = TextProcessor.Process(text);

    int charLen = new StringInfo(text).LengthInTextElements;
    Console.WriteLine($"preprocessing for: {text} length: {charLen}");
    Console.WriteLine($"found {results.Count} variants");

    foreach (var r in results)
        Console.WriteLine(r.Text);
}

static void CmdQuery(string dbPath, string expression)
{
    var query = new DictionaryQuery();
    query.AddTermDict(dbPath);
    var results = query.Query(expression);

    int charLen = new StringInfo(expression).LengthInTextElements;
    Console.WriteLine($"query results for: {expression} length: {charLen}");
    Console.WriteLine($"{results.Count} entries");

    foreach (var r in results)
    {
        Console.WriteLine("---------------------------------------------------------------");
        Console.WriteLine($"{r.Expression} {r.Reading} {r.Rules}");
        Console.WriteLine($"{r.Glossaries.Count} glossary entries");
        foreach (var g in r.Glossaries)
        {
            Console.WriteLine("------");
            Console.WriteLine(g.DictName);
            Console.WriteLine(g.Glossary);
        }
    }
}

static void CmdFreq(string path, string expression, string reading)
{
    var terms = new List<TermResult>
    {
        new() { Expression = expression, Reading = reading }
    };

    var query = new DictionaryQuery();
    query.AddFreqDict(path);
    query.QueryFreq(terms);

    Console.WriteLine($"frequency entries for: {expression}");
    int count = 0;
    foreach (var freq in terms[0].Frequencies)
    {
        Console.WriteLine($"dict: {freq.DictName}");
        foreach (var f in freq.Frequencies)
        {
            Console.WriteLine($"val: {f.Value} display_val: {f.DisplayValue}");
            count++;
        }
    }
    Console.WriteLine($"count: {count}");
}

static void CmdLookup(string[] dbPaths, string lookupString)
{
    var query = new DictionaryQuery();
    foreach (var path in dbPaths)
        query.AddTermDict(path);

    var deconjugator = new Deconjugator();
    var lookup = new LookupEngine(query, deconjugator);
    var results = lookup.Lookup(lookupString, 8, 16);

    Console.WriteLine($"lookup results for: {lookupString} max_results: 8 scan_length: 16");
    Console.WriteLine($"{results.Count} results");

    foreach (var r in results)
    {
        Console.WriteLine("---------------------------------------------------------------");
        Console.WriteLine(r.Matched);
        if (r.Process.Count > 0)
            Console.WriteLine($"  {string.Join(" -> ", r.Process)}");
        Console.WriteLine($"{r.Term.Expression} {r.Term.Reading}");
        foreach (var g in r.Term.Glossaries)
        {
            Console.WriteLine("------");
            Console.WriteLine(g.DictName);
            Console.WriteLine(g.Glossary);
        }
    }

    Console.WriteLine("styles: ");
    foreach (var (name, css) in query.GetStyles())
    {
        Console.WriteLine(name);
        Console.WriteLine(css);
    }
}
