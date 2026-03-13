namespace HoshiDictsSharp;

public sealed class ImportResult
{
    public bool Success { get; set; }
    public string Title { get; set; } = "";
    public int TermCount { get; set; }
    public int MetaCount { get; set; }
    public int MediaCount { get; set; }
    public List<string> Errors { get; } = [];
}
