# OwnAudio.Midi

AOT-compatible, reflection-free MIDI library for Windows, macOS, and Linux.  
Part of the [OwnAudioSharp](https://github.com/modernmube/OwnAudioSharp) ecosystem.

## Features

- **MIDI I/O** — real-time input/output via platform-native APIs (WinMM / CoreMIDI / ALSA rawmidi)
- **MIDI File** — read, edit, and write Standard MIDI Files (SMF format 0 and 1)
- **MIDI Clock** — hardware-accurate 24 PPQN clock with `ThreadPriority.Highest`
- **Native AOT ready** — `IsAotCompatible=true`, `IsTrimmable=true`, zero reflection
- **Zero managed dependencies** — no third-party packages; pure P/Invoke

## Installation

```xml
<ProjectReference Include="OwnAudio.Midi/OwnAudio.Midi.csproj" />
```

Or (once published to NuGet):

```xml
<PackageReference Include="OwnAudioSharp.Midi" Version="3.0.0" />
```

---

## Namespaces

| Namespace | Contents |
|---|---|
| `OwnAudio.Midi.IO` | `MidiMessage`, `IMidiInputPort`, `IMidiOutputPort`, `MidiPortFactory` |
| `OwnAudio.Midi.File` | `MidiFile`, `MidiTrack`, `MidiEvent`, `MidiFileReader`, `MidiFileWriter` |
| `OwnAudio.Midi.Clock` | `MidiClock` |

---

## 1. Port Selection

### List available ports

```csharp
using OwnAudio.Midi.IO;

// List all input devices
IReadOnlyList<string> inputs = MidiPortFactory.GetInputPortNames();
foreach (var name in inputs)
    Console.WriteLine($"[IN]  {name}");

// List all output devices
IReadOnlyList<string> outputs = MidiPortFactory.GetOutputPortNames();
foreach (var name in outputs)
    Console.WriteLine($"[OUT] {name}");
```

### Open a port by name

```csharp
// Open the first available input port
using IMidiInputPort input = MidiPortFactory.OpenInput(inputs[0]);

// Open a specific output port by name
using IMidiOutputPort output = MidiPortFactory.OpenOutput("IAC Driver Bus 1");
```

> **Platform notes**
> - **Windows**: ports are named by WinMM (e.g. `"Microsoft GS Wavetable Synth"`)
> - **macOS**: CoreMIDI names (e.g. `"IAC Driver Bus 1"`, `"USB MIDI Interface"`)
> - **Linux**: ALSA rawmidi device paths (e.g. `"/dev/midi1"`, `"/dev/snd/midiC1D0"`)

---

## 2. Receiving MIDI Data

Subscribe to `MessageReceived` **before** calling `Start()`.  
The callback fires on a background thread — marshal to your UI thread if needed.

```csharp
using OwnAudio.Midi.IO;

using IMidiInputPort input = MidiPortFactory.OpenInput("USB MIDI Interface");

input.MessageReceived += OnMidiMessage;
input.Start();

Console.WriteLine("Listening... press Enter to stop.");
Console.ReadLine();

input.Stop();
input.MessageReceived -= OnMidiMessage;

// ─── Handler ───────────────────────────────────────────────
void OnMidiMessage(MidiMessage msg)
{
    if (msg.IsNoteOn)
        Console.WriteLine($"Note ON  — pitch={msg.Data1} velocity={msg.Data2} ch={msg.Channel}");
    else if (msg.IsNoteOff)
        Console.WriteLine($"Note OFF — pitch={msg.Data1} ch={msg.Channel}");
    else if (msg.IsControlChange)
        Console.WriteLine($"CC #{msg.Data1} = {msg.Data2}  ch={msg.Channel}");
    else if (msg.IsPitchBend)
    {
        int bend = (msg.Data2 << 7) | msg.Data1; // 0..16383, centre = 8192
        Console.WriteLine($"Pitch Bend = {bend}  ch={msg.Channel}");
    }
}
```

### MidiMessage properties

| Property | Type | Description |
|---|---|---|
| `Status` | `byte` | Raw status byte |
| `Data1` | `byte` | First data byte (note number, CC number, …) |
| `Data2` | `byte` | Second data byte (velocity, CC value, …) |
| `Timestamp` | `long` | Nanoseconds since port open (platform-dependent) |
| `Type` | `MidiMessageType` | Enum: `NoteOn`, `NoteOff`, `ControlChange`, … |
| `Channel` | `int` | MIDI channel 0–15 |
| `IsNoteOn` | `bool` | `true` if NoteOn with velocity > 0 |
| `IsNoteOff` | `bool` | `true` if NoteOff, or NoteOn with velocity = 0 |
| `IsControlChange` | `bool` | CC message |
| `IsPitchBend` | `bool` | Pitch bend message |

---

## 3. Sending MIDI Data

```csharp
using OwnAudio.Midi.IO;

using IMidiOutputPort output = MidiPortFactory.OpenOutput("USB MIDI Interface");

// ─── Note On / Note Off ────────────────────────────────────
// MidiMessage(status, data1, data2)
// status = 0x90 | channel  (NoteOn, channel 0)
output.Send(new MidiMessage(0x90, 60, 100)); // Middle C, velocity 100
await Task.Delay(500);
output.Send(new MidiMessage(0x80, 60, 0));   // Note Off

// ─── Control Change (CC) ──────────────────────────────────
// status = 0xB0 | channel, data1 = CC number, data2 = value
output.Send(new MidiMessage(0xB0, 7, 100));  // Volume (CC #7) = 100

// ─── Program Change ───────────────────────────────────────
// status = 0xC0 | channel, data1 = program number
output.Send(new MidiMessage(0xC0, 0, 0));    // Program 0 (Grand Piano)

// ─── Pitch Bend ───────────────────────────────────────────
// 14-bit value split across Data1 (LSB) and Data2 (MSB)
int bend = 10000; // range 0..16383, centre = 8192
output.Send(new MidiMessage(0xE0, (byte)(bend & 0x7F), (byte)(bend >> 7)));

// ─── SysEx ────────────────────────────────────────────────
ReadOnlySpan<byte> sysex = [0xF0, 0x41, 0x10, 0x42, 0x12, 0xF7];
output.SendSysEx(sysex);
```

### Common status bytes

| Message | Status byte formula | Data1 | Data2 |
|---|---|---|---|
| Note Off | `0x80 \| ch` | note (0–127) | velocity |
| Note On | `0x90 \| ch` | note (0–127) | velocity |
| Aftertouch | `0xA0 \| ch` | note | pressure |
| Control Change | `0xB0 \| ch` | CC number | value |
| Program Change | `0xC0 \| ch` | program | — |
| Channel Pressure | `0xD0 \| ch` | pressure | — |
| Pitch Bend | `0xE0 \| ch` | LSB (7-bit) | MSB (7-bit) |

---

## 4. MIDI File — Reading

`MidiFileReader` parses Standard MIDI Files (`.mid`) with no external dependencies.

```csharp
using OwnAudio.Midi.File;

MidiFile file = MidiFileReader.Read("song.mid");

Console.WriteLine($"Format:        {file.Format}");         // 0, 1, or 2
Console.WriteLine($"Ticks/beat:    {file.TicksPerBeat}");
Console.WriteLine($"Track count:   {file.Tracks.Count}");

foreach (MidiTrack track in file.Tracks)
{
    long absoluteTick = 0;

    foreach (MidiEvent evt in track.Events)
    {
        absoluteTick += evt.DeltaTime;

        switch (evt.Type)
        {
            case MidiEventType.Midi:
                byte msgType = (byte)(evt.Status & 0xF0);
                int  channel = evt.Status & 0x0F;

                if (msgType == 0x90 && evt.Data2 > 0)
                    Console.WriteLine($"  t={absoluteTick,8}  NoteOn  ch={channel} note={evt.Data1} vel={evt.Data2}");
                else if (msgType == 0x80 || (msgType == 0x90 && evt.Data2 == 0))
                    Console.WriteLine($"  t={absoluteTick,8}  NoteOff ch={channel} note={evt.Data1}");
                break;

            case MidiEventType.Meta:
                if (evt.IsTempoChange)
                {
                    int us = evt.GetTempoMicroseconds();
                    double bpm = 60_000_000.0 / us;
                    Console.WriteLine($"  t={absoluteTick,8}  Tempo   {bpm:F1} BPM");
                }
                else if (evt.IsEndOfTrack)
                    Console.WriteLine($"  t={absoluteTick,8}  End of Track");
                break;

            case MidiEventType.SysEx:
                Console.WriteLine($"  t={absoluteTick,8}  SysEx   {evt.MetaData?.Length ?? 0} bytes");
                break;
        }
    }
}
```

### Convert ticks to seconds

Ticks are tempo-dependent. Use `GetTempoMicroseconds()` from the last tempo event:

```csharp
int tempoUs = 500_000; // default: 120 BPM

double TicksToSeconds(long ticks) =>
    ticks * tempoUs / (1_000_000.0 * file.TicksPerBeat);
```

---

## 5. MIDI File — Editing

`MidiFile`, `MidiTrack`, and `MidiEvent` are immutable by design.  
To edit, rebuild the event list from the existing one:

```csharp
using OwnAudio.Midi.File;

MidiFile original = MidiFileReader.Read("song.mid");

// Transpose all notes up by 2 semitones on track 0
var editedEvents = new List<MidiEvent>();

foreach (MidiEvent evt in original.Tracks[0].Events)
{
    if (evt.Type == MidiEventType.Midi)
    {
        byte msgType = (byte)(evt.Status & 0xF0);
        bool isNote  = msgType == 0x90 || msgType == 0x80;

        if (isNote)
        {
            byte newNote = (byte)Math.Clamp(evt.Data1 + 2, 0, 127);
            editedEvents.Add(new MidiEvent(evt.DeltaTime, evt.Status, newNote, evt.Data2));
            continue;
        }
    }
    editedEvents.Add(evt); // keep everything else unchanged
}

var editedFile = new MidiFile(
    original.Format,
    original.TicksPerBeat,
    [new MidiTrack(editedEvents)]
);

MidiFileWriter.Write(editedFile, "song_transposed.mid");
```

### Change tempo

```csharp
var events = new List<MidiEvent>();

// Insert a new tempo event at the beginning (120 BPM → 140 BPM)
int newTempoUs = (int)(60_000_000.0 / 140);
byte[] tempoBytes = [(byte)(newTempoUs >> 16), (byte)(newTempoUs >> 8), (byte)newTempoUs];

events.Add(new MidiEvent(0, 0x51, tempoBytes));  // delta=0, meta type 0x51

// Copy the rest of the track, skipping the original tempo event
foreach (var evt in original.Tracks[0].Events)
{
    if (evt.IsTempoChange) continue;
    events.Add(evt);
}

var modified = new MidiFile(original.Format, original.TicksPerBeat,
    [new MidiTrack(events)]);
MidiFileWriter.Write(modified, "song_140bpm.mid");
```

---

## 6. MIDI File — Writing from scratch

```csharp
using OwnAudio.Midi.File;

const ushort TicksPerBeat = 480;
const int Bpm = 120;
int tempoUs = 60_000_000 / Bpm; // 500_000 μs

var events = new List<MidiEvent>();

// Tempo meta event (type 0x51, 3 bytes)
byte[] tempoBytes = [(byte)(tempoUs >> 16), (byte)(tempoUs >> 8), (byte)tempoUs];
events.Add(new MidiEvent(0, 0x51, tempoBytes));

// Program Change — channel 0, piano
events.Add(new MidiEvent(0, 0xC0, 0, 0));

// Helper: ticks from beat position
long T(double beats) => (long)(beats * TicksPerBeat);

// Build a C major scale: C D E F G A B C
int[] scale = [60, 62, 64, 65, 67, 69, 71, 72];
long cursor = 0;

for (int i = 0; i < scale.Length; i++)
{
    long noteOnTick  = T(i);
    long noteOffTick = T(i + 0.9);

    int deltaOn  = (int)(noteOnTick  - cursor); cursor = noteOnTick;
    int deltaOff = (int)(noteOffTick - cursor); cursor = noteOffTick;

    events.Add(new MidiEvent(deltaOn,  0x90, (byte)scale[i], 80)); // Note On
    events.Add(new MidiEvent(deltaOff, 0x80, (byte)scale[i],  0)); // Note Off
}

// End of Track is appended automatically by MidiFileWriter if missing
var midiFile = new MidiFile(0, TicksPerBeat, [new MidiTrack(events)]);
MidiFileWriter.Write(midiFile, "c_major_scale.mid");
```

---

## 7. MIDI Clock

`MidiClock` sends standard MIDI Timing Clock messages (0xF8) at 24 pulses per quarter note.  
The clock thread runs at `ThreadPriority.Highest` using a spin-wait loop for microsecond accuracy.

```csharp
using OwnAudio.Midi.Clock;
using OwnAudio.Midi.IO;

// Clock without output port — internal use only (e.g. driving a sequencer)
using var clock = new MidiClock(bpm: 120.0);
clock.Start();

await Task.Delay(4000); // run for 4 seconds

clock.Bpm = 140.0;      // change tempo while running

await Task.Delay(4000);
clock.Stop();
```

### Clock with a hardware output port

```csharp
using IMidiOutputPort output = MidiPortFactory.OpenOutput("USB MIDI Interface");
using var clock = new MidiClock(bpm: 120.0, outputPort: output);

clock.Start();   // sends 0xFA (MIDI Start) then 0xF8 clock pulses
// ...
clock.Stop();    // sends 0xFC (MIDI Stop)
clock.Continue();// sends 0xFB (MIDI Continue) and resumes clock
```

### Clock messages sent automatically

| Event | MIDI message | Byte |
|---|---|---|
| `Start()` | MIDI Start | `0xFA` |
| `Stop()` | MIDI Stop | `0xFC` |
| `Continue()` | MIDI Continue | `0xFB` |
| Every pulse | MIDI Timing Clock | `0xF8` |

---

## 8. Full example — MIDI keyboard to file recorder

```csharp
using OwnAudio.Midi.IO;
using OwnAudio.Midi.File;

var recordedEvents = new List<(long absoluteMs, MidiEvent evt)>();
var startTime = DateTimeOffset.UtcNow;
const ushort TicksPerBeat = 480;
const int Bpm = 120;
int tempoUs = 60_000_000 / Bpm;
double ticksPerMs = TicksPerBeat * Bpm / 60_000.0;

using IMidiInputPort input = MidiPortFactory.OpenInput(
    MidiPortFactory.GetInputPortNames()[0]);

input.MessageReceived += msg =>
{
    long ms = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
    long tick = (long)(ms * ticksPerMs);
    recordedEvents.Add((tick, new MidiEvent(0, msg.Status, msg.Data1, msg.Data2)));
};

input.Start();
Console.WriteLine("Recording... press Enter to stop and save.");
Console.ReadLine();
input.Stop();

// Convert absolute ticks to delta times
recordedEvents.Sort((a, b) => a.absoluteMs.CompareTo(b.absoluteMs));
var midiEvents = new List<MidiEvent>();

byte[] tempoBytes = [(byte)(tempoUs >> 16), (byte)(tempoUs >> 8), (byte)tempoUs];
midiEvents.Add(new MidiEvent(0, 0x51, tempoBytes));

long prev = 0;
foreach (var (tick, evt) in recordedEvents)
{
    int delta = (int)(tick - prev);
    prev = tick;
    midiEvents.Add(new MidiEvent(delta, evt.Status, evt.Data1, evt.Data2));
}

var midiFile = new MidiFile(0, TicksPerBeat, [new MidiTrack(midiEvents)]);
MidiFileWriter.Write(midiFile, "recording.mid");
Console.WriteLine("Saved: recording.mid");
```

---

## Platform support

| Platform | I/O backend | File R/W | Clock |
|---|---|---|---|
| Windows | WinMM (`winmm.dll`) | ✅ | ✅ |
| macOS | CoreMIDI framework | ✅ | ✅ |
| Linux | ALSA rawmidi (`libasound`) | ✅ | ✅ |

All platform code is selected at **compile time** via `#if WINDOWS / MACOS / LINUX` — no runtime reflection.

## AOT / Trimming

The library is fully compatible with .NET Native AOT and trimming:

- All P/Invoke uses `[LibraryImport]` (source-generated, AOT-safe)
- No `Assembly.Load`, `Activator.CreateInstance`, or `GetType()` calls
- `IsAotCompatible=true` and `IsTrimmable=true` are set in the `.csproj`
- `0 IL2026 / IL2072 / IL3050` warnings on `dotnet publish --self-contained`
