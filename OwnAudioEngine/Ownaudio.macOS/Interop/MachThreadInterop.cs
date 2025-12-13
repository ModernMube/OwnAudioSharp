using System;
using System.Runtime.InteropServices;

namespace Ownaudio.macOS.Interop
{
    /// <summary>
    /// macOS MACH kernel thread API for real-time audio thread scheduling.
    /// Provides true real-time priority via the MACH thread_policy API.
    ///
    /// This is CRITICAL for professional audio on macOS - without this,
    /// threads use standard BSD priority which is not suitable for real-time audio.
    ///
    /// Apple's Audio Thread Programming Guide recommends using MACH APIs for audio threads:
    /// https://developer.apple.com/library/archive/technotes/tn2169/_index.html
    /// </summary>
    internal static class MachThreadInterop
    {
        private const string LibSystem = "/usr/lib/libSystem.dylib";

        // MACH thread policy types
        public const int THREAD_TIME_CONSTRAINT_POLICY = 2;
        public const int THREAD_PRECEDENCE_POLICY = 3;

        // Thread policy flavor count
        public const int THREAD_TIME_CONSTRAINT_POLICY_COUNT = 4;
        public const int THREAD_PRECEDENCE_POLICY_COUNT = 1;

        /// <summary>
        /// Time constraint policy for real-time threads.
        /// This policy ensures the thread runs within specific timing constraints,
        /// which is essential for audio processing.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct thread_time_constraint_policy
        {
            /// <summary>
            /// Period between thread invocations (in Mach absolute time units).
            /// For audio: buffer_size_in_frames / sample_rate
            /// </summary>
            public uint period;

            /// <summary>
            /// Maximum computation time per period (in Mach absolute time units).
            /// Should be ~80% of period to allow for scheduling overhead.
            /// </summary>
            public uint computation;

            /// <summary>
            /// Maximum constraint time (deadline) for completion (in Mach absolute time units).
            /// Typically set to period for strict real-time constraints.
            /// </summary>
            public uint constraint;

            /// <summary>
            /// Whether the thread can preempt other threads (1 = yes, 0 = no).
            /// Set to 1 for audio threads to ensure they can interrupt lower-priority work.
            /// </summary>
            public int preemptible;
        }

        /// <summary>
        /// Thread precedence policy for relative priority.
        /// Lower values = higher priority.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct thread_precedence_policy
        {
            /// <summary>
            /// Importance value (0 is default, negative is higher priority).
            /// Valid range: -128 to +127
            /// For audio: use -47 (high priority without being kernel-critical)
            /// </summary>
            public int importance;
        }

        /// <summary>
        /// Gets the Mach thread port for the current thread.
        /// This is required to set thread policies.
        /// </summary>
        /// <returns>Mach port for current thread.</returns>
        [DllImport(LibSystem, EntryPoint = "mach_thread_self")]
        public static extern uint mach_thread_self();

        /// <summary>
        /// Sets the scheduling policy for a thread.
        /// </summary>
        /// <param name="thread">Mach thread port (from mach_thread_self()).</param>
        /// <param name="flavor">Policy type (THREAD_TIME_CONSTRAINT_POLICY or THREAD_PRECEDENCE_POLICY).</param>
        /// <param name="policy_info">Pointer to policy structure.</param>
        /// <param name="count">Size of policy structure in integers.</param>
        /// <returns>0 on success, non-zero error code on failure.</returns>
        [DllImport(LibSystem, EntryPoint = "thread_policy_set")]
        public static extern int thread_policy_set(
            uint thread,
            int flavor,
            IntPtr policy_info,
            uint count);

        /// <summary>
        /// Gets the timebase information for converting between Mach absolute time and nanoseconds.
        /// Required for setting time constraint policy values.
        /// </summary>
        /// <param name="info">Output: timebase information structure.</param>
        /// <returns>0 on success.</returns>
        [DllImport(LibSystem, EntryPoint = "mach_timebase_info")]
        private static extern int mach_timebase_info(out mach_timebase_info_data_t info);

        [StructLayout(LayoutKind.Sequential)]
        private struct mach_timebase_info_data_t
        {
            public uint numer;
            public uint denom;
        }

