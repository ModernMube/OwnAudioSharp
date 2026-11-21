using System;
using System.Collections.Generic;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Jumps the tracks carry out themselves, inside a block: a loop's way back, a skipped section,
/// a queued jump — armed ahead, sample for sample, with no seek and no hole.
/// </summary>
public partial class AudioMixer
{
    /// <summary>
    /// How far past an armed jump's moment the clock may run before it counts as missed.
    /// </summary>
    private const double JumpLateSeconds = 0.1;

    private readonly object _jumpLock = new object();
    private ulong _nextJumpId;
    private ulong _jumpId;
    private double _jumpAt, _jumpTo;
    private IRustClockedSource[] _jumpSources = Array.Empty<IRustClockedSource>();

    /// <summary>
    /// The armed jump happened on every track that took it, or its moment went by without it
    /// (<see cref="JumpTakenEventArgs.Late"/>). Raised from the mixer's control tick.
    /// </summary>
    public event EventHandler<JumpTakenEventArgs>? JumpTaken;

    /// <summary>
    /// When the project reaches atProjectSeconds, every track goes on from toProjectSeconds — inside
    /// the block, with a few milliseconds of crossfade. One at a time: arming again replaces it, a
    /// seek takes it back. Re-arm after a tempo change, the frames move with the tempo.
    /// </summary>
    /// <returns>
    /// True when every track carries it out itself. False when one of them cannot (a streamed file
    /// track, for now): the rest still jump, and the caller seeks there at the boundary.
    /// </returns>
    public bool ArmJump(double atProjectSeconds, double toProjectSeconds)
    {
        _throwIfDisposed();
        _throwUnlessRustNative(nameof(ArmJump));

        lock (_jumpLock)
        {
            _disarmJumpLocked();

            ulong _id = ++_nextJumpId;
            var _armed = new List<IRustClockedSource>();
            bool _every = true;

            lock (_rustSessionLock)
            {
                foreach (IAudioSource source in _sources.Values)
                {
                    IRustClockedSource? _fs = _resolve<IRustClockedSource>(source);
                    if (_fs is null) continue;

                    if (_fs.ArmRustNativeJump(_id, atProjectSeconds, toProjectSeconds)) _armed.Add(_fs);
                    else _every = false;
                }
            }

            _jumpId = _id;
            _jumpAt = atProjectSeconds;
            _jumpTo = toProjectSeconds;
            _jumpSources = _armed.ToArray();
            return _every;
        }
    }

    /// <summary>
    /// Takes back the armed jump, if it has not happened yet.
    /// </summary>
    public void DisarmJump()
    {
        lock (_jumpLock) { _disarmJumpLocked(); }
    }

    private void _disarmJumpLocked()
    {
        foreach (IRustClockedSource source in _jumpSources) source.DisarmRustNativeJump();
        _jumpSources = Array.Empty<IRustClockedSource>();
        _jumpId = 0;
    }

    /// <summary>
    /// Control tick: has every armed track taken it, or has its moment passed?
    /// </summary>
    private void _pollJump()
    {
        JumpTakenEventArgs? _done = null;

        lock (_jumpLock)
        {
            if (_jumpId == 0) return;

            bool _taken = _jumpSources.Length > 0;
            foreach (IRustClockedSource source in _jumpSources)
                if (source.RustNativeJumpTaken != _jumpId) { _taken = false; break; }

            bool _late = !_taken && _masterClock.CurrentTimestamp > _jumpAt + JumpLateSeconds;
            if (!_taken && !_late) return;

            _done = new JumpTakenEventArgs(_jumpId, _jumpAt, _jumpTo, _late);
            if (_late) _disarmJumpLocked();
            else
            {
                _jumpSources = Array.Empty<IRustClockedSource>();
                _jumpId = 0;
            }
        }

        JumpTaken?.Invoke(this, _done);
    }
}
