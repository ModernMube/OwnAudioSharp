using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Windows.Interop
{
    /// <summary>
    /// kernel32.dll P/Invoke definitions for event handling and timing.
    /// </summary>
    internal static class Kernel32
    {
        // Event creation and management
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEvent(
            IntPtr lpEventAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
            [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
            string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        // Wait functions
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 0x00000102;
        public const uint WAIT_FAILED = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForMultipleObjects(
            uint nCount,
            IntPtr[] lpHandles,
            [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
            uint dwMilliseconds);

        // High-resolution timing
        [DllImport("kernel32.dll")]
        public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

        [DllImport("kernel32.dll")]
        public static extern bool QueryPerformanceFrequency(out long lpFrequency);

        // Thread priority
        public enum ThreadPriorityLevel
        {
            THREAD_PRIORITY_IDLE = -15,
            THREAD_PRIORITY_LOWEST = -2,
            THREAD_PRIORITY_BELOW_NORMAL = -1,
            THREAD_PRIORITY_NORMAL = 0,
            THREAD_PRIORITY_ABOVE_NORMAL = 1,
            THREAD_PRIORITY_HIGHEST = 2,
            THREAD_PRIORITY_TIME_CRITICAL = 15
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetThreadPriority(IntPtr hThread, ThreadPriorityLevel nPriority);

        // Memory functions
        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
        public static extern void ZeroMemory(IntPtr dest, IntPtr size);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, IntPtr count);

        // MMCSS (Multimedia Class Scheduler Service) functions for real-time thread priority
        // These functions provide guaranteed CPU resources for multimedia applications

        /// <summary>
        /// Associates the calling thread with the specified task name and gives it a high priority for real-time processing.
        /// Commonly used task names: "Pro Audio", "Audio", "Playback", "Games", "Capture", "Distribution"
        /// </summary>
        /// <param name="taskName">The name of the task (e.g., "Pro Audio" for WASAPI audio threads)</param>
        /// <param name="taskIndex">Task index (output parameter, can be 0)</param>
        /// <returns>Handle to the MMCSS task, or IntPtr.Zero on failure</returns>
        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        /// <summary>
        /// Removes the calling thread from the MMCSS task.
        /// Should be called when the thread exits or no longer needs real-time priority.
        /// </summary>
        /// <param name="avrtHandle">Handle returned by AvSetMmThreadCharacteristics</param>
        /// <returns>True if successful, false otherwise</returns>
        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

        /// <summary>
        /// Sets the maximum thread priority within the MMCSS task.
        /// </summary>
        /// <param name="avrtHandle">Handle returned by AvSetMmThreadCharacteristics</param>
        /// <param name="priority">Priority value (typically "AVRT_PRIORITY_CRITICAL" or "AVRT_PRIORITY_HIGH")</param>
        /// <returns>True if successful, false otherwise</returns>
        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, AvrtPriority priority);

        /// <summary>
        /// MMCSS thread priority levels
        /// </summary>
        public enum AvrtPriority
        {
            AVRT_PRIORITY_VERYLOW = -2,
            AVRT_PRIORITY_LOW = -1,
            AVRT_PRIORITY_NORMAL = 0,
            AVRT_PRIORITY_HIGH = 1,
            AVRT_PRIORITY_CRITICAL = 2
        }
    }
}