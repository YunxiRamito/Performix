// Options for the update path: channel, budgets, the local test override and the per-download
// knobs. Parsed from the command line so `--check-update` and the page agree on one config.
//
// The two budget numbers are copied from the reference implementation the design doc studies
// (docs\UPDATE-DESIGN.md §1.1.4): 4 s per source group, 8 s for the whole check. A source that
// has not answered inside its group budget is simply not part of the result — the check never
// throws because one mirror is slow, it degrades.

using System;
using System.Globalization;

namespace Predex.WinUI.Services.Update
{
    /// <summary>Immutable update configuration (channel, budgets, override, temp root).</summary>
    public sealed class UpdateOptions
    {
        public const string DefaultChannel = "alpha";

        /// <summary>Channel requested from the feed. Must match the manifest's own channel.</summary>
        public string Channel { get; init; } = DefaultChannel;

        /// <summary>
        /// --update-feed: a manifest file path / URL used INSTEAD of the real source families.
        /// Test and verification only; it also relaxes the asset URL check to loopback http/file
        /// because that is the only way to exercise the state machine without a release.
        /// </summary>
        public string? FeedOverride { get; init; }

        /// <summary>Version reported in the User-Agent ("Performix/0.1.0-alpha.1").</summary>
        public string ProductVersion { get; init; } = "0.0.0";

        /// <summary>Budget for one source group (official or accelerated).</summary>
        public TimeSpan GroupBudget { get; init; } = TimeSpan.FromSeconds(4);

        /// <summary>Budget for the whole check (both groups together).</summary>
        public TimeSpan TotalBudget { get; init; } = TimeSpan.FromSeconds(8);

        /// <summary>No new byte for this long during a download aborts the attempt and retries.</summary>
        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(20);

        /// <summary>Retries per URL, transient failures only (max 2 per the brief).</summary>
        public int MaxRetries { get; init; } = 2;

        /// <summary>Directory downloads go to; default %TEMP%\Performix\update.</summary>
        public string? TempRoot { get; init; }

        public bool AllowLocalUrls => !string.IsNullOrWhiteSpace(FeedOverride);

        /// <summary>Effective download directory (created on demand by the client).</summary>
        public string ResolveTempRoot()
            => string.IsNullOrWhiteSpace(TempRoot)
                ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Performix", "update")
                : TempRoot!;

        public string UserAgent => "Performix/" + ProductVersion;

        public UpdateOptions WithChannel(string channel) => new()
        {
            Channel = channel,
            FeedOverride = FeedOverride,
            ProductVersion = ProductVersion,
            GroupBudget = GroupBudget,
            TotalBudget = TotalBudget,
            StallTimeout = StallTimeout,
            MaxRetries = MaxRetries,
            TempRoot = TempRoot,
        };

        /// <summary>
        /// Reads --update-channel=, --update-feed=, --update-temp= out of a command line. Both
        /// "--name=value" and "--name value" are accepted; anything else is ignored, so a typo
        /// cannot change behaviour silently (the value simply stays at its default).
        /// </summary>
        public static UpdateOptions FromCommandLine(string[]? args)
        {
            string channel = DefaultChannel;
            string? feed = null;
            string? temp = null;

            if (args is not null)
            {
                string? rawChannel = OptionValue(args, "--update-channel");
                if (!string.IsNullOrWhiteSpace(rawChannel)) channel = rawChannel!.Trim().ToLowerInvariant();
                string? rawFeed = OptionValue(args, "--update-feed");
                if (!string.IsNullOrWhiteSpace(rawFeed)) feed = rawFeed!.Trim();
                string? rawTemp = OptionValue(args, "--update-temp");
                if (!string.IsNullOrWhiteSpace(rawTemp)) temp = rawTemp!.Trim();
            }

            return new UpdateOptions { Channel = channel, FeedOverride = feed, TempRoot = temp };
        }

        /// <summary>Returns the value of "--name=value" / "--name value", or null.</summary>
        public static string? OptionValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return argument[(name.Length + 1)..];
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    // "--update-feed --no-autohide" must not read the next switch as the value:
                    // a name starting with "--" is another option, so this one has no value.
                    string next = args[i + 1];
                    if (next.StartsWith("--", StringComparison.Ordinal)) continue;
                    return next;
                }
            }
            return null;
        }

        /// <summary>
        /// True when the command line asked for "--name" (flag form). The "--name=value" form is
        /// parsed as a boolean: "--update-no-install=false" must NOT read as "the switch is
        /// there". A value that is not true/false is treated as if the switch were absent and is
        /// logged, so a typo cannot silently flip a behaviour either.
        /// </summary>
        public static bool HasFlag(string[]? args, string name)
        {
            if (args is null) return false;
            foreach (string argument in args)
            {
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) return true;
                if (!argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) continue;

                string value = argument[(name.Length + 1)..].Trim();
                if (bool.TryParse(value, out bool enabled)) return enabled;

                ShellLog.Write("update",
                    $"ignoring {name}={value}: expected true or false, so the switch counts as absent");
            }
            return false;
        }

        public override string ToString()
            => string.Create(CultureInfo.InvariantCulture,
                $"channel={Channel} group={GroupBudget.TotalSeconds:0}s total={TotalBudget.TotalSeconds:0}s stall={StallTimeout.TotalSeconds:0}s feed={FeedOverride ?? "default"}");
    }
}
