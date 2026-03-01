using System;
using System.Collections.Generic;
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// VST3 plugin host that manages the plugin lifecycle and editor window.
    /// This class separates UI concerns from audio processing.
    /// Based on the OwnVST3EditorDemo architecture.
    /// </summary>
    /// <remarks>
    /// Responsibilities:
    /// - Load and manage VST3 plugin lifecycle (OwnVst3Wrapper)
    /// - Open and close the plugin's graphical editor (VstEditorController)
    /// - Provide processor instances for audio processing
    /// 
    /// Usage:
    /// 1. Create a VST3PluginHost with a plugin path
    /// 2. Call OpenEditor() to show the plugin UI (optional)
    /// 3. Call GetProcessor() to get an audio processor for the engine
    /// 4. Dispose when done
    /// </remarks>
    public sealed class VST3PluginHost : IDisposable
    {
        private readonly OwnVst3Wrapper _wrapper;
        private readonly string _pluginPath;
        private VstEditorController? _editorController;
        private bool _disposed;

        /// <summary>
        /// Gets the plugin name.
        /// </summary>
        public string Name => _wrapper?.Name ?? string.Empty;

        /// <summary>
        /// Gets the plugin vendor/manufacturer.
        /// </summary>
        public string Vendor => _wrapper?.Vendor ?? string.Empty;

        /// <summary>
        /// Gets the plugin version.
        /// </summary>
        public string Version => _wrapper?.Version ?? string.Empty;

        /// <summary>
        /// Gets whether the plugin is an audio effect.
        /// </summary>
        public bool IsEffect => _wrapper?.IsEffect ?? false;

        /// <summary>
        /// Gets whether the plugin is an instrument.
        /// </summary>
        public bool IsInstrument => _wrapper?.IsInstrument ?? false;

        /// <summary>
        /// Gets whether the plugin has a graphical editor.
        /// </summary>
        public bool HasEditor => _wrapper?.GetEditorSize() != null;

        /// <summary>
        /// Gets whether the editor window is currently open.
        /// </summary>
        public bool IsEditorOpen => _editorController?.IsEditorOpen ?? false;

        /// <summary>
        /// Gets the full path to the loaded VST3 plugin file.
        /// </summary>
        public string PluginPath => _pluginPath;

        /// <summary>
        /// Creates a new VST3 plugin host and loads the specified plugin.
        /// </summary>
        /// <param name="pluginPath">Full path to the .vst3 file.</param>
        /// <exception cref="ArgumentNullException">When pluginPath is null or empty.</exception>
        /// <exception cref="InvalidOperationException">When plugin fails to load.</exception>
        public VST3PluginHost(string pluginPath)
        {
            if (string.IsNullOrEmpty(pluginPath))
                throw new ArgumentNullException(nameof(pluginPath));

            _pluginPath = pluginPath;
            _wrapper = new OwnVst3Wrapper();

            if (!_wrapper.LoadPlugin(pluginPath))
            {
                _wrapper.Dispose();
                throw new InvalidOperationException($"Failed to load VST3 plugin: {pluginPath}");
            }
        }

        /// <summary>
        /// Opens the plugin's graphical editor in a native window.
        /// </summary>
        /// <param name="title">Optional window title. If null, uses the plugin name.</param>
        /// <exception cref="InvalidOperationException">When the plugin has no editor.</exception>
        /// <exception cref="ObjectDisposedException">When the host has been disposed.</exception>
        public void OpenEditor(string? title = null)
        {
            ThrowIfDisposed();

            if (!HasEditor)
                throw new InvalidOperationException($"Plugin '{Name}' does not have a graphical editor.");

            // Close existing editor if open
            CloseEditor();

            // Create editor controller if needed
            if (_editorController == null)
            {
                _editorController = new VstEditorController(_wrapper);
            }

            // Open the editor with the specified title or plugin name
            string windowTitle = title ?? Name ?? "VST3 Plugin";
            _editorController.OpenEditor(windowTitle);
        }

        /// <summary>
        /// Closes the plugin's graphical editor if it's open.
        /// </summary>
        public void CloseEditor()
        {
            _editorController?.CloseEditor();
        }

        /// <summary>
        /// Creates a new VST3EffectProcessor instance for audio processing.
        /// The processor shares the same underlying VST3 wrapper.
        /// </summary>
        /// <returns>A new VST3EffectProcessor instance.</returns>
        /// <exception cref="ObjectDisposedException">When the host has been disposed.</exception>
        /// <remarks>
        /// The returned processor does NOT own the wrapper and will not dispose it.
        /// Multiple processors can be created from the same host, but typically
        /// you only need one processor per plugin instance.
        /// </remarks>
        public VST3EffectProcessor GetProcessor()
        {
            ThrowIfDisposed();
            return new VST3EffectProcessor(_wrapper);
        }

        /// <summary>
        /// Gets the preferred editor window size.
        /// </summary>
        /// <returns>Width and height tuple, or null if no editor available.</returns>
        public (int Width, int Height)? GetEditorSize()
        {
            var size = _wrapper?.GetEditorSize();
            if (size == null) return null;
            return (size.Value.Width, size.Value.Height);
        }

        /// <summary>
        /// Gets the number of exposed parameters.
        /// </summary>
        public int ParameterCount => _wrapper?.GetParameterCount() ?? 0;

        /// <summary>
        /// Gets all parameter information from the VST3 plugin.
        /// </summary>
        /// <returns>Array of parameter information.</returns>
        public VST3ParameterInfo[] GetParameters()
        {
            if (_wrapper == null) return Array.Empty<VST3ParameterInfo>();

            var vst3Params = _wrapper.GetAllParameters();
            var result = new VST3ParameterInfo[vst3Params.Count];

            for (int i = 0; i < vst3Params.Count; i++)
            {
                var p = vst3Params[i];
                result[i] = new VST3ParameterInfo(
                    (uint)p.Id,
                    p.Name,
                    p.CurrentValue,
                    p.MinValue,
                    p.MaxValue,
                    p.DefaultValue
                );
            }

            return result;
        }

        /// <summary>
        /// Sets a parameter value by ID.
        /// </summary>
        /// <param name="id">Parameter ID.</param>
        /// <param name="value">Normalized value (typically 0.0 to 1.0).</param>
        public void SetParameter(int id, double value)
        {
            _wrapper?.SetParameter(id, value);
        }

        /// <summary>
        /// Gets a parameter value by ID.
        /// </summary>
        /// <param name="id">Parameter ID.</param>
        /// <returns>Current parameter value.</returns>
        public double GetParameter(int id)
        {
            return _wrapper?.GetParameter(id) ?? 0.0;
        }

        /// <summary>
        /// Scans system directories for VST3 plugins.
        /// </summary>
        /// <param name="includeSubdirectories">Whether to scan subdirectories.</param>
        /// <returns>List of plugin file paths.</returns>
        public static List<string> FindPlugins(bool includeSubdirectories = true)
        {
            return OwnVst3Wrapper.FindVst3Plugins(includeSubdirectories);
        }

        /// <summary>
        /// Scans custom directories for VST3 plugins.
        /// </summary>
        /// <param name="searchDirectories">Directories to search.</param>
        /// <param name="includeSubdirectories">Whether to scan subdirectories.</param>
        /// <returns>List of plugin file paths.</returns>
        public static List<string> FindPlugins(string[] searchDirectories, bool includeSubdirectories = true)
        {
            return OwnVst3Wrapper.FindVst3Plugins(searchDirectories, includeSubdirectories);
        }

        /// <summary>
        /// Scans and returns detailed info about available VST3 plugins.
        /// Note: This is slow as it loads each plugin briefly.
        /// </summary>
        /// <param name="includeSubdirectories">Whether to scan subdirectories.</param>
        /// <returns>List of plugin information.</returns>
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
                catch
                {
                    // Skip plugins that fail to load
                }
            }

            return results;
        }

        /// <summary>
        /// Throws ObjectDisposedException if the host has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VST3PluginHost));
        }

        /// <summary>
        /// Disposes the plugin host and releases all resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            // Close editor first
            CloseEditor();
            _editorController?.Dispose();
            _editorController = null;

            // Dispose the VST3 wrapper
            _wrapper?.Dispose();

            _disposed = true;
        }

        /// <summary>
        /// Returns a string representation of the plugin host.
        /// </summary>
        public override string ToString()
        {
            return $"VST3PluginHost: {Name} ({Vendor}), HasEditor={HasEditor}, EditorOpen={IsEditorOpen}";
        }
    }
}
