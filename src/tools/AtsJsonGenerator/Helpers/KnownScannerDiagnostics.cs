using System.Text.RegularExpressions;

namespace AtsJsonGenerator.Helpers;

/// <summary>
/// Recognizes ATS scanner error diagnostics caused by known scanner defects.
/// </summary>
/// <remarks>
/// The Aspire CLI scanner exports a method inherited by several proxy types once per
/// derived type, all under the base type's capability ID. The scanner keeps the first
/// definition and reports each collision as an error. Remove this tolerance once the
/// shipped Aspire CLI includes the fix (https://github.com/microsoft/aspire/pull/20443).
/// </remarks>
internal static partial class KnownScannerDiagnostics
{
    /// <summary>
    /// Prefix of the line written for each tolerated diagnostic. generate-ts-api-json.ps1 parses this prefix.
    /// </summary>
    public const string ToleratedOutputPrefix = "Tolerated known ATS scanner diagnostic:";

    /// <summary>
    /// Returns whether <paramref name="message"/> reports the inherited duplicate capability defect.
    /// </summary>
    public static bool IsInheritedDuplicateCapability(string message)
    {
        var match = InheritedDuplicateCapabilityPattern().Match(message);
        if (!match.Success)
        {
            return false;
        }

        var capabilityNamespace = match.Groups["namespace"].Value;
        var first = SplitQualifiedMember(match.Groups["first"].Value);
        var second = SplitQualifiedMember(match.Groups["second"].Value);

        return first.Namespace.Equals(capabilityNamespace, StringComparison.Ordinal)
            && second.Namespace.Equals(capabilityNamespace, StringComparison.Ordinal)
            && !first.Type.Equals(second.Type, StringComparison.Ordinal)
            && first.Member.Equals(second.Member, StringComparison.Ordinal)
            && first.Member.Equals(match.Groups["method"].Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the error diagnostics in <paramref name="dump"/> that match a known scanner defect.
    /// </summary>
    public static IReadOnlyList<string> GetToleratedErrors(AtsDumpRoot dump) =>
        AtsTransformer.GetErrorDiagnostics(dump)
            .Where(IsInheritedDuplicateCapability)
            .ToArray();

    private static (string Namespace, string Type, string Member) SplitQualifiedMember(string qualifiedMember)
    {
        // The pattern guarantees at least three non-empty dot-separated segments.
        var memberSeparator = qualifiedMember.LastIndexOf('.');
        var typeSeparator = qualifiedMember.LastIndexOf('.', memberSeparator - 1);

        return (
            qualifiedMember[..typeSeparator],
            qualifiedMember[(typeSeparator + 1)..memberSeparator],
            qualifiedMember[(memberSeparator + 1)..]);
    }

    [GeneratedRegex(
        @"^Duplicate capability '(?<namespace>\w+(?:\.\w+)*)/\w+\.(?<method>\w+)': defined at '(?<first>\w+(?:\.\w+){2,})' and '(?<second>\w+(?:\.\w+){2,})'\. Remove \[AspireExport\] from one of them or use different capability IDs\.\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex InheritedDuplicateCapabilityPattern();
}
