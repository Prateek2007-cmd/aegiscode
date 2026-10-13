// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MeisterDev.ProPR.Api.OpenApi;

/// <summary>Describes the constructor constraints enforced when withheld evidence is submitted.</summary>
public sealed class WithheldReviewFindingSchemaFilter : ISchemaFilter
{
    /// <inheritdoc />
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(WithheldReviewFinding) || schema is not OpenApiSchema { Properties: not null } finding)
        {
            return;
        }

        finding.Required ??= new HashSet<string>(StringComparer.Ordinal);
        finding.Required.Add("comment");
        finding.Required.Add("reasonCode");
        if (finding.Properties["reasonCode"] is OpenApiSchema reason)
        {
            reason.Type = JsonSchemaType.String;
            reason.MinLength = 1;
            reason.MaxLength = WithheldReviewFinding.MaximumReasonCodeLength;
            reason.Pattern = @"\S";
        }
    }
}
