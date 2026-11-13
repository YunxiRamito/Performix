// Semantic-version parsing and comparison for the update feed.
//
// WHY NOT System.Version: the update server hands us "0.1.0-alpha.2", and Version.TryParse
// rejects the pre-release suffix outright — the old UpdateCheckService silently gave up on
// every pre-release build (see its own comment). A string comparison is equally wrong
// ("0.10.0" < "0.9.0" as text). So the comparison lives here: one implementation, unit-tested,
// used by the checker, the page and the tests.
//
// Accepted shape (SemVer 2.0 subset, deliberately tolerant about the leading "v" and the
// 2..4 numeric parts the reference implementation also accepts):
//
//     [v]MAJOR[.MINOR[.PATCH[.REVISION]]][-PRE.RELEASE][+BUILD]
//
//   • "v" / "V" prefix is stripped (GitHub tags are "v0.1.0-alpha.1").
//   • BUILD metadata ("+<commit>") is parsed away and never compared (SemVer §10).
//   • REVISION is the 4th numeric part some .NET builds carry ("1.7.1.0"); a missing
//     revision compares equal to 0, so 1.7.1.0 == 1.7.1 (reference behaviour).
//   • PRE-RELEASE precedence is SemVer §11: a version without a pre-release outranks the
//     same version with one ("0.1.0" > "0.1.0-alpha.2"), numeric identifiers compare
//     numerically ("alpha.2" > "alpha.1"), numeric identifiers rank below alphanumeric
//     ones, alphanumeric identifiers compare ordinal, and a shorter identifier list loses.
//     That ordering is what makes alpha < beta < rc < stable (ordinal a < b < r, and the
//     stable release has no pre-release at all) — pinned by tests, not by a lookup table
//     that could silently disagree with SemVer.

using System;
using System.Collections.Generic;

namespace Predex.WinUI.Services.Update
{
    /// <summary>
    /// Parsed semantic version with SemVer 2.0 precedence. Immutable; a rejected string
    /// yields <c>null</c> from <see cref="Parse"/> so callers can tell "same version" from
    /// "the feed sent garbage" (never downgrade or install on a value we could not read).
    /// </summary>
    public sealed class SemanticVersion : IComparable<SemanticVersion>, IComparable, IEquatable<SemanticVersion>
    {
        private const int MaxParts = 4;

        private SemanticVersion(int major, int minor, int patch, int revision, bool hasRevision, string prerelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Revision = revision;
            HasRevision = hasRevision;
            Prerelease = prerelease;
            NormalizedPrerelease = NormalizePrerelease(prerelease);
        }

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }

        /// <summary>Fourth numeric part; 0 when absent (absent and 0 compare equal).</summary>
        public int Revision { get; }

        /// <summary>True when the text carried a fourth part, so ToString round-trips it.</summary>
        public bool HasRevision { get; }

        /// <summary>Dot-separated pre-release label ("alpha.2"); empty for a stable version.</summary>
        public string Prerelease { get; }

        /// <summary>
        /// The pre-release label in the form <see cref="CompareTo(SemanticVersion?)"/> treats as
        /// equal to it: numeric identifiers with their leading zeros stripped ("a.01" → "a.1").
        /// Without this, "1.0.0-a.01" and "1.0.0-a.1" compare equal but hash differently, which
        /// breaks the Equals/GetHashCode contract and silently corrupts any HashSet/Dictionary.
        /// </summary>
        private string NormalizedPrerelease { get; }

        public bool IsPrerelease => Prerelease.Length > 0;

        /// <summary>
        /// Parses <paramref name="text"/>. Returns false for empty input, a non-numeric core,
        /// more than four parts, an empty pre-release label or an invalid identifier — a value
        /// like that must never be compared, because "unreadable" is not "equal".
        /// </summary>
        public static bool TryParse(string? text, out SemanticVersion? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string core = text.Trim();

            // Optional tag prefix ("v0.1.0-alpha.1").
            if (core.Length > 1 && (core[0] == 'v' || core[0] == 'V')) core = core[1..];

            // Build metadata never participates in precedence.
            int plus = core.IndexOf('+');
            if (plus >= 0) core = core[..plus];
            if (core.Length == 0) return false;

            // Pre-release label: everything after the FIRST hyphen (SemVer allows hyphens
            // inside identifiers, so only the first one splits the core).
            string prerelease = string.Empty;
            int dash = core.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = core[(dash + 1)..];
                core = core[..dash];
                if (prerelease.Length == 0) return false;
                if (!IsValidPrerelease(prerelease)) return false;
            }

            string[] parts = core.Split('.');
            if (parts.Length < 1 || parts.Length > MaxParts) return false;

