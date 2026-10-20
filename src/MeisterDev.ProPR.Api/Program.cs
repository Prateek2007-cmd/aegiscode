// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Api.Features.Reviewing.Runners;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

await ProPRHost.RunAsync(args);

/// <summary>Entry point for the API application used by tests and host.</summary>
public partial class Program
{
    private static readonly HttpClient HealthCheckHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    ///     Prevents direct instantiation. This class exists only as the entry-point marker type
    ///     (e.g. for <c>WebApplicationFactory&lt;Program&gt;</c> in integration tests) and to host
    ///     the static helpers below.
    /// </summary>
    protected Program()
    {
    }

    /// <summary>
    ///     Writes the identifier this installation reports itself under. Information level, because it is
    ///     startup information an operator looks for and not a fault. The logger type is written out in full
    ///     because this file also imports Serilog, which has a type of the same name.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Licensing identity {LicensingIdentity}. Quote it in usage reports and support requests; it grants nothing and no license is issued against it.")]
    internal static partial void LogLicensingIdentity(
        Microsoft.Extensions.Logging.ILogger logger,
        Guid licensingIdentity);

    /// <summary>
    ///     Whether the failure is PostgreSQL reporting that a table the query named does not exist, which 
    ///     a database without the migrations applied answers.
    ///     <para>
    ///         The chain is walked because the same condition arrives wrapped when it surfaces on a write: Entity
    ///         Framework raises <see cref="DbUpdateException" /> and carries the provider exception as its inner.
    ///     </para>
    /// </summary>
    internal static bool IsMissingRelation(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UndefinedTable })
            {
                return true;
            }
        }

        return false;
    }

    internal static async Task<bool> TryRunHealthCheckAsync(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--healthcheck", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var response = await HealthCheckHttpClient.GetAsync(args[1], HttpCompletionOption.ResponseHeadersRead);
            Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            Environment.ExitCode = 1;
        }

        return true;
    }

    internal static string GetEffectiveProCursorMode(IConfiguration configuration)
    {
        var mode = new ProCursorRemoteOptions
        {
            Mode = configuration["PROCURSOR_REMOTE_MODE"],
            ServiceBaseUrl = configuration["PROCURSOR_SERVICE_BASE_URL"],
            SharedKey = configuration["PROCURSOR_SHARED_KEY"],
        }.GetEffectiveMode();

        return mode switch
        {
            var value when IsRemoteProCursorMode(value) => ProCursorRemoteOptions.ProprManagedRemoteMode,
            var value when IsDisabledProCursorMode(value) => ProCursorRemoteOptions.DisabledMode,
            _ => ProCursorRemoteOptions.DisabledMode,
        };
    }

    internal static bool IsRemoteProCursorMode(string? mode)
    {
        return string.Equals(mode, ProCursorRemoteOptions.ProprManagedRemoteMode, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDisabledProCursorMode(string? mode)
    {
        return string.Equals(mode, ProCursorRemoteOptions.DisabledMode, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetSelectedCommentRelevanceFilterId()
    {
        return "hybrid-v1";
    }

    /// <summary>
    ///     Configures how the API reads and writes JSON. Enums travel as their camelCase names.
    ///     <para>
    ///         Order matters: the first converter that accepts a type handles it, so the capability override
    ///         state is registered ahead of the general enum converter and keeps its own refusal message. For
    ///         the states that enum defines the two write the same camelCase names; they differ on a value it
    ///         does not define, which the general converter writes as its number and the override converter
    ///         writes as the default name.
    ///     </para>
    /// </summary>
    internal static void ConfigureJsonSerialization(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Converters.Add(new WithheldReviewFindingJsonConverter());
        options.Converters.Add(new PremiumCapabilityOverrideStateJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }
}
