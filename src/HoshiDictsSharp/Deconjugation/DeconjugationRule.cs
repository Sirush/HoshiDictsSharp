using System.Text.Json.Serialization;

namespace HoshiDictsSharp;

public class DeconjugationRule(
    string type,
    string? contextRule,
    string[] decEnd,
    string[] conEnd,
    string[]? decTag,
    string[]? conTag,
    string detail)
{
    [JsonPropertyName("type")] public string Type { get; set; } = type;
    [JsonPropertyName("contextrule")] public string? ContextRule { get; set; } = contextRule;
    [JsonPropertyName("dec_end")] public string[] DecEnd { get; set; } = decEnd;
    [JsonPropertyName("con_end")] public string[] ConEnd { get; set; } = conEnd;
    [JsonPropertyName("dec_tag")] public string[]? DecTag { get; set; } = decTag;
    [JsonPropertyName("con_tag")] public string[]? ConTag { get; set; } = conTag;
    [JsonPropertyName("detail")] public string Detail { get; set; } = detail;
}
