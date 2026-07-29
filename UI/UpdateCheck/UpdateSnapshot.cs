using System;
using System.Collections.Generic;

namespace MozaPlugin.UI.UpdateCheck
{
    /// <summary>
    /// One release as the updater sees it — the newest stable release, or a
    /// single per-commit PR build.
    /// </summary>
    public sealed class ReleaseEntry
    {
        public string TagName { get; }
        public string Version { get; }
        public string HtmlUrl { get; }
        public string Notes { get; }
        public string AssetUrl { get; }
        public DateTime PublishedAtUtc { get; }

        public ReleaseEntry(
            string tagName, string version, string htmlUrl,
            string notes, string assetUrl, DateTime publishedAtUtc)
        {
            TagName = tagName ?? "";
            Version = version ?? "";
            HtmlUrl = htmlUrl ?? "";
            Notes = notes ?? "";
            AssetUrl = assetUrl ?? "";
            PublishedAtUtc = publishedAtUtc;
        }
    }

    /// <summary>
    /// An open PR's release channel: its number, title (parsed from the
    /// release name) and newest commit build by publish time.
    /// </summary>
    public sealed class PrChannel
    {
        public int Number { get; }
        public string Title { get; }
        public ReleaseEntry Newest { get; }
        public string ChannelId => UpdateCheckService.PrChannelId(Number);

        public PrChannel(int number, string title, ReleaseEntry newest)
        {
            Number = number;
            Title = title ?? "";
            Newest = newest;
        }
    }

    /// <summary>
    /// Parsed view of the repo's release list: the newest stable release plus
    /// one channel per open PR that has builds, sorted by PR number.
    /// </summary>
    public sealed class UpdateSnapshot
    {
        public ReleaseEntry? Stable { get; }
        public IReadOnlyList<PrChannel> PrChannels { get; }
        public DateTime FetchedUtc { get; }

        public UpdateSnapshot(
            ReleaseEntry? stable, IReadOnlyList<PrChannel> prChannels, DateTime fetchedUtc)
        {
            Stable = stable;
            PrChannels = prChannels ?? Array.Empty<PrChannel>();
            FetchedUtc = fetchedUtc;
        }
    }

    /// <summary>
    /// Outcome of a release-list fetch: a snapshot, or the error that
    /// prevented one.
    /// </summary>
    public sealed class SnapshotFetchResult
    {
        public UpdateSnapshot? Snapshot { get; }
        public UpdateCheckErrorKind ErrorKind { get; }
        public string ErrorMessage { get; }

        private SnapshotFetchResult(
            UpdateSnapshot? snapshot, UpdateCheckErrorKind errorKind, string errorMessage)
        {
            Snapshot = snapshot;
            ErrorKind = errorKind;
            ErrorMessage = errorMessage ?? "";
        }

        public static SnapshotFetchResult Ok(UpdateSnapshot snapshot)
            => new SnapshotFetchResult(snapshot, UpdateCheckErrorKind.None, "");

        public static SnapshotFetchResult Fail(UpdateCheckErrorKind kind, string message)
            => new SnapshotFetchResult(null, kind, message);
    }
}
