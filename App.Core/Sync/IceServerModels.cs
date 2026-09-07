using System.Text.Json;
using System.Text.Json.Serialization;

namespace App.Core.Sync;

public sealed class IceServersResponse
{
    public List<IceServerDto> IceServers { get; set; } = new();
}

public sealed class IceServerDto
{
    [JsonConverter(typeof(IceServerUrlsConverter))]
    public List<string> Urls { get; set; } = new();

    public string? Username { get; set; }

    public string? Credential { get; set; }
}

/// <summary>Cloudflare/WebRTC allow <c>urls</c> as a string or an array.</summary>
public sealed class IceServerUrlsConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return string.IsNullOrWhiteSpace(s) ? new List<string>() : new List<string> { s };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var s = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
                else
                    reader.Skip();
            }
            return list;
        }

        reader.Skip();
        return new List<string>();
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var url in value)
            writer.WriteStringValue(url);
        writer.WriteEndArray();
    }
}

public interface IIceServerSource
{
    Task<IReadOnlyList<IceServerDto>> GetIceServersAsync(CancellationToken ct = default);
}
