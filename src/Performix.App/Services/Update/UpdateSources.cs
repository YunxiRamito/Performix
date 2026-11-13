// Update source catalogue — the two source families the user specified.
//
//   OFFICIAL    GitHub Releases API + npm registry. Both are the source of truth: the release
//               carries update.json as an asset, the npm package carries the same file.
//   ACCELERATED Mainland-China paths. GitHub is reached through a proxy prefix
//               (gh-proxy.com, ghproxy.net) or jsDelivr's GitHub CDN; npm through
//               registry.npmmirror.com. These serve the SAME update.json that is committed to
//               the repository (that copy is what makes a proxy path possible at all — a proxy
//               cannot invent a release asset).
//
// The strategy lives in UpdateClient: the official group is queried first, in parallel, with a
// 4 s group budget; only when it fails or times out is the accelerated group queried, also in
// parallel. Inside a group the newest complete manifest wins and the source that produced it
// is recorded so the page can say "hit: gh-proxy" instead of pretending the network is magic.
//
// Channel → manifest file name: alpha is the channel this build publishes today and uses the
// plain update.json the format doc specifies; a future stable/beta channel would publish
// update-<channel>.json alongside it (docs\RELEASE.md).

using System;
using System.Collections.Generic;

namespace Predex.WinUI.Services.Update
{
    /// <summary>How a source answers: direct manifest bytes, a release list or npm metadata.</summary>
    public enum UpdateSourceKind
    {
        /// <summary>The URL is the manifest itself (repo file, proxy prefix, local override).</summary>
        Manifest = 0,

        /// <summary>GitHub releases API: pick the release, then its update.json asset.</summary>
        GitHubReleases = 1,

        /// <summary>npm registry metadata: pick the version, then update.json inside the tarball.</summary>
        NpmRegistry = 2,
    }

    /// <summary>One way of getting the manifest. Immutable description, no state.</summary>
    public sealed class UpdateSource
    {
        public UpdateSource(string id, UpdateSourceKind kind, string url, string label)
        {
            Id = id;
            Kind = kind;
            Url = url;
            Label = label;
        }

        /// <summary>Stable id used in logs ("github-releases", "gh-proxy", …).</summary>
        public string Id { get; }

        public UpdateSourceKind Kind { get; }

        public string Url { get; }

        /// <summary>Short English label shown on the page (Loc.T has a zh-CN row for each).</summary>
        public string Label { get; }

        public override string ToString() => $"{Id} ({Label})";
    }

    /// <summary>Where the two groups come from, and the local-override group.</summary>
    public static class UpdateSourceCatalog
    {
        /// <summary>GitHub repository that publishes the releases.</summary>
        public const string Repository = "YunxiRamito/Performix";

        public const string NpmPackage = "performix";

        public const string DefaultBranch = "main";

        /// <summary>
        /// File name of the manifest inside the release / npm package / repository. alpha is the
        /// only published channel today, so it keeps the plain "update.json" name.
        /// </summary>
        public static string ManifestFileName(string channel)
            => string.Equals(channel, "alpha", StringComparison.OrdinalIgnoreCase)
                ? "update.json"
                : $"update-{channel.ToLowerInvariant()}.json";

        /// <summary>Official group: GitHub Releases + npm registry, plus the committed manifest
        /// on raw.githubusercontent.com as the official path that does not depend on the API
        /// (the anonymous GitHub API is rate limited to 60 requests/hour, and the update check
        /// must still answer on a seventh run).</summary>
        public static IReadOnlyList<UpdateSource> Official(string channel) => new[]
        {
            new UpdateSource(
                "github-releases",
                UpdateSourceKind.GitHubReleases,
                $"https://api.github.com/repos/{Repository}/releases",
                "GitHub Releases"),
            new UpdateSource(
                "npm-official",
                UpdateSourceKind.NpmRegistry,
                $"https://registry.npmjs.org/{NpmPackage}",
                "npm registry"),
            new UpdateSource(
                "github-raw",
                UpdateSourceKind.Manifest,
                $"https://raw.githubusercontent.com/{Repository}/{DefaultBranch}/{ManifestFileName(channel)}",
                "GitHub raw"),
        };

        /// <summary>
        /// Mainland-China group: GitHub through the three proxy families the brief names, npm
        /// through the npmmirror registry. Every GitHub entry points at the committed
        /// update.json on the default branch.
        /// </summary>
        public static IReadOnlyList<UpdateSource> Accelerated(string channel)
        {
            string fileName = ManifestFileName(channel);
            string rawUrl = $"https://raw.githubusercontent.com/{Repository}/{DefaultBranch}/{fileName}";
            return new[]
            {
                new UpdateSource("gh-proxy", UpdateSourceKind.Manifest,
                    "https://gh-proxy.com/" + rawUrl, "gh-proxy"),
                new UpdateSource("ghproxy-net", UpdateSourceKind.Manifest,
                    "https://ghproxy.net/" + rawUrl, "ghproxy.net"),
                new UpdateSource("jsdelivr-gh", UpdateSourceKind.Manifest,
                    $"https://cdn.jsdelivr.net/gh/{Repository}@{DefaultBranch}/{fileName}", "jsDelivr"),
                new UpdateSource("npmmirror", UpdateSourceKind.NpmRegistry,
                    $"https://registry.npmmirror.com/{NpmPackage}", "npmmirror"),
            };
        }

        /// <summary>
        /// --update-feed override: one local source, used by the verification runs and
        /// --check-update against a hand-written manifest. The manifest's own channel still has
        /// to match, so a local alpha manifest cannot be used to test the stable channel in a
        /// misleading way.
        /// </summary>
        public static UpdateSource Override(string channel, string feed)
            => new("override", UpdateSourceKind.Manifest, feed, "Local test feed");

        /// <summary>All candidates in the order they are tried, for logs/UI.</summary>
        public static IEnumerable<UpdateSource> All(string channel, string? feedOverride = null)
        {
            if (!string.IsNullOrWhiteSpace(feedOverride))
            {
                yield return Override(channel, feedOverride);
                yield break;
            }
            foreach (UpdateSource source in Official(channel)) yield return source;
            foreach (UpdateSource source in Accelerated(channel)) yield return source;
        }
    }
}
