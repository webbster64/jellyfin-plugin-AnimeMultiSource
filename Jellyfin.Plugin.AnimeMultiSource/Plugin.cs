using System;
using System.Collections.Generic;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AnimeMultiSource
{
    public class Plugin : BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
    {
        public override string Name => Constants.PluginName;
        public override string Description => "Multi-source anime metadata provider using .plexmatch files and Fribb anime lists";
        public override Guid Id => Guid.Parse(Constants.PluginGuid);

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

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