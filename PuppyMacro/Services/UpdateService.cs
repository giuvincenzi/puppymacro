using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace PuppyMacro.Services;

/// <summary>New versions from the GitHub Releases of the repository, through Velopack.</summary>
internal sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/giuvincenzi/puppymacro";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    /// <summary>False when PuppyMacro was not installed with Setup (dotnet run, a copied folder).</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>The installed version, e.g. "1.7.0". Null when not installed.</summary>
    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public static string ReleaseUrl(string version) => $"{RepositoryUrl}/releases/tag/v{version}";

    /// <summary>The newest release, or null when the installed version is the newest.</summary>
    public Task<UpdateInfo?> CheckAsync() => _manager.CheckForUpdatesAsync();

    /// <summary>Downloads the update. <paramref name="progress"/> (0-100) is called on a worker thread.</summary>
    public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancel) =>
        _manager.DownloadUpdatesAsync(update, progress, cancel);

    /// <summary>Starts the updater: it waits for this process to exit, installs the update and starts PuppyMacro again.</summary>
    public void ApplyAfterExit(UpdateInfo update) =>
        _manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true);
}
