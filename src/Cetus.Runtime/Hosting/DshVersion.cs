using System.Globalization;

namespace Cetus.Hosting;

/// <summary>
/// Semantic version used for DSH npm dist-tags. <see cref="System.Version"/>
/// cannot parse prerelease versions such as <c>0.1.7-alpha.1</c>.
/// </summary>
public sealed class DshVersion
{
    private readonly IReadOnlyList<string> _prerelease;

    private DshVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public IReadOnlyList<string> Prerelease => _prerelease;

    public static IComparer<DshVersion> SemanticComparer { get; } =
        Comparer<DshVersion>.Create(Compare);

    public static bool TryParse(string? value, out DshVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        int buildSeparator = normalized.IndexOf('+');
        if (buildSeparator >= 0)
        {
            normalized = normalized[..buildSeparator];
        }

        string[] segments = normalized.Split('-', 2);
        string[] core = segments[0].Split('.');
        if (core.Length != 3
            || !TryParseCoreIdentifier(core[0], out int major)
            || !TryParseCoreIdentifier(core[1], out int minor)
            || !TryParseCoreIdentifier(core[2], out int patch))
        {
            return false;
        }

        string[] prerelease = segments.Length == 1
            ? []
            : segments[1].Split('.');
        if (prerelease.Any(identifier => identifier.Length == 0
            || identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            return false;
        }

        version = new DshVersion(major, minor, patch, prerelease);
        return true;
    }

    public static int Compare(DshVersion? left, DshVersion? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int coreComparison = left.Major.CompareTo(right.Major);
        if (coreComparison != 0) return coreComparison;
        coreComparison = left.Minor.CompareTo(right.Minor);
        if (coreComparison != 0) return coreComparison;
        coreComparison = left.Patch.CompareTo(right.Patch);
        if (coreComparison != 0) return coreComparison;

        bool leftIsRelease = left._prerelease.Count == 0;
        bool rightIsRelease = right._prerelease.Count == 0;
        if (leftIsRelease || rightIsRelease)
        {
            return leftIsRelease == rightIsRelease ? 0 : leftIsRelease ? 1 : -1;
        }

        int commonCount = Math.Min(left._prerelease.Count, right._prerelease.Count);
        for (int index = 0; index < commonCount; index++)
        {
            int identifierComparison = ComparePrereleaseIdentifier(left._prerelease[index], right._prerelease[index]);
            if (identifierComparison != 0)
            {
                return identifierComparison;
            }
        }

        return left._prerelease.Count.CompareTo(right._prerelease.Count);
    }

    public override string ToString() =>
        _prerelease.Count == 0
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{string.Join('.', _prerelease)}";

    private static bool TryParseCoreIdentifier(string identifier, out int value) =>
        int.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && value >= 0;

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        bool leftIsNumeric = IsNumeric(left);
        bool rightIsNumeric = IsNumeric(right);
        if (leftIsNumeric && rightIsNumeric)
        {
            return CompareNumericIdentifier(left, right);
        }

        if (leftIsNumeric != rightIsNumeric)
        {
            return leftIsNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value) => value.All(char.IsAsciiDigit);

    private static int CompareNumericIdentifier(string left, string right)
    {
        string normalizedLeft = left.TrimStart('0');
        string normalizedRight = right.TrimStart('0');
        normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
        normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;

        int lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
        return lengthComparison != 0
            ? lengthComparison
            : string.CompareOrdinal(normalizedLeft, normalizedRight);
    }
}