        private static mach_timebase_info_data_t? _timebaseInfo;

        /// <summary>
        /// Converts nanoseconds to Mach absolute time units.
        /// Caches the timebase info for efficiency.
        /// </summary>
        /// <param name="nanoseconds">Time in nanoseconds.</param>
        /// <returns>Time in Mach absolute time units.</returns>
        public static uint NanosecondsToAbsoluteTime(ulong nanoseconds)
        {
            if (!_timebaseInfo.HasValue)
            {
                int result = mach_timebase_info(out var timebaseInfo);
                if (result != 0)
                {
                    // Fallback: assume 1:1 ratio (may not be accurate on all systems)
                    return (uint)nanoseconds;
                }
                _timebaseInfo = timebaseInfo;
            }

            var cachedInfo = _timebaseInfo.Value;
            // Convert: absolute_time = nanoseconds * (denom / numer)
            return (uint)((nanoseconds * cachedInfo.denom) / cachedInfo.numer);
        }

        /// <summary>
        /// Sets real-time priority for the current thread using MACH time constraint policy.
        /// This is the CORRECT way to create real-time audio threads on macOS.
        ///
        /// Apple's Core Audio uses this internally for all audio I/O callbacks.
        /// </summary>
        /// <param name="periodNs">Period between thread runs in nanoseconds (e.g., buffer duration).</param>
        /// <param name="computationNs">Maximum computation time per period in nanoseconds (typically 80% of period).</param>
        /// <param name="constraintNs">Deadline constraint in nanoseconds (typically same as period).</param>
        /// <param name="preemptible">Whether thread can preempt others (true for audio).</param>
        /// <returns>True on success, false on failure.</returns>
        public static bool SetThreadToRealTimePriority(
            ulong periodNs,
            ulong computationNs,
            ulong constraintNs,
            bool preemptible = true)
        {
            try
            {
                // Get current thread's Mach port
                uint thread = mach_thread_self();

                // Convert times to Mach absolute time units
                var policy = new thread_time_constraint_policy
                {
                    period = NanosecondsToAbsoluteTime(periodNs),
                    computation = NanosecondsToAbsoluteTime(computationNs),
                    constraint = NanosecondsToAbsoluteTime(constraintNs),
                    preemptible = preemptible ? 1 : 0
                };

                // Set time constraint policy
                unsafe
                {
                    int result = thread_policy_set(
                        thread,
                        THREAD_TIME_CONSTRAINT_POLICY,
                        new IntPtr(&policy),
                        THREAD_TIME_CONSTRAINT_POLICY_COUNT);

                    if (result != 0)
                    {
                        return false;
                    }
                }

                // Also set high precedence (importance)
                var precedence = new thread_precedence_policy
                {
                    importance = -47 // High priority for audio (Apple uses -47 to -63)
                };

                unsafe
                {
                    int result = thread_policy_set(
                        thread,
                        THREAD_PRECEDENCE_POLICY,
                        new IntPtr(&precedence),
                        THREAD_PRECEDENCE_POLICY_COUNT);

                    // Precedence is less critical, so we don't fail if it doesn't work
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sets real-time priority for an audio thread with standard audio parameters.
        /// Calculates timing constraints based on sample rate and buffer size.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate (e.g., 48000).</param>
        /// <param name="bufferSizeFrames">Buffer size in frames (e.g., 512).</param>
        /// <returns>True on success, false on failure.</returns>
        public static bool SetThreadToAudioRealTimePriority(int sampleRate, int bufferSizeFrames)
        {
            // Calculate period (time per buffer)
            // period_seconds = bufferSizeFrames / sampleRate
            // period_ns = (bufferSizeFrames / sampleRate) * 1_000_000_000
            ulong periodNs = (ulong)bufferSizeFrames * 1_000_000_000UL / (ulong)sampleRate;

            // Computation time: allow 80% of period for processing
            ulong computationNs = (periodNs * 80) / 100;

            // Constraint (deadline): same as period for strict real-time
            ulong constraintNs = periodNs;

            return SetThreadToRealTimePriority(
                periodNs,
                computationNs,
                constraintNs,
                preemptible: true);
        }
    }
}
