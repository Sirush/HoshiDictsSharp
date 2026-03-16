using System.Diagnostics;
using System.Text;
using HoshiDictsSharp;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 2)
{
    Console.Error.WriteLine($"Usage: {AppDomain.CurrentDomain.FriendlyName} <mode> <path> [options]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Modes:");
    Console.Error.WriteLine("  import <zip_path>                         Measure memory during import");
    Console.Error.WriteLine("  runtime <dict_path> [terms...]            Measure memory for load + query + lookup");
    return 1;
}

string mode = args[0];

return mode switch
{
    "import" => MeasureImport(args[1]),
    "runtime" => MeasureRuntime(args[1], args.Length > 2 ? args[2..] : null),
    _ => Error($"Unknown mode: {mode}")
};

static int MeasureImport(string zipPath)
{
    var proc = Process.GetCurrentProcess();

    ForceGC();
    long heapBefore = GC.GetTotalMemory(true);
    long workingSetBefore = proc.WorkingSet64;

    var sw = Stopwatch.StartNew();
    var result = DictionaryImporter.Import(zipPath, ".");
    sw.Stop();

    proc.Refresh();
    long heapAfter = GC.GetTotalMemory(false);
    long workingSetAfter = proc.WorkingSet64;
    long peakWorkingSet = proc.PeakWorkingSet64;

    if (!result.Success)
    {
        foreach (string error in result.Errors)
            Console.Error.WriteLine($"error: {error}");
        return 1;
    }

    Console.WriteLine("[import]");
    Console.WriteLine($"dict: {result.Title}");
    Console.WriteLine($"terms: {result.TermCount}");
    Console.WriteLine($"media: {result.MediaCount}");
    Console.WriteLine($"time: {sw.Elapsed.TotalMilliseconds:F2}ms");
    Console.WriteLine();
    PrintMemory("during import", heapBefore, heapAfter, workingSetBefore, workingSetAfter, peakWorkingSet);

    ForceGC();
    proc.Refresh();
    long heapSettled = GC.GetTotalMemory(true);
    long workingSetSettled = proc.WorkingSet64;

    Console.WriteLine();
    Console.WriteLine("[after GC]");
    Console.WriteLine($"gc_heap: {FormatBytes(heapSettled)}");
    Console.WriteLine($"working_set: {FormatBytes(workingSetSettled)}");

    try { Directory.Delete(result.Title, true); } catch { }

    return 0;
}

