namespace SoundTouch
{
    internal readonly struct Beat
    {
        public Beat(float pos, float strength)
        {
            Position = pos;
            Strength = strength;
        }

        public float Position { get; }

        public float Strength { get; }
    }
}
