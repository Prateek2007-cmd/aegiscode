// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

public static class DiscussionFingerprints
{
    public static string CanonicalValue(object? value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string CanonicalThread(PrCommentThread thread)
    {
        var normalized = thread with
        {
            Comments = thread.Comments.OrderBy(comment => comment.CommentId).ToArray(),
            SourceAnchor = CanonicalAnchor(thread.SourceAnchor)
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(normalized));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? CanonicalAnchor(string? anchor)
    {
        if (anchor is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(anchor);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Write(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return anchor;
        }
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(property.Value, writer);
            }

            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                Write(item, writer);
            }

            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }
}
