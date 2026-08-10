using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource
{
    public class Plugin : BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
    {
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
            CleanupStaleVersionFolders(applicationPaths, logger);
        }

        // Jellyfin's plugin updater installs a new version alongside the old one instead of
        // replacing it (https://github.com/jellyfin/jellyfin/issues/12959 - affects other plugins
        // too, not specific to this one). If both stick around, a later restart can load both
        // assemblies at once, which breaks config load/save (see GetConfigurationSafe) because
        // .NET treats PluginConfiguration from the two assemblies as different types despite the
        // identical name. We're already running by the time this constructor executes, so any
        // *other* folder here that carries our own name and our own dll is provably a stale
        // leftover - remove it now so the next restart never hits the dual-load bug at all,
        // instead of requiring a manual SSH cleanup after every update.
        private void CleanupStaleVersionFolders(IApplicationPaths applicationPaths, ILogger logger)
        {
            try
            {
                var pluginsPath = applicationPaths.PluginsPath;
                var ownDirectory = Path.GetDirectoryName(AssemblyFilePath);
                if (string.IsNullOrWhiteSpace(pluginsPath) || !Directory.Exists(pluginsPath) || string.IsNullOrWhiteSpace(ownDirectory))
                {
                    return;
                }

                var ownDllName = Path.GetFileName(AssemblyFilePath);

                foreach (var directory in Directory.GetDirectories(pluginsPath))
                {
                    if (string.Equals(directory, ownDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var folderName = Path.GetFileName(directory);
                    if (string.IsNullOrEmpty(folderName) || !folderName.StartsWith(Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!File.Exists(Path.Combine(directory, ownDllName)))
                    {
                        // Doesn't look like a version of this same plugin; leave it alone.
                        continue;
                    }

                    try
                    {
                        Directory.Delete(directory, recursive: true);
                        logger.LogInformation("Removed stale plugin version folder {Directory} left behind by a previous update.", directory);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Found a stale plugin version folder at {Directory} but could not remove it automatically; delete it manually to avoid config/duplicate-provider issues.", directory);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to check for stale plugin version folders.");
            }
        }

        public static Plugin? Instance { get; private set; }

        public static Configuration.PluginConfiguration GetConfigurationSafe(ILogger logger)
        {
            if (Instance == null)
            {
                return new Configuration.PluginConfiguration();
            }

            try
            {
                return Instance.Configuration;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load plugin configuration via the cached instance; attempting to recover it directly from disk.");
                return RecoverConfigurationFromDisk(logger) ?? new Configuration.PluginConfiguration();
            }
        }

        // BasePlugin<T>.Configuration throws InvalidCastException when a stale duplicate plugin
        // folder from a previous update is still sitting in the plugins directory alongside the
        // current one (a known upstream Jellyfin bug - old version folders aren't cleaned up on
        // update: https://github.com/jellyfin/jellyfin/issues/12959). When that happens, .NET
        // treats PluginConfiguration loaded from that other assembly as a different type even
        // though the name matches, so the cached Configuration instance silently ends up holding
        // the wrong type. The fix above (catch and default) was losing every setting on every
        // such conflict. Re-deserializing the XML file directly, against our own local type
        // reference instead of whatever type the cache is holding, recovers the user's actual
        // saved settings instead.
        private static Configuration.PluginConfiguration? RecoverConfigurationFromDisk(ILogger logger)
        {
            try
            {
                var path = Instance!.ConfigurationFilePath;
                if (!System.IO.File.Exists(path))
                {
                    return null;
                }

                var recovered = Instance.XmlSerializer.DeserializeFromFile(typeof(Configuration.PluginConfiguration), path) as Configuration.PluginConfiguration;
                if (recovered != null)
                {
                    logger.LogWarning(
                        "Recovered plugin configuration directly from {Path} after a duplicate-plugin-version conflict. " +
                        "To stop this happening on every update, delete any old \"Anime Multi Source_<version>\" folders " +
                        "under your Jellyfin plugins directory, leaving only the current version.",
                        path);
                }

                return recovered;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Direct configuration file recovery also failed; falling back to defaults.");
                return null;
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
