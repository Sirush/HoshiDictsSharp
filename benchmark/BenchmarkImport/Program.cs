using System.Diagnostics;
using HoshiDictsSharp;

if (args.Length < 2)
{
    Console.Error.WriteLine($"{AppDomain.CurrentDomain.FriendlyName} <zip_path> <iterations>");
    return 1;
}

string zipPath = args[0];
int iterations = int.Parse(args[1]);
var durations = new List<double>(iterations);
string dictTitle = "";
int termCount = 0;
int mediaCount = 0;

for (int i = 0; i < iterations; i++)
{
    var sw = Stopwatch.StartNew();
    var result = DictionaryImporter.Import(zipPath, ".");
    sw.Stop();

    if (!result.Success)
    {
        foreach (string error in result.Errors)
            Console.Error.WriteLine($"error: {error}");
        continue;
    }

    if (string.IsNullOrEmpty(dictTitle))
        dictTitle = result.Title;

    if (termCount == 0)
    {
        termCount = result.TermCount;
        mediaCount = result.MediaCount;
    }

    durations.Add(sw.Elapsed.TotalMilliseconds);

    try { Directory.Delete(result.Title, true); } catch { }
}

if (durations.Count == 0)
    return 1;

double min = durations.Min();
double max = durations.Max();
double total = durations.Sum();
double avg = total / durations.Count;

Console.WriteLine($"dict: {dictTitle} iterations: {iterations}");
Console.WriteLine($"term_count: {termCount}");
Console.WriteLine($"media_count: {mediaCount}");
Console.WriteLine($"total: {total:F2}ms");
Console.WriteLine($"avg: {avg:F2}ms");
Console.WriteLine($"min: {min:F2}ms");
Console.WriteLine($"max: {max:F2}ms");

return 0;
