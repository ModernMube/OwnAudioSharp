namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// Represents a VST3 plugin parameter with metadata.
    /// </summary>
    public sealed class VST3ParameterInfo
    {
        /// <summary>
        /// Parameter ID used for Get/Set operations.
        /// </summary>
        public uint Id { get; }

        /// <summary>
        /// Display name of the parameter.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Current normalized value (typically 0.0 to 1.0).
        /// </summary>
        public double Value { get; set; }

        /// <summary>
        /// Minimum value in parameter's native range.
        /// </summary>
        public double MinValue { get; }

        /// <summary>
        /// Maximum value in parameter's native range.
        /// </summary>
        public double MaxValue { get; }

        /// <summary>
        /// Default value of the parameter.
        /// </summary>
        public double DefaultValue { get; }

        /// <summary>
        /// Creates a new VST3 parameter info instance.
        /// </summary>
        /// <param name="id">Parameter ID.</param>
        /// <param name="name">Parameter display name.</param>
        /// <param name="value">Current value.</param>
        /// <param name="minValue">Minimum value.</param>
        /// <param name="maxValue">Maximum value.</param>
        /// <param name="defaultValue">Default value.</param>
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
        /// Returns a string representation of the parameter.
        /// </summary>
        public override string ToString()
        {
            return $"{Name}: {Value:F3} (range: {MinValue:F3} - {MaxValue:F3})";
        }
    }
}
