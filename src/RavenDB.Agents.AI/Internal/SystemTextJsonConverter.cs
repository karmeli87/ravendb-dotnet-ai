using System.Text.Json;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;

namespace RavenDB.Agents.AI.Internal;

internal sealed class SystemTextJsonConverter : Newtonsoft.Json.JsonConverter
{
    public static JToken ToToken(object? value, Type type)
    {
        var json = JsonSerializer.Serialize(value, type, AIJsonUtilities.DefaultOptions);
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
        return JToken.ReadFrom(reader);
    }

    public override bool CanConvert(Type objectType) => true;

    public override void WriteJson(Newtonsoft.Json.JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer) =>
        ToToken(value, value?.GetType() ?? typeof(object)).WriteTo(writer);

    public override object? ReadJson(Newtonsoft.Json.JsonReader reader, Type objectType, object? existingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        using var json = new StringWriter();
        using (var writer = new Newtonsoft.Json.JsonTextWriter(json))
        {
            writer.WriteToken(reader);
        }

        return JsonSerializer.Deserialize(json.ToString(), objectType, AIJsonUtilities.DefaultOptions);
    }
}