            var numbers = new int[MaxParts];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryParseNumber(parts[i], out numbers[i])) return false;
            }

            bool hasRevision = parts.Length == MaxParts;
            version = new SemanticVersion(numbers[0], numbers[1], numbers[2], numbers[3], hasRevision, prerelease);
            return true;
        }

        /// <summary>Convenience wrapper: the parsed value, or null when the text is invalid.</summary>
        public static SemanticVersion? Parse(string? text) => TryParse(text, out SemanticVersion? parsed) ? parsed : null;

        /// <summary>
        /// SemVer precedence of two possibly-null versions; null sorts below everything.
        /// Positive means <paramref name="left"/> is newer. This is the only comparison the
        /// update path may use.
        /// </summary>
        public static int Compare(SemanticVersion? left, SemanticVersion? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return left.CompareTo(right);
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;

            int result = Major.CompareTo(other.Major);
            if (result != 0) return result;
            result = Minor.CompareTo(other.Minor);
            if (result != 0) return result;
            result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;
            result = Revision.CompareTo(other.Revision);
            if (result != 0) return result;

            // Same core: a version without a pre-release is the release and outranks any
            // pre-release of the same core (SemVer §11.3).
            if (!IsPrerelease && !other.IsPrerelease) return 0;
            if (!IsPrerelease) return 1;
            if (!other.IsPrerelease) return -1;
            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        /// <summary>
        /// IComparable contract: null sorts below every instance (a version is always "greater"
        /// than nothing); a non-SemanticVersion argument is a programming error. Throwing for null
        /// would make Array.Sort/List.Sort treat a null element as a broken comparer.
        /// </summary>
        public int CompareTo(object? obj)
        {
            if (obj is null) return 1;      // null sorts first, per IComparable.CompareTo(object)
            if (obj is SemanticVersion other) return CompareTo(other);
            throw new ArgumentException("Object is not a SemanticVersion.", nameof(obj));
        }

        public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;
        public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

        /// <summary>
        /// Hash on the same normalized identifiers <see cref="CompareTo(SemanticVersion?)"/> uses,
        /// so equal versions always share a hash (see <see cref="NormalizedPrerelease"/>).
        /// </summary>
        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Revision, NormalizedPrerelease);

        /// <summary>Canonical text: core parts that were present plus "-" + pre-release.</summary>
        public override string ToString()
        {
            string core = HasRevision
                ? $"{Major}.{Minor}.{Patch}.{Revision}"
                : $"{Major}.{Minor}.{Patch}";
            return IsPrerelease ? core + "-" + Prerelease : core;
        }

        /// <summary>True when a published version is newer than the running one.</summary>
        public static bool IsNewer(string? candidate, string? current)
        {
            SemanticVersion? remote = Parse(candidate);
            SemanticVersion? local = Parse(current);
            return remote is not null && local is not null && Compare(remote, local) > 0;
        }

        // ── Pre-release precedence (SemVer §11.4) ────────────────────────────────

        private static int ComparePrerelease(string left, string right)
        {
            string[] a = left.Split('.');
            string[] b = right.Split('.');
            int shared = Math.Min(a.Length, b.Length);

            for (int i = 0; i < shared; i++)
            {
                bool aNumeric = IsNumeric(a[i]);
                bool bNumeric = IsNumeric(b[i]);

                if (aNumeric && bNumeric)
                {
                    int numeric = CompareNumeric(a[i], b[i]);
                    if (numeric != 0) return numeric;
                }
                else if (aNumeric)
                {
                    return -1;      // numeric identifiers always rank below alphanumeric
                }
                else if (bNumeric)
                {
                    return 1;
                }
                else
                {
                    int ordinal = string.CompareOrdinal(a[i], b[i]);
                    if (ordinal != 0) return ordinal;
                }
            }

            // All shared identifiers equal: the longer list has higher precedence (alpha.1 <
            // alpha.1.1). A shorter list losing is what makes "alpha" < "alpha.1".
            return a.Length.CompareTo(b.Length);
        }

        /// <summary>
        /// Compares two digit-only identifiers without overflowing: strip the leading zeros,
        /// then a longer digit string is the larger number ("10" > "9"), equal lengths fall
        /// back to ordinal.
        /// </summary>
        private static int CompareNumeric(string left, string right)
        {
            ReadOnlySpan<char> a = left.AsSpan().TrimStart('0');
            ReadOnlySpan<char> b = right.AsSpan().TrimStart('0');
            if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
            return a.SequenceCompareTo(b);
        }

        private static bool IsNumeric(string value)
        {
            if (value.Length == 0) return false;
            foreach (char c in value)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        /// <summary>
        /// Canonical form of a pre-release label for equality/hashing: numeric identifiers lose
        /// their leading zeros ("01" → "1"), which is exactly what <see cref="CompareNumeric"/>
        /// ignores during comparison. Alphanumeric identifiers keep their ordinal spelling
        /// (comparison is ordinal there, so case and digits are significant).
        /// </summary>
        private static string NormalizePrerelease(string label)
        {
            if (label.Length == 0) return label;
            string[] identifiers = label.Split('.');
            var normalized = new System.Text.StringBuilder(label.Length);
            for (int i = 0; i < identifiers.Length; i++)
            {
                if (i > 0) normalized.Append('.');
                string identifier = identifiers[i];
                if (!IsNumeric(identifier))
                {
                    normalized.Append(identifier);
                    continue;
                }
                ReadOnlySpan<char> trimmed = identifier.AsSpan().TrimStart('0');
                if (trimmed.Length == 0) normalized.Append('0');
                else normalized.Append(trimmed);
            }
            return normalized.ToString();
        }

        private static bool IsValidPrerelease(string label)
        {
            string[] identifiers = label.Split('.');
            foreach (string identifier in identifiers)
            {
                if (identifier.Length == 0) return false;
                foreach (char c in identifier)
                {
                    bool allowed = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') ||
                                   (c >= 'A' && c <= 'Z') || c == '-';
                    if (!allowed) return false;
                }
            }
            return true;
        }

        private static bool TryParseNumber(string text, out int value)
        {
            value = 0;
            if (text.Length == 0) return false;
            foreach (char c in text)
            {
                if (c < '0' || c > '9') return false;
            }
            return int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