static int MeasureRuntime(string dictPath, string[]? customTerms)
{
    string[] terms = customTerms ??
    [
        "食べる", "読む", "読んでいる", "美しい", "学校",
        "カタカナ", "走った", "食べさせられた", "行く", "大きい",
        "見る", "書いている", "飲まなかった", "新しい", "先生", "分かる",
    ];

    var proc = Process.GetCurrentProcess();

    // --- Load ---
    ForceGC();
    long heapBeforeLoad = GC.GetTotalMemory(true);
    long wsBeforeLoad = proc.WorkingSet64;

    var swLoad = Stopwatch.StartNew();
    var query = new DictionaryQuery();
    query.AddTermDict(dictPath);
    var deconjugator = new Deconjugator();
    var lookup = new LookupEngine(query, deconjugator);
    swLoad.Stop();

    proc.Refresh();
    long heapAfterLoad = GC.GetTotalMemory(false);
    long wsAfterLoad = proc.WorkingSet64;
    long peakAfterLoad = proc.PeakWorkingSet64;

    Console.WriteLine("[load]");
    Console.WriteLine($"dict: {dictPath}");
    Console.WriteLine($"time: {swLoad.Elapsed.TotalMilliseconds:F2}ms");
    PrintMemory("load", heapBeforeLoad, heapAfterLoad, wsBeforeLoad, wsAfterLoad, peakAfterLoad);

    ForceGC();
    proc.Refresh();
    long heapLoadSettled = GC.GetTotalMemory(true);
    long wsLoadSettled = proc.WorkingSet64;
    Console.WriteLine();
    Console.WriteLine("[load settled]");
    Console.WriteLine($"gc_heap: {FormatBytes(heapLoadSettled)}");
    Console.WriteLine($"working_set: {FormatBytes(wsLoadSettled)}");

    // --- Query ---
    ForceGC();
    long heapBeforeQuery = GC.GetTotalMemory(true);

    int queryResultCount = 0;
    var swQuery = Stopwatch.StartNew();
    foreach (string term in terms)
        queryResultCount += query.Query(term).Count;
    swQuery.Stop();

    long heapAfterQuery = GC.GetTotalMemory(false);

    Console.WriteLine();
    Console.WriteLine("[query]");
    Console.WriteLine($"terms: {terms.Length}");
    Console.WriteLine($"results: {queryResultCount}");
    Console.WriteLine($"time: {swQuery.Elapsed.TotalMilliseconds:F2}ms");
    Console.WriteLine($"gc_heap_delta: {FormatBytes(heapAfterQuery - heapBeforeQuery)}");

    // --- Lookup ---
    ForceGC();
    long heapBeforeLookup = GC.GetTotalMemory(true);

    int lookupResultCount = 0;
    var swLookup = Stopwatch.StartNew();
    foreach (string term in terms)
        lookupResultCount += lookup.Lookup(term, 16, 16).Count;
    swLookup.Stop();

    long heapAfterLookup = GC.GetTotalMemory(false);

    Console.WriteLine();
    Console.WriteLine("[lookup]");
    Console.WriteLine($"terms: {terms.Length}");
    Console.WriteLine($"results: {lookupResultCount}");
    Console.WriteLine($"time: {swLookup.Elapsed.TotalMilliseconds:F2}ms");
    Console.WriteLine($"gc_heap_delta: {FormatBytes(heapAfterLookup - heapBeforeLookup)}");

    // --- GC stats ---
    Console.WriteLine();
    Console.WriteLine("[gc_stats]");
    for (int gen = 0; gen <= GC.MaxGeneration; gen++)
        Console.WriteLine($"gen{gen}_collections: {GC.CollectionCount(gen)}");
    Console.WriteLine($"total_gc_pause: {GC.GetTotalPauseDuration().TotalMilliseconds:F2}ms");

    proc.Refresh();
    Console.WriteLine();
    Console.WriteLine("[final]");
    Console.WriteLine($"gc_heap: {FormatBytes(GC.GetTotalMemory(true))}");
    Console.WriteLine($"working_set: {FormatBytes(proc.WorkingSet64)}");
    Console.WriteLine($"peak_working_set: {FormatBytes(proc.PeakWorkingSet64)}");

    return 0;
}

static void ForceGC()
{
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    GC.WaitForPendingFinalizers();
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
}

static void PrintMemory(string label, long heapBefore, long heapAfter, long wsBefore, long wsAfter, long peakWs)
{
    Console.WriteLine($"gc_heap_before: {FormatBytes(heapBefore)}");
    Console.WriteLine($"gc_heap_after: {FormatBytes(heapAfter)}");
    Console.WriteLine($"gc_heap_delta: {FormatBytes(heapAfter - heapBefore)}");
    Console.WriteLine($"working_set_before: {FormatBytes(wsBefore)}");
    Console.WriteLine($"working_set_after: {FormatBytes(wsAfter)}");
    Console.WriteLine($"working_set_delta: {FormatBytes(wsAfter - wsBefore)}");
    Console.WriteLine($"peak_working_set: {FormatBytes(peakWs)}");
}

static string FormatBytes(long bytes)
{
    double abs = Math.Abs(bytes);
    string sign = bytes < 0 ? "-" : "";
    return abs switch
    {
        >= 1024 * 1024 * 1024 => $"{sign}{abs / (1024 * 1024 * 1024):F2} GB",
        >= 1024 * 1024 => $"{sign}{abs / (1024 * 1024):F2} MB",
        >= 1024 => $"{sign}{abs / 1024:F2} KB",
        _ => $"{sign}{abs} B"
    };
}

static int Error(string msg)
{
    Console.Error.WriteLine(msg);
    return 1;
}
