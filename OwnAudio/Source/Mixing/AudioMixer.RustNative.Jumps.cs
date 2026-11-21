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
    /// The armed jump's frame on each armed source's own timeline, what its lateness is told by.
    /// </summary>
    private double[] _jumpOwnAt = Array.Empty<double>();

    /// <summary>
    /// The armed jump happened on every track that took it, or its moment went by without it
    /// (<see cref="JumpTakenEventArgs.Late"/>). Raised from the mixer's control tick.
    /// </summary>
    public event EventHandler<JumpTakenEventArgs>? JumpTaken;

    /// <summary>
    /// When the song reaches atSongSeconds, every track goes on from toSongSeconds — inside the
    /// block, with a few milliseconds of crossfade. Song seconds are content seconds, the markers'
    /// own: they stay put under a tempo change, which project seconds do not. One at a time:
    /// arming again replaces it, a seek takes it back.
    /// </summary>
    /// <returns>
    /// True when every track carries it out itself. False when one of them cannot (its target lies
    /// beyond the file, say): the rest still jump, and the caller seeks there at the boundary. A
    /// file track opens a second stream of its file on the first jump armed, so arm ahead.
    /// </returns>
    public bool ArmJump(double atSongSeconds, double toSongSeconds)
    {
        _throwIfDisposed();
        _throwUnlessRustNative(nameof(ArmJump));

        lock (_jumpLock)
        {
            _disarmJumpLocked();

            ulong _id = ++_nextJumpId;
            var _armed = new List<IRustClockedSource>();
            var _ownAt = new List<double>();
            bool _every = true;

            lock (_rustSessionLock)
            {
                foreach (IAudioSource source in _sources.Values)
                {
                    IRustClockedSource? _fs = _resolve<IRustClockedSource>(source);
                    if (_fs is null) continue;

                    if (_fs.ArmRustNativeJump(_id, atSongSeconds, toSongSeconds) is { } _own)
                    {
                        _armed.Add(_fs);
                        _ownAt.Add(_own);
                    }
                    else _every = false;
                }
            }

            if (_armed.Count == 0) return false;

            _jumpId = _id;
            _jumpAt = atSongSeconds;
            _jumpTo = toSongSeconds;
            _jumpOwnAt = _ownAt.ToArray();
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
        _jumpOwnAt = Array.Empty<double>();
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

            // a source that has not taken it yet is late once its own, unjumped position is well past
            // its frame — the others may well be past it already: the stretch stages read ahead by
            // different amounts, so the tracks take a jump a block or two apart
            bool _taken = true, _late = false;
            for (int i = 0; i < _jumpSources.Length; i++)
            {
                IRustClockedSource source = _jumpSources[i];
                if (source.RustNativeJumpMissed == _jumpId) _late = true;
                else if (source.RustNativeJumpTaken != _jumpId)
                {
                    _taken = false;
                    if (source.RustNativeContentPosition > _jumpOwnAt[i] + JumpLateSeconds) _late = true;
                }
            }
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
