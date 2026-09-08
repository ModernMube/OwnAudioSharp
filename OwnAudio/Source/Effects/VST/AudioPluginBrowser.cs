using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OwnVST3Host;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// Plugin formats we can host. AudioUnit is macOS only.
    /// </summary>
    [Flags]
    public enum AudioPluginFormat
    {
        None = 0,
        Vst3 = 0x01,
        AudioUnit = 0x02,
        All = Vst3 | AudioUnit
    }

    /// <summary>
    /// One plugin the browser found. Identifier is what VST3PluginHost.CreateAsync takes:
    /// a bundle path for VST3, an "AudioUnit:..." token for AU.
    /// </summary>
    public sealed class AudioPluginInfo
    {
        public string Name { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string Version { get; init; } = "";
        public string Category { get; init; } = "";
        public string Identifier { get; init; } = "";
        public AudioPluginFormat Format { get; init; }

        /// <summary>
        /// Empty for AUs that live only in the component registry and have no bundle on disk.
        /// </summary>
        public string FilePath { get; init; } = "";

        public bool IsInstrument { get; init; }

        /// <summary>
        /// -1 after a quick scan until ResolveAsync loads the plugin and fills it in.
        /// </summary>
        public int InputChannels { get; init; } = -1;
        public int OutputChannels { get; init; } = -1;

        public bool IsResolved => InputChannels >= 0;

        public override string ToString() => $"{Name} — {Vendor} ({Format})";
    }

    /// <summary>
    /// How far the scan got, for an IProgress report.
    /// </summary>
    public readonly struct AudioPluginScanProgress
    {
        public float Fraction { get; }
        public string CurrentItem { get; }
        public int FoundSoFar { get; }

        public AudioPluginScanProgress(float fraction, string currentItem, int foundSoFar)
        {
            Fraction = fraction;
            CurrentItem = currentItem;
            FoundSoFar = foundSoFar;
        }
    }

    /// <summary>
    /// Finds installed plugins across every format the native host was built with.
    /// VST3PluginHost.FindPlugins only walks the VST3 directories, and AudioUnits have no
    /// directory to walk — they live in the macOS component registry — so this is the way in
    /// when you want both.
    /// </summary>
    public static class AudioPluginBrowser
    {
        /// <summary>
        /// False against an OwnAudioVst native library older than 1.7.0.
        /// </summary>
        public static bool IsSupported => PluginScanner.IsSupported;

        /// <summary>
        /// AudioUnits exist on macOS only; elsewhere a scan just returns VST3s.
        /// </summary>
        public static bool AudioUnitSupported => PluginScanner.AudioUnitSupported;

        public static bool IsScanning => PluginScanner.IsScanning;

        /// <summary>
        /// Asks the running scan to stop after the plugin it is on.
        /// </summary>
        public static void Cancel() => PluginScanner.Cancel();

        /// <summary>
        /// Lists what is installed. Quick is near-instant but leaves the channel counts at -1;
        /// deep loads every plugin to fill them in and takes minutes on a big collection.
        /// </summary>
        public static async Task<IReadOnlyList<AudioPluginInfo>> ScanAsync(
            AudioPluginFormat formats = AudioPluginFormat.All,
            bool deepScan = false,
            IProgress<AudioPluginScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            IProgress<ScanProgress>? _inner = progress == null
                ? null
                : new Progress<ScanProgress>(p =>
                    progress.Report(new AudioPluginScanProgress(p.Fraction, p.CurrentItem, p.FoundSoFar)));

            var found = await PluginScanner.ScanAsync(
                _toNative(formats),
                deepScan ? ScanMode.Full : ScanMode.Fast,
                _inner,
                cancellationToken).ConfigureAwait(false);

            return _toInfo(found);
        }

        /// <summary>
        /// Loads the one plugin to fill in what a quick scan left out. Costs as long as loading
        /// that plugin does; null when it cannot be loaded at all.
        /// </summary>
        public static async Task<AudioPluginInfo?> ResolveAsync(AudioPluginInfo plugin,
                                                               CancellationToken cancellationToken = default)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            var descriptor = new PluginDescriptor
            {
                Identifier = plugin.Identifier,
                Format = _toNative(plugin.Format)
            };

            var resolved = await PluginScanner.ResolveAsync(descriptor, cancellationToken).ConfigureAwait(false);
            return resolved == null ? null : _toInfo(resolved);
        }

        /// <summary>
        /// Whatever the last scan or cache restore produced. Fine to call while a scan runs.
        /// </summary>
        public static IReadOnlyList<AudioPluginInfo> GetResults() => _toInfo(PluginScanner.GetResults());

        /// <summary>
        /// Persist this next to your settings and hand it back through RestoreCache to skip
        /// the scan on the next start.
        /// </summary>
        public static string GetCacheXml() => PluginScanner.GetCacheXml();

        /// <returns>False on malformed XML, or while a scan is in flight.</returns>
        public static bool RestoreCache(string xml) => PluginScanner.RestoreCache(xml);

        private static PluginFormat _toNative(AudioPluginFormat formats)
        {
            PluginFormat _result = 0;
            if ((formats & AudioPluginFormat.Vst3) != 0) _result |= PluginFormat.Vst3;
            if ((formats & AudioPluginFormat.AudioUnit) != 0) _result |= PluginFormat.AudioUnit;
            return _result;
        }

        private static IReadOnlyList<AudioPluginInfo> _toInfo(IReadOnlyList<PluginDescriptor> found)
        {
            var result = new List<AudioPluginInfo>(found.Count);
            for (int i = 0; i < found.Count; i++)
                result.Add(_toInfo(found[i]));

            return result;
        }

        private static AudioPluginInfo _toInfo(PluginDescriptor d) => new AudioPluginInfo
        {
            Name = d.Name,
            Vendor = d.Vendor,
            Version = d.Version,
            Category = d.Category,
            Identifier = d.Identifier,
            FilePath = d.FilePath,
            Format = d.Format == PluginFormat.AudioUnit ? AudioPluginFormat.AudioUnit : AudioPluginFormat.Vst3,
            IsInstrument = d.IsInstrument,
            InputChannels = d.InputChannels,
            OutputChannels = d.OutputChannels
        };
    }
}
