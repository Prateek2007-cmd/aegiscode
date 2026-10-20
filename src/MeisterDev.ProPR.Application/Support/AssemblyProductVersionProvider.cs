// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Support;

/// <summary>Reads the running release version from the assembly the release build stamps.</summary>
public sealed class AssemblyProductVersionProvider : IProductVersionProvider
{
    /// <summary>
    ///     Reported when nothing stamped a version, which is every build outside the release pipeline.
    ///     <para>
    ///         A local build has no release number. Reporting the SDK default of <c>1.0.0</c> would put a
    ///         version that was never released into the usage statistics and into support conversations.
    ///     </para>
    /// </summary>
    public const string UnstampedVersion = "0.0.0-dev";

    private readonly string version;

    /// <summary>Creates a provider reading the version stamped into the ProPR assemblies.</summary>
    /// <remarks>
    ///     The release build stamps every ProPR assembly with the same version. The entry assembly is not read,
    ///     because the process can start from another assembly, such as a test host, that carries a version of its own.
    /// </remarks>
    public AssemblyProductVersionProvider()
        : this(typeof(AssemblyProductVersionProvider).Assembly)
    {
    }

    /// <summary>Creates a provider reading the version stamped into <paramref name="assembly" />.</summary>
    public AssemblyProductVersionProvider(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        this.version = Normalize(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    /// <inheritdoc />
    public string Version => this.version;

    /// <summary>
    ///     Strips the source-revision suffix the SDK appends and rejects the unstamped SDK default.
    ///     <para>
    ///         The SDK writes <c>1.0.0+&lt;commit sha&gt;</c> when a project sets no version. The sha
    ///         identifies a single build rather than a release and would be the highest-entropy field in the
    ///         payload, so it is stripped before the version is reported.
    ///     </para>
    /// </summary>
    internal static string Normalize(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return UnstampedVersion;
        }

        var trimmed = informationalVersion.Trim();
        var buildMetadataIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (buildMetadataIndex >= 0)
        {
            trimmed = trimmed[..buildMetadataIndex];
        }

        return trimmed.Length == 0 || string.Equals(trimmed, "1.0.0", StringComparison.Ordinal)
            ? UnstampedVersion
            : trimmed;
    }
}
