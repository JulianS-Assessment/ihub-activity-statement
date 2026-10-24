using System.Globalization;

namespace IHub.Statements.Api;

// Single-field checks shared by both endpoints; rules that combine fields or need the clock stay in the endpoint
// that owns them.
public static class Validation
{
    public static void RequireIdentifier(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (value?.Trim().Length is not (>= 1 and <= 64))
        {
            errors[field] = ["Is required and must be 1 to 64 characters after trimming."];
        }
    }

    public static DateTimeOffset? RequireDateTimeOffset(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
        {
            errors[field] = ["Is required and must be a timestamp, for example 2026-10-01T00:00:00+02:00."];
            return null;
        }

        // DateTimeOffset.TryParse reads a timestamp without an offset as local time, so DateTime.Kind tells whether one was given.
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) || parsed.Kind == DateTimeKind.Unspecified)
        {
            errors[field] = ["Needs an explicit offset, for example 2026-10-01T00:00:00+02:00."];
            return null;
        }

        if (timestamp.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            errors[field] = ["Must have at most six fractional digits."];
            return null;
        }

        return timestamp.ToUniversalTime();
    }

    public static void Report(Dictionary<string, string[]> errors, string field, string? error)
    {
        if (error is not null)
        {
            errors[field] = [error];
        }
    }
}
