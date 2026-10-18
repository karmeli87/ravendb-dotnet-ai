using Microsoft.Extensions.AI;
using Newtonsoft.Json;

namespace RavenDB.Extensions.VectorData.Internal;

internal sealed class VectorJsonConverter : JsonConverter
{
    public static readonly VectorJsonConverter Instance = new();

    public override bool CanConvert(Type objectType) =>
        objectType == typeof(ReadOnlyMemory<float>) || objectType == typeof(ReadOnlyMemory<float>?) || objectType == typeof(Embedding<float>);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        var vector = value switch
        {
            ReadOnlyMemory<float> memory => memory,
            Embedding<float> embedding => embedding.Vector,
            _ => (ReadOnlyMemory<float>?)null,
        };

        if (vector is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartArray();
        foreach (var number in vector.Value.Span)
        {
            writer.WriteValue(number);
        }

        writer.WriteEndArray();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return objectType == typeof(ReadOnlyMemory<float>) ? default(ReadOnlyMemory<float>) : (object?)null;
        }

        var numbers = serializer.Deserialize<float[]>(reader)!;
        return objectType == typeof(Embedding<float>) ? new Embedding<float>(numbers) : new ReadOnlyMemory<float>(numbers);
    }
}
