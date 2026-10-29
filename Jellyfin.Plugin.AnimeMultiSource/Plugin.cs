using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.AnimeMultiSource.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger _logger;
        private readonly object _guardLock = new();
        private FileSystemWatcher? _watcher;
        private Timer? _debounce;
        private Timer? _recheck;
        private int _rechecks;

        public override string Name => Constants.PluginName;
        public override string Description => "Multi-source anime metadata provider using .plexmatch files and Fribb anime lists";
        public override Guid Id => Guid.Parse(Constants.PluginGuid);

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            PluginLog.Directory = applicationPaths.LogDirectoryPath;
            _logger = PluginLog.Wrap(logger);

            // Order matters: read the user's settings before anything else can touch the file (see PluginSettingsStore).
            LoadSettingsOurselves();
            StartSettingsGuard();

            // The old version's folder must NOT be deleted from here: Jellyfin is still creating the plugins and, right after this
            // constructor, writes the old version's meta.json to mark it superseded. With its folder gone, that write throws and
            // the whole server fails to start. So wait until startup is over (and try once more later in case a file was locked).
            _cleanupTimers = new[]
            {
                new Timer(_ => CleanupStaleVersionFolders(applicationPaths), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan),
                new Timer(_ => CleanupStaleVersionFolders(applicationPaths), null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan)
            };
        }

        private Timer[]? _cleanupTimers;

        public static Plugin? Instance { get; private set; }

        // ---- settings -------------------------------------------------------------------------------------------------
        //
        // An update leaves the previous version's folder in place, and on the next restart Jellyfin loads BOTH versions into one
        // process (https://github.com/jellyfin/jellyfin/issues/12959). BasePlugin<T> reads the settings lazily and, if reading
        // fails for any reason, saves a default configuration over the user's file. With two copies of the class loaded, the
        // second reader always fails, so the settings were wiped on the restart after every update. So this copy:
        //   1. reads the file itself, with a serializer bound to its own class, before the lazy loader can run;
        //   2. saves through the same serializer, and keeps a backup copy next to the file;
        //   3. watches the file and puts the settings back if another copy resets it.

        private void LoadSettingsOurselves()
        {
            try
            {
                var path = ConfigurationFilePath;
                var backupPath = PluginSettingsStore.BackupPathFor(path);
                var onDisk = PluginSettingsStore.Read(path);

                if (onDisk != null && !PluginSettingsStore.IsDefault(onDisk))
                {
                    Configuration = onDisk;
                    RefreshBackup(onDisk, backupPath);
                    return;
                }

                // Missing, unreadable, or already reset to defaults by a copy that ran before this one: the backup, if any,
                // holds what the user last saved.
                var backup = PluginSettingsStore.Read(backupPath);
                if (backup != null && !PluginSettingsStore.IsDefault(backup))
                {
                    if (File.Exists(path) && onDisk == null)
                    {
                        KeepUnreadableCopy(path);
                    }

                    Configuration = backup;
                    PluginSettingsStore.Write(backup, path);
                    _logger.LogWarning("The plugin's settings file {Path} was missing, unreadable or reset to defaults; restored your settings from {Backup}.", path, backupPath);
                    return;
                }

                if (onDisk != null)
                {
                    Configuration = onDisk; // genuinely default settings
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the plugin settings ahead of Jellyfin; falling back to Jellyfin's own loading.");
            }
        }

        /// <summary>
        /// Saved through our own serializer. The backup that lets the settings survive a reset by another copy follows real
        /// settings only: a default configuration saved by Jellyfin's own fallback must not replace it.
        /// </summary>
        public override void SaveConfiguration(PluginConfiguration config)
        {
            lock (_guardLock)
            {
                PluginSettingsStore.Write(config, ConfigurationFilePath);
                if (!PluginSettingsStore.IsDefault(config))
                {
                    RefreshBackup(config, PluginSettingsStore.BackupPathFor(ConfigurationFilePath));
                }
            }
        }

        /// <summary>The user pressed Save on the settings page: what they saved is the truth, even if it is back to defaults.</summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);

            lock (_guardLock)
            {
                if (configuration is PluginConfiguration saved)
                {
                    RefreshBackup(saved, PluginSettingsStore.BackupPathFor(ConfigurationFilePath));
                }
            }
        }

        private void RefreshBackup(PluginConfiguration config, string backupPath)
        {
            try
            {
                if (!PluginSettingsStore.AreEqual(PluginSettingsStore.Read(backupPath), config))
                {
                    PluginSettingsStore.Write(config, backupPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not update the settings backup {Path}", backupPath);
            }
        }

        private void KeepUnreadableCopy(string path)
        {
            try
            {
                File.Copy(path, path + ".unreadable", overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not keep a copy of the unreadable settings file {Path}", path);
            }
        }

        private void StartSettingsGuard()
        {
            try
            {
                var path = ConfigurationFilePath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    _debounce = new Timer(_ => RestoreIfReset("the settings file changed"), null, Timeout.Infinite, Timeout.Infinite);
                    _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
                    };
                    void Changed(object? sender, FileSystemEventArgs e) => _debounce?.Change(300, Timeout.Infinite);
                    _watcher.Changed += Changed;
                    _watcher.Created += Changed;
                    _watcher.Deleted += Changed;
                    _watcher.Renamed += (s, e) => Changed(s, e);
                    _watcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not watch the settings file; relying on the timed checks.");
            }

            // File watching is not reliable on every file system (network shares, some container volumes), so check on a timer too:
            // soon after start - when a second loaded copy does its damage - and then now and then for a while.
            _recheck = new Timer(
                _ =>
                {
                    RestoreIfReset("a timed check");
                    if (Interlocked.Increment(ref _rechecks) >= 20)
                    {
                        _recheck?.Dispose();
                    }
                },
                null,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(30));
        }

        private void RestoreIfReset(string reason)
        {
            lock (_guardLock)
            {
                try
                {
                    var path = ConfigurationFilePath;
                    var backupPath = PluginSettingsStore.BackupPathFor(path);
                    var onDisk = PluginSettingsStore.Read(path);

                    if (onDisk != null && !PluginSettingsStore.IsDefault(onDisk))
                    {
                        return; // fine as it is
                    }

                    // The file is missing, unreadable or back to defaults. If the user really has settings - in memory, or in the
                    // backup - they are put back; if not, there is nothing to protect.
                    var held = Configuration;
                    PluginConfiguration? truth = held != null && !PluginSettingsStore.IsDefault(held) ? held : null;
                    if (truth == null)
                    {
                        var backup = PluginSettingsStore.Read(backupPath);
                        truth = backup != null && !PluginSettingsStore.IsDefault(backup) ? backup : null;
                    }

                    if (truth == null)
                    {
                        return;
                    }

                    PluginSettingsStore.Write(truth, path);
                    if (!ReferenceEquals(truth, held))
                    {
                        Configuration = truth;
                    }

                    _logger.LogWarning("The settings file {Path} had been reset (detected by {Reason}), most likely by an older copy of the plugin loaded alongside this one after an update. Your settings were restored.", path, reason);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Settings check failed ({Reason})", reason);
                }
            }
        }

        // ---- leftover folders of older versions --------------------------------------------------------------------------

        private void CleanupStaleVersionFolders(IApplicationPaths applicationPaths)
        {
            try
            {
                var ownDirectory = Path.GetDirectoryName(AssemblyFilePath);
                if (string.IsNullOrWhiteSpace(applicationPaths.PluginsPath) || string.IsNullOrWhiteSpace(ownDirectory))
                {
                    return;
                }

                var ownVersion = Version ?? GetType().Assembly.GetName().Version ?? new Version(0, 0);
                foreach (var directory in StalePluginFolders.Find(applicationPaths.PluginsPath, ownDirectory, Path.GetFileName(AssemblyFilePath), Id, ownVersion))
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                        _logger.LogInformation("Removed the folder of an older plugin version left behind by an update: {Directory}", directory);
                    }
                    catch (Exception ex)
                    {
                        // A loaded assembly cannot be deleted on Windows. Mark the folder disabled so Jellyfin stops loading it.
                        var disabled = StalePluginFolders.TryDisable(directory);
                        _logger.LogWarning(ex, "Found the folder of an older plugin version at {Directory} but could not delete it ({Result}); delete it manually.",
                            directory, disabled ? "marked as disabled instead" : "could not disable it either");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check for folders of older plugin versions.");
            }
        }

        // ---- access for the providers ------------------------------------------------------------------------------------

        public static PluginConfiguration GetConfigurationSafe(ILogger logger)
        {
            if (Instance == null)
            {
                return new PluginConfiguration();
            }

            try
            {
                return Instance.Configuration;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load plugin configuration via the cached instance; reading it directly from disk.");
                return PluginSettingsStore.Read(Instance.ConfigurationFilePath) ?? new PluginConfiguration();
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Constants.PluginName,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
                }
            };
        }
    }
}
