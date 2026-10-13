// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Api.Features.Reviewing.Runners;

/// <summary>Rejects malformed withheld evidence during JSON binding, before runner execution services are called.</summary>
internal sealed class WithheldReviewFindingJsonConverter : JsonConverter<WithheldReviewFinding>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override WithheldReviewFinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var payload = JsonSerializer.Deserialize<Payload>(ref reader, options)
                      ?? throw new JsonException("A withheld finding must contain a comment and a withholding reason.");
        try
        {
            return new WithheldReviewFinding(payload.Comment!, payload.ReasonCode!);
        }
        catch (ArgumentException exception)
        {
            // Domain constructor failures must become binding errors so malformed input receives HTTP 400.
            throw new JsonException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, WithheldReviewFinding value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value is null ? null : new Payload(value.Comment, value.ReasonCode), options);
    }

    private sealed record Payload(ReviewComment? Comment, string? ReasonCode);
}
