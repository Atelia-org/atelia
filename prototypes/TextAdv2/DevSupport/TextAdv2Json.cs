using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.DevSupport;

public static class TextAdv2Json {
    public static void AddHostConverters(JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        options.Converters.Add(new TravelModeJsonConverter());
        options.Converters.Add(new RoutePlanStatusJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
    }

    private sealed class TravelModeJsonConverter : JsonConverter<TravelMode> {
        public override TravelMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => TravelModeCodec.FromStorageValue(reader.GetString() ?? throw new JsonException("Travel mode token is required."));

        public override void Write(Utf8JsonWriter writer, TravelMode value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToStorageValue());
    }

    private sealed class RoutePlanStatusJsonConverter : JsonConverter<RoutePlanStatus> {
        public override RoutePlanStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => RoutePlanStatusWireToken.Parse(reader.GetString() ?? throw new JsonException("Route plan status token is required."));

        public override void Write(Utf8JsonWriter writer, RoutePlanStatus value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToWireToken());
    }
}
