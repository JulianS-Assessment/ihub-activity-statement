using System.Text.Json;
using System.Text.Json.Serialization;

namespace IHub.Statements.Api.Activities;

// The default converter reads a timestamp without an offset as local time; a source's local time read as UTC
// lands two hours off in a statement window, so a timestamp without an offset is refused as unreadable.
public sealed class OffsetRequiredDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime().Kind == DateTimeKind.Unspecified
            ? throw new JsonException("A timestamp needs an explicit offset, for example 2026-10-01T10:00:00+02:00.")
            : reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
