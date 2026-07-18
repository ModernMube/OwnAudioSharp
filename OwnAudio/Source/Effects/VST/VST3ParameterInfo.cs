namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// One VST3 param plus its metadata.
    /// </summary>
    public sealed class VST3ParameterInfo
    {
        /// <summary>
        /// Param ID for Get/Set.
        /// </summary>
        public uint Id { get; }

        /// <summary>
        /// Display name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Current value, normalized 0..1 in most plugins.
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// Low end of the native range.
        /// </summary>
        public double MinValue { get; }

        /// <summary>
        /// High end of the native range.
        /// </summary>
        public double MaxValue { get; }

        /// <summary>
        /// What the plugin ships with.
        /// </summary>
        public double DefaultValue { get; }

        /// <summary>
        /// Builds the info record. Ranges default to the usual normalized 0..1.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="name"></param>
        public VST3ParameterInfo(uint id, string name, double value,
            double minValue = 0.0, double maxValue = 1.0, double defaultValue = 0.0)
        {
            Id = id;
            Name = name ?? string.Empty;
            Value = value;
            MinValue = minValue;
            MaxValue = maxValue;
            DefaultValue = defaultValue;
        }

        /// <summary>
        /// Diagnostics only.
        /// </summary>
        public override string ToString() => $"{Name}: {Value:F3} (range: {MinValue:F3} - {MaxValue:F3})";
    }
}
