using System;
using System.IO;
using System.Text.Json;
using Logger;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Manages SmartMaster preset loading, saving, and factory preset creation.
    /// </summary>
    internal sealed class SmartMasterPresetManager
    {
        private readonly string _presetsDirectory;
        private const string DEFAULT_PRESET_NAME = "default.smartmaster.json";
        
        /// <summary>
        /// Creates a new preset manager.
        /// </summary>
        /// <param name="presetsDirectory">Directory path for storing presets.</param>
        public SmartMasterPresetManager(string presetsDirectory)
        {
            _presetsDirectory = presetsDirectory ?? throw new ArgumentNullException(nameof(presetsDirectory));
            
            // Ensure directory exists
            if (!Directory.Exists(_presetsDirectory))
            {
                Directory.CreateDirectory(_presetsDirectory);
            }
        }
        
        /// <summary>
        /// Gets the presets directory path.
        /// </summary>
        public string PresetsDirectory => _presetsDirectory;
        
        /// <summary>
        /// Creates factory presets for different speaker types if they don't exist.
        /// </summary>
        public void CreateFactoryPresetsIfNeeded()
        {
            foreach (SpeakerType speakerType in Enum.GetValues(typeof(SpeakerType)))
            {
                string filename = SmartMasterPresetFactory.GetPresetFilename(speakerType);
                string filePath = Path.Combine(_presetsDirectory, filename);
                
                // Only create if doesn't exist
                if (!File.Exists(filePath))
                {
                    try
                    {
                        var factoryConfig = SmartMasterPresetFactory.CreatePreset(speakerType);
                        SaveInternal(factoryConfig, filePath);
                        Log.Info($"[SmartMaster] Created factory preset: {filename}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[SmartMaster] Failed to create factory preset {filename}: {ex.Message}");
                    }
                }
            }
        }
        
        /// <summary>
        /// Saves the configuration to a preset file.
        /// </summary>
        /// <param name="config">Configuration to save.</param>
        /// <param name="presetName">Preset name (without extension).</param>
        public void Save(SmartMasterConfig config, string presetName)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            
            if (string.IsNullOrWhiteSpace(presetName))
                throw new ArgumentException("Preset name cannot be empty", nameof(presetName));
            
            string fileName = $"{presetName}.smartmaster.json";
            string filePath = Path.Combine(_presetsDirectory, fileName);
            
            try
            {
                SaveInternal(config, filePath);
                Log.Info($"[SmartMaster] Preset saved: {filePath}");
            }
            catch (Exception ex)
            {
                Log.Error($"[SmartMaster] Preset save error: {ex.Message}");
                throw new InvalidOperationException($"Failed to save preset: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Loads a preset from disk.
        /// </summary>
        /// <param name="presetName">Preset name (without extension).</param>
        /// <returns>Loaded configuration.</returns>
        public SmartMasterConfig Load(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
                throw new ArgumentException("Preset name cannot be empty", nameof(presetName));
            
            string fileName = $"{presetName}.smartmaster.json";
            string filePath = Path.Combine(_presetsDirectory, fileName);
            
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Preset not found: {filePath}");
            
            try
            {
                string json = File.ReadAllText(filePath);
                
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                
                var loadedConfig = JsonSerializer.Deserialize<SmartMasterConfig>(json, options);
                
                if (loadedConfig == null)
                    throw new InvalidOperationException("Failed to deserialize preset");
                
                Log.Info($"[SmartMaster] Preset loaded: {filePath}");
                return loadedConfig;
            }
            catch (Exception ex)
            {
                Log.Error($"[SmartMaster] Preset load error: {ex.Message}");
                throw new InvalidOperationException($"Failed to load preset: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Loads the default preset if it exists.
        /// </summary>
        /// <returns>Default configuration, or null if not found.</returns>
        public SmartMasterConfig? LoadDefaultPreset()
        {
            string defaultPath = Path.Combine(_presetsDirectory, DEFAULT_PRESET_NAME);
            
            if (File.Exists(defaultPath))
            {
                try
                {
                    return Load("default");
                }
                catch (Exception ex)
                {
                    Log.Warning($"[SmartMaster] Failed to load default preset: {ex.Message}");
                    return null;
                }
            }
            
            Log.Info("[SmartMaster] No default preset found");
            return null;
        }
        
        /// <summary>
        /// Loads a speaker-specific factory preset.
        /// </summary>
        /// <param name="speakerType">Type of speaker system.</param>
        /// <returns>Speaker-specific configuration.</returns>
        public SmartMasterConfig LoadSpeakerPreset(SpeakerType speakerType)
        {
            string presetName = speakerType.ToString().ToLowerInvariant();
            
            try
            {
                return Load(presetName);
            }
            catch (Exception ex)
            {
                Log.Warning($"[SmartMaster] Failed to load speaker preset {speakerType}, using factory default: {ex.Message}");
                
                // Fallback: create from factory
                return SmartMasterPresetFactory.CreatePreset(speakerType);
            }
        }
        
        /// <summary>
        /// Internal save method with full file path.
        /// </summary>
        private void SaveInternal(SmartMasterConfig config, string filePath)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            
            string json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(filePath, json);
        }
    }
}
