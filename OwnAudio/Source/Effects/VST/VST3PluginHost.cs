using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// VST3 plugin host that manages the plugin lifecycle and editor window.
    ///
    /// Threading model:
    ///   Plugin thread  – all native VST operations (load, init, parameter reads) run here
    ///                    via ThreadedVst3Wrapper. Never blocks the UI thread.
    ///   Audio thread   – ProcessAudio() is called directly; the ThreadedVst3Wrapper drains
    ///                    the lock-free SPSC queue before each block (SetParameter, tempo, …).
    ///   UI thread      – uses CreateAsync / OpenEditorAsync / SetParameter (lock-free enqueue).
    ///   Editor thread  – native window + CreateEditor/CloseEditor on the caller (UI) thread
    ///                    as required by VST3 and macOS Cocoa.
    ///
    /// Required usage sequence:
    ///   1. var host = await VST3PluginHost.CreateAsync(path);      // load on plugin thread
    ///   2. await host.InitializeAudioAsync(sampleRate, blockSize); // audio init on plugin thread
    ///   3. var proc = host.GetProcessor();                         // only when IsReady == true
    ///   4. mixer.AddMasterEffect(proc);
    ///   5. mixer.Stop(); host.Dispose();                           // clean shutdown
    /// </summary>
    public sealed class VST3PluginHost : IDisposable, IAsyncDisposable
    {
        private readonly ThreadedVst3Wrapper _threaded;
        private readonly string _pluginPath;
        private VstEditorController? _editorController;
        private bool _disposed;

        // Properties cached after load on the plugin thread – safe to read from any thread.
        private readonly string _name;
        private readonly string _vendor;
        private readonly string? _version;
        private readonly bool _isEffect;
        private readonly bool _isInstrument;
        private readonly bool _hasEditor;

        /// <summary>
        /// Gets the plugin name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets the plugin vendor/manufacturer.
        /// </summary>
        public string Vendor => _vendor;

        /// <summary>
        /// Gets the plugin version.
        /// </summary>
        public string? Version => _version;

        /// <summary>
        /// Gets whether the plugin is an audio effect.
        /// </summary>
        public bool IsEffect => _isEffect;

        /// <summary>
        /// Gets whether the plugin is an instrument.
        /// </summary>
        public bool IsInstrument => _isInstrument;

        /// <summary>
        /// Gets whether the plugin has a graphical editor.
        /// </summary>
        public bool HasEditor => _hasEditor;

        /// <summary>
        /// Gets whether the editor window is currently open.
        /// </summary>
        public bool IsEditorOpen => _editorController?.IsEditorOpen ?? false;

        /// <summary>
        /// Gets the full path to the loaded VST3 plugin file.
        /// </summary>
        public string PluginPath => _pluginPath;

        /// <summary>
        /// Gets whether the plugin has been audio-initialized and is ready to process audio.
        /// Must be true before calling <see cref="GetProcessor"/>.
        /// Safe to read from any thread.
        /// </summary>
        public bool IsReady => !_disposed && _threaded.IsReady;

        /// <summary>
        /// Gets the current plugin state. Safe to read from any thread.
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
        /// Creates a new VST3 plugin host and loads the plugin synchronously.
        /// Blocks the calling thread during native library loading (may take 50–500 ms).
        /// Prefer <see cref="CreateAsync"/> when called from a UI thread.
        /// After construction call <see cref="InitializeAudioAsync"/> before <see cref="GetProcessor"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">When pluginPath is null or empty.</exception>
        /// <exception cref="InvalidOperationException">When plugin fails to load.</exception>
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
        /// Asynchronously loads a VST3 plugin on the dedicated plugin thread and returns a ready host.
        /// The calling (UI) thread is never blocked. After this call invoke
        /// <see cref="InitializeAudioAsync"/> before <see cref="GetProcessor"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">When pluginPath is null or empty.</exception>
        /// <exception cref="InvalidOperationException">When plugin fails to load.</exception>
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

            bool hasEditor = true;

            return new VST3PluginHost(pluginPath, threaded, name, vendor, version,
                                      isEffect, isInstrument, hasEditor);
        }

        /// <summary>
        /// Initializes the VST3 plugin for audio processing on the dedicated plugin thread.
        /// Must be called and awaited before <see cref="GetProcessor"/>.
        /// Sets <see cref="State"/> to <c>Ready</c> on success.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate (e.g. 44100, 48000).</param>
        /// <param name="maxBlockSize">Maximum number of frames per processing block.</param>
        /// <returns>True if initialization succeeded; false otherwise.</returns>
        /// <exception cref="ObjectDisposedException">When the host has been disposed.</exception>
        public Task<bool> InitializeAudioAsync(int sampleRate, int maxBlockSize)
        {
            ThrowIfDisposed();
            return _threaded.InitializeAsync(sampleRate, maxBlockSize);
        }

        /// <summary>
        /// Opens the plugin's graphical editor synchronously.
        /// GetEditorSize is read from the cached InnerWrapper; CreateEditor runs on the UI thread.
        /// </summary>
        /// <exception cref="InvalidOperationException">When the plugin has no editor.</exception>
        public void OpenEditor(string? title = null)
        {
            ThrowIfDisposed();

            if (!_hasEditor)
                throw new InvalidOperationException($"Plugin '{_name}' does not have a graphical editor.");

            CloseEditor();

            _editorController ??= new VstEditorController(_threaded);
            _editorController.OpenEditor(title ?? _name ?? "VST3 Plugin");
        }

        /// <summary>
        /// Opens the plugin's graphical editor without blocking the UI thread.
        /// GetEditorSize is fetched asynchronously on the plugin thread;
        /// CreateEditor runs on the caller (UI) thread as required by VST3/macOS.
        /// </summary>
        /// <exception cref="InvalidOperationException">When the plugin has no editor.</exception>
        public async Task OpenEditorAsync(string? title = null)
        {
            ThrowIfDisposed();

            if (!_hasEditor)
                throw new InvalidOperationException($"Plugin '{_name}' does not have a graphical editor.");

            CloseEditor();

            _editorController ??= new VstEditorController(_threaded);
            await _editorController.OpenEditorAsync(title ?? _name ?? "VST3 Plugin").ConfigureAwait(true);
        }

        /// <summary>Closes the plugin's graphical editor if it is open.</summary>
        public void CloseEditor()
        {
            _editorController?.CloseEditor();
        }

        /// <summary>
        /// Returns a <see cref="VST3EffectProcessor"/> for use in an effect chain.
        /// The plugin must be in <c>Ready</c> state — call and await
        /// <see cref="InitializeAudioAsync"/> first.
        /// The processor shares the same <see cref="ThreadedVst3Wrapper"/> and does NOT own it.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// When the plugin is not audio-initialized. Call InitializeAudioAsync() first.
        /// </exception>
        public VST3EffectProcessor GetProcessor()
        {
            ThrowIfDisposed();

            if (!_threaded.IsReady)
                throw new InvalidOperationException(
                    $"VST3 plugin '{_name}' is not audio-initialized. " +
                    $"Call and await InitializeAudioAsync(sampleRate, blockSize) before GetProcessor(). " +
                    $"Current state: {_threaded.State}");

            return new VST3EffectProcessor(_threaded);
        }

        /// <summary>
        /// Gets the preferred editor window size.
        /// </summary>
        public async Task<(int Width, int Height)?> GetEditorSizeAsync()
        {
            ThrowIfDisposed();
            var size = await _threaded.GetEditorSizeAsync().ConfigureAwait(false);
            return size is null ? null : (size.Value.Width, size.Value.Height);
        }

        /// <summary>
        /// Gets all parameter information from the VST3 plugin (plugin thread).
        /// </summary>
        public async Task<VST3ParameterInfo[]> GetParametersAsync()
        {
            ThrowIfDisposed();
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
        /// Gets the parameter count (plugin thread).
        /// </summary>
        public Task<int> GetParameterCountAsync()
        {
            ThrowIfDisposed();
            return _threaded.GetParameterCountAsync();
        }

        /// <summary>
        /// Gets a parameter value (plugin thread).
        /// </summary>
        public Task<double> GetParameterAsync(int id)
        {
            ThrowIfDisposed();
            return _threaded.GetParameterAsync(id);
        }

        /// <summary>
        /// Sets a parameter value. Lock-free enqueue to the UI→audio SPSC queue;
        /// applied on the audio thread before the next block (~11 ms latency at 44100/512).
        /// Safe to call from any thread.
        /// </summary>
        public void SetParameter(int id, double value)
        {
            ThrowIfDisposed();
            _threaded.SetParameter(id, value);
        }

        /// <summary>
        /// Sets multiple parameter values synchronously on the dedicated plugin thread.
        /// Unlike SetParameter (SPSC queue, audio thread), this method applies each parameter
        /// directly via the plugin thread — the same thread used during initialization and
        /// GetParametersAsync — so the native controller state is updated immediately.
        /// Intended for non-realtime use such as project state restoration; do not call
        /// during active audio processing.
        /// </summary>
        public async Task SetParametersAsync(IReadOnlyDictionary<int, double> parameters)
        {
            ThrowIfDisposed();
            foreach (var kv in parameters)
                await _threaded.SetParameterAsync(kv.Key, kv.Value).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the complete processor state as a byte array (plugin thread).
        /// Returns null if the plugin does not support state serialization.
        /// </summary>
        public Task<byte[]?> GetStateAsync()
        {
            ThrowIfDisposed();
            return _threaded.GetStateAsync();
        }

        /// <summary>
        /// Restores the complete processor state and syncs the controller display (plugin thread).
        /// More reliable than SetParametersAsync for full state restoration.
        /// </summary>
        public Task<bool> SetStateAsync(byte[] stateData)
        {
            ThrowIfDisposed();
            return _threaded.SetStateAsync(stateData);
        }
        

        /// <summary>
        /// Scans system directories for VST3 plugins.
        /// </summary>
        public static List<string> FindPlugins(bool includeSubdirectories = true)
            => OwnVst3Wrapper.FindVst3Plugins(includeSubdirectories);

        /// <summary>
        /// Scans custom directories for VST3 plugins.
        /// </summary>
        public static List<string> FindPlugins(string[] searchDirectories, bool includeSubdirectories = true)
            => OwnVst3Wrapper.FindVst3Plugins(searchDirectories, includeSubdirectories);

        /// <summary>
        /// Quickly builds a plugin list from the filesystem without loading any plugin.
        /// Names come from the bundle/file name (macOS: Info.plist CFBundleName when available).
        /// Use this on startup; call <see cref="ScanPlugins"/> only when full metadata is needed.
        /// </summary>
        public static List<VST3PluginInfo> ScanPluginsQuick(bool includeSubdirectories = true)
        {
            var paths = OwnVst3Wrapper.FindVst3Plugins(includeSubdirectories);
            var results = new List<VST3PluginInfo>(paths.Count);

            foreach (var path in paths)
            {
                string name = ReadPluginNameFromFilesystem(path);
                results.Add(new VST3PluginInfo
                {
                    Path = path,
                    Name = name,
                    Vendor = string.Empty,
                    Version = null,
                    IsEffect = true,
                    IsInstrument = false,
                    ParameterCount = 0
                });
            }

            return results;
        }

        private static string ReadPluginNameFromFilesystem(string path)
        {
            if (OperatingSystem.IsMacOS())
            {
                string plistPath = System.IO.Path.Combine(path, "Contents", "Info.plist");
                if (System.IO.File.Exists(plistPath))
                {
                    string? plistName = ReadCFBundleNameFromPlist(plistPath);
                    if (!string.IsNullOrEmpty(plistName))
                        return plistName;
                }
            }

            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        private static string? ReadCFBundleNameFromPlist(string plistPath)
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
        /// Scans and returns detailed info about available VST3 plugins.
        /// Loads each plugin briefly to read metadata — can be slow with many plugins.
        /// Prefer <see cref="ScanPluginsQuick"/> for building the initial list.
        /// </summary>
        public static List<VST3PluginInfo> ScanPlugins(bool includeSubdirectories = true)
        {
            var paths = OwnVst3Wrapper.FindVst3Plugins(includeSubdirectories);
            var results = new List<VST3PluginInfo>();

            foreach (var path in paths)
            {
                try
                {
                    using var wrapper = new OwnVst3Wrapper();
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
                catch {}
            }

            return results;
        }


        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VST3PluginHost));
        }

        /// <summary>
        /// Preferred disposal path on macOS. Closes the editor, then yields the calling
        /// thread for 200 ms so that any JUCE timer callbacks already queued in the
        /// CFRunLoop (e.g. ModuleEditor::timerCallback) can execute and exit cleanly
        /// before the native library is unloaded. Without this gap those callbacks
        /// would access freed memory and produce SIGSEGV.
        /// On other platforms this behaves identically to <see cref="Dispose"/>.
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
        /// Stops the plugin, closes the editor, and releases all native resources.
        /// On macOS this blocks the calling thread briefly to let JUCE RunLoop timer
        /// callbacks drain before the native library is freed. Prefer
        /// <see cref="DisposeAsync"/> when an async context is available.
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

        public override string ToString() =>
            $"VST3PluginHost: {_name} ({_vendor}), State={_threaded.State}, HasEditor={_hasEditor}";
    }
}
