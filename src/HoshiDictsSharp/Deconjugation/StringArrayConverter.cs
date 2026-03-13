using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoshiDictsSharp;

public class StringArrayConverter : JsonConverter<string[]>
{
    public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString()!];
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;

                if (reader.TokenType == JsonTokenType.String)
                    list.Add(reader.GetString()!);
                else
                    throw new JsonException("Unexpected token in array when deserializing string array.");
            }
            return list.ToArray();
        }

        throw new JsonException($"Unexpected token: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var str in value)
            writer.WriteStringValue(str);
        writer.WriteEndArray();
    }
}
