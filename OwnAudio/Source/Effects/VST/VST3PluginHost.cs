using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// Owns a VST3 plugin: loading, audio init, editor window, teardown.
    /// Everything native runs on the dedicated plugin thread, the UI thread never blocks.
    /// Usual order: CreateAsync -> InitializeAudioAsync -> GetProcessor -> add to mixer -> Dispose last.
    /// </summary>
    public sealed class VST3PluginHost : IDisposable, IAsyncDisposable
    {
        private readonly ThreadedVst3Wrapper _threaded;
        private readonly string _pluginPath;
        private VstEditorController? _editorController;
        private bool _disposed;

        private readonly string _name;
        private readonly string _vendor;
        private readonly string? _version;
        private readonly bool _isEffect;
        private readonly bool _isInstrument;
        private readonly bool _hasEditor;

        /// <summary>
        /// Plugin name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Vendor / manufacturer.
        /// </summary>
        public string Vendor => _vendor;

        /// <summary>
        /// Version string, may be null.
        /// </summary>
        public string? Version => _version;

        /// <summary>
        /// True for audio effects.
        /// </summary>
        public bool IsEffect => _isEffect;

        /// <summary>
        /// True for instruments.
        /// </summary>
        public bool IsInstrument => _isInstrument;

        /// <summary>
        /// Whether the plugin brings its own GUI.
        /// </summary>
        public bool HasEditor => _hasEditor;

        /// <summary>
        /// Is the editor window up right now.
        /// </summary>
        public bool IsEditorOpen => _editorController?.IsEditorOpen ?? false;

        /// <summary>
        /// Path we loaded the plugin from.
        /// </summary>
        public string PluginPath => _pluginPath;

        /// <summary>
        /// Audio init done and not disposed — must be true before GetProcessor().
        /// </summary>
        public bool IsReady => !_disposed && _threaded.IsReady;

        /// <summary>
        /// Current plugin state, readable from any thread.
        /// </summary>
        public VstPluginState State => _threaded.State;


        private VST3PluginHost(
            string pluginPath,
            ThreadedVst3Wrapper threaded,
            string name,
            string vendor,
            string? version,
            bool isEffect,
            bool isInstrument,
            bool hasEditor)
        {
            _pluginPath = pluginPath;
            _threaded = threaded;
            _name = name;
            _vendor = vendor;
            _version = version;
            _isEffect = isEffect;
            _isInstrument = isInstrument;
            _hasEditor = hasEditor;
        }

        /// <summary>
        /// Loads the plugin the blocking way — native loading can eat 50-500 ms, so from a UI
        /// thread use CreateAsync instead. InitializeAudioAsync still has to follow.
        /// </summary>
        public VST3PluginHost(string pluginPath)
        {
            if (string.IsNullOrEmpty(pluginPath))
                throw new ArgumentNullException(nameof(pluginPath));

            _pluginPath = pluginPath;
            _threaded = new ThreadedVst3Wrapper();

            bool loaded = _threaded.LoadPluginAsync(pluginPath).GetAwaiter().GetResult();
            if (!loaded)
            {
                _threaded.Dispose();
                throw new InvalidOperationException($"Failed to load VST3 plugin: {pluginPath}");
            }

            _name = _threaded.GetNameAsync().GetAwaiter().GetResult() ?? "VST3 Plugin";
            _vendor = _threaded.GetVendorAsync().GetAwaiter().GetResult() ?? string.Empty;
            _version = _threaded.GetVersionAsync().GetAwaiter().GetResult();
            _isEffect = _threaded.GetIsEffectAsync().GetAwaiter().GetResult();
            _isInstrument = _threaded.GetIsInstrumentAsync().GetAwaiter().GetResult();

            _hasEditor = true;
        }

        /// <summary>
        /// Same thing on the plugin thread, the caller stays responsive.
        /// </summary>
        public static async Task<VST3PluginHost> CreateAsync(string pluginPath)
        {
            if (string.IsNullOrEmpty(pluginPath))
                throw new ArgumentNullException(nameof(pluginPath));

            var threaded = new ThreadedVst3Wrapper();

            bool loaded = await threaded.LoadPluginAsync(pluginPath).ConfigureAwait(false);
            if (!loaded)
            {
                threaded.Dispose();
                throw new InvalidOperationException($"Failed to load VST3 plugin: {pluginPath}");
            }

            string name = await threaded.GetNameAsync().ConfigureAwait(false) ?? "VST3 Plugin";
            string vendor = await threaded.GetVendorAsync().ConfigureAwait(false) ?? string.Empty;
            string? version = await threaded.GetVersionAsync().ConfigureAwait(false);
            bool isEffect = await threaded.GetIsEffectAsync().ConfigureAwait(false);
            bool isInstrument = await threaded.GetIsInstrumentAsync().ConfigureAwait(false);

            return new VST3PluginHost(pluginPath, threaded, name, vendor, version, isEffect, isInstrument, true);
        }

        /// <summary>
        /// Preps the plugin for audio and flips State to Ready. Await this before GetProcessor().
        /// </summary>
        /// <param name="maxBlockSize">Biggest frame count we will ever hand it.</param>
        public Task<bool> InitializeAudioAsync(int sampleRate, int maxBlockSize)
        {
            _throwIfDisposed();
            return _threaded.InitializeAsync(sampleRate, maxBlockSize);
        }

        /// <summary>
        /// Opens the plugin GUI. CreateEditor has to run on the caller (UI) thread, VST3/Cocoa rule.
        /// </summary>
        public void OpenEditor(string? title = null)
        {
            _throwIfDisposed();

            if (!_hasEditor)
                throw new InvalidOperationException($"Plugin '{_name}' does not have a graphical editor.");

            CloseEditor();

            _editorController ??= new VstEditorController(_threaded);
            _editorController.OpenEditor(title ?? _name ?? "VST3 Plugin");
        }

        /// <summary>
        /// Same, but the size query goes to the plugin thread so the UI does not stall.
        /// </summary>
        public async Task OpenEditorAsync(string? title = null)
        {
            _throwIfDisposed();

            if (!_hasEditor)
                throw new InvalidOperationException($"Plugin '{_name}' does not have a graphical editor.");

            CloseEditor();

            _editorController ??= new VstEditorController(_threaded);
            await _editorController.OpenEditorAsync(title ?? _name ?? "VST3 Plugin").ConfigureAwait(true);
        }

        /// <summary>
        /// Shuts the editor window if there is one.
        /// </summary>
        public void CloseEditor() => _editorController?.CloseEditor();

        /// <summary>
        /// Hands out a processor for the effect chain. Plugin must be Ready.
        /// The processor shares our wrapper and does not own it.
        /// </summary>
        public VST3EffectProcessor GetProcessor()
        {
            _throwIfDisposed();

            if (!_threaded.IsReady)
                throw new InvalidOperationException(
                    $"VST3 plugin '{_name}' is not audio-initialized. " +
                    $"Call and await InitializeAudioAsync(sampleRate, blockSize) before GetProcessor(). " +
                    $"Current state: {_threaded.State}");

            return new VST3EffectProcessor(_threaded);
        }

        /// <summary>
        /// Editor size the plugin prefers.
        /// </summary>
        public async Task<(int Width, int Height)?> GetEditorSizeAsync()
        {
            _throwIfDisposed();
            var size = await _threaded.GetEditorSizeAsync().ConfigureAwait(false);
            return size is null ? null : (size.Value.Width, size.Value.Height);
        }

        /// <summary>
        /// Every param, read on the plugin thread.
        /// </summary>
        public async Task<VST3ParameterInfo[]> GetParametersAsync()
        {
            _throwIfDisposed();
            var vst3Params = await _threaded.GetAllParametersAsync().ConfigureAwait(false);
            var result = new VST3ParameterInfo[vst3Params.Count];

            for (int i = 0; i < vst3Params.Count; i++)
            {
                var p = vst3Params[i];
                result[i] = new VST3ParameterInfo(
                    (uint)p.Id, p.Name, p.CurrentValue, p.MinValue, p.MaxValue, p.DefaultValue);
            }

            return result;
        }

        /// <summary>
        /// How many params the plugin has.
        /// </summary>
        public Task<int> GetParameterCountAsync()
        {
            _throwIfDisposed();
            return _threaded.GetParameterCountAsync();
        }

        /// <summary>
        /// One param value, read on the plugin thread.
        /// </summary>
        public Task<double> GetParameterAsync(int id)
        {
            _throwIfDisposed();
            return _threaded.GetParameterAsync(id);
        }

        /// <summary>
        /// Param change through the lock-free queue, applied on the audio thread before the next
        /// block (~11 ms at 44100/512). Callable from anywhere.
        /// </summary>
        public void SetParameter(int id, double value)
        {
            _throwIfDisposed();
            _threaded.SetParameter(id, value);
        }

        /// <summary>
        /// Bulk set on the plugin thread so the native controller updates immediately.
        /// For project load and such, not while audio is running.
        /// </summary>
        public async Task SetParametersAsync(IReadOnlyDictionary<int, double> parameters)
        {
            _throwIfDisposed();
            foreach (var kv in parameters)
                await _threaded.SetParameterAsync(kv.Key, kv.Value).ConfigureAwait(false);
        }

        /// <summary>
        /// Full processor state blob, null when the plugin cannot serialize itself.
        /// </summary>
        public Task<byte[]?> GetStateAsync()
        {
            _throwIfDisposed();
            return _threaded.GetStateAsync();
        }

        /// <summary>
        /// Restores a state blob and syncs the GUI. More reliable than SetParametersAsync.
        /// </summary>
        public Task<bool> SetStateAsync(byte[] stateData)
        {
            _throwIfDisposed();
            return _threaded.SetStateAsync(stateData);
        }


        /// <summary>
        /// Walks the system VST3 folders.
        /// </summary>
        public static List<string> FindPlugins(bool includeSubdirectories = true)
            => OwnVst3Wrapper.FindVst3Plugins(includeSubdirectories);

        /// <summary>
        /// Same, but with your own folders.
        /// </summary>
        public static List<string> FindPlugins(string[] searchDirectories, bool includeSubdirectories = true)
            => OwnVst3Wrapper.FindVst3Plugins(searchDirectories, includeSubdirectories);

        /// <summary>
        /// Cheap plugin list straight from the filesystem, nothing gets loaded. Names come from the
        /// bundle (Info.plist on mac). Use this at startup, ScanPlugins only when you need metadata.
        /// </summary>
        public static List<VST3PluginInfo> ScanPluginsQuick(bool includeSubdirectories = true)
        {
            var paths = OwnVst3Wrapper.FindVst3Plugins(includeSubdirectories);
            var results = new List<VST3PluginInfo>(paths.Count);

            foreach (var path in paths)
            {
                results.Add(new VST3PluginInfo
                {
                    Path = path,
                    Name = _readPluginName(path),
                    Vendor = string.Empty,
                    Version = null,
                    IsEffect = true,
                    IsInstrument = false,
                    ParameterCount = 0
                });
            }

            return results;
        }

        private static string _readPluginName(string path)
        {
            if (OperatingSystem.IsMacOS())
            {
                string plistPath = System.IO.Path.Combine(path, "Contents", "Info.plist");
                if (System.IO.File.Exists(plistPath))
                {
                    string? plistName = _readBundleName(plistPath);
                    if (!string.IsNullOrEmpty(plistName)) return plistName;
                }
            }

            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        private static string? _readBundleName(string plistPath)
        {
            try
            {
                string content = System.IO.File.ReadAllText(plistPath);
                int keyIdx = content.IndexOf("<key>CFBundleName</key>", StringComparison.Ordinal);
                if (keyIdx < 0) return null;

                int strStart = content.IndexOf("<string>", keyIdx, StringComparison.Ordinal);
                int strEnd = content.IndexOf("</string>", strStart, StringComparison.Ordinal);
                if (strStart < 0 || strEnd < 0) return null;

                strStart += "<string>".Length;
                string name = content.Substring(strStart, strEnd - strStart).Trim();
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Detailed scan — every plugin gets loaded briefly, so with a big folder this crawls.
        /// Prefer ScanPluginsQuick for the initial list.
        /// </summary>
        public static List<VST3PluginInfo> ScanPlugins(bool includeSubdirectories = true)
        {
            var paths = OwnVst3Wrapper.FindVst3Plugins(includeSubdirectories);
            var results = new List<VST3PluginInfo>();

            foreach (var path in paths)
            {
                try
                {
                    using (var wrapper = new OwnVst3Wrapper())
                    {
                        if (wrapper.LoadPlugin(path))
                        {
                            results.Add(new VST3PluginInfo
                            {
                                Path = path,
                                Name = wrapper.Name,
                                Vendor = wrapper.Vendor,
                                Version = wrapper.Version,
                                IsEffect = wrapper.IsEffect,
                                IsInstrument = wrapper.IsInstrument,
                                ParameterCount = wrapper.GetParameterCount()
                            });
                        }
                    }
                }
                catch {}
            }

            return results;
        }


        private void _throwIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VST3PluginHost));
        }

        /// <summary>
        /// The good teardown on macOS. After closing the editor we let the CFRunLoop breathe for
        /// 200 ms so already queued JUCE timer callbacks can finish — unload them underneath and
        /// you get a SIGSEGV. Elsewhere it is just Dispose.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            CloseEditor();
            _editorController?.Dispose();
            _editorController = null;

            if (OperatingSystem.IsMacOS())
                await Task.Delay(200).ConfigureAwait(false);

            _threaded?.Dispose();
        }

        /// <summary>
        /// Blocking teardown. On macOS it sleeps a bit for the same JUCE timer reason —
        /// use DisposeAsync when you have an async context.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CloseEditor();
            _editorController?.Dispose();
            _editorController = null;

            if (OperatingSystem.IsMacOS())
                System.Threading.Thread.Sleep(150);

            _threaded?.Dispose();
        }

        /// <summary>
        /// Diagnostics only.
        /// </summary>
        public override string ToString() =>
            $"VST3PluginHost: {_name} ({_vendor}), State={_threaded.State}, HasEditor={_hasEditor}";
    }
}
