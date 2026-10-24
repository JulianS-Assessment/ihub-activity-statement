using Microsoft.AspNetCore.Mvc;

namespace IHub.Statements.Api.Activities;

public sealed record IngestActivityRequest(string IntermediaryId, [FromBody] IntermediaryActivity Activity)
{
    public IngestActivityRequest WithTrimmedIds() => this with
    {
        IntermediaryId = IntermediaryId.Trim(),
        Activity = Activity with { Source = Activity.Source.Trim(), ActivityId = Activity.ActivityId.Trim() }
    };
}
