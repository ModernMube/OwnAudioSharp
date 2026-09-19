# DSP és effect kezelés elemzése

**Projekt:** OwnAudioSharp
**Dátum:** 2026-09-04
**Hatáskör:** managed C# API (`OwnaudioNET.Effects`, mixer, sources), HighLevel native wrapper (`Ownaudio.Audio.Effects`), Rust DSP (`ownaudio-core`)
**Kód nem változott** — elemzés és a választott javítási terv.

Alapszabály, amit a vizsgálat kiindulópontnak vett:

> A menedzselt oldalon nem történhet audio adat feldolgozás. A C# oldal paramétert, életciklust és tükrözést kezel; a minták a natív motorban mennek keresztül.

A visszafelé kompatibilitás kényszere miatt a DSP és az effect API két rétegen él: a nyilvános `IEffectProcessor` (benne a teljes C# DSP) és a natív Rust effect (HighLevel wrapper + FFI). Ez a kettősség magyarázza a káoszt.

---

## 1. Vezetői összefoglaló

A **valósidejű lejátszási út** (mixer → native `MultiTrackSession` → cpal) **nem dolgozza fel a mintákat C#-ban**. A 4.0 óta a `RustNativeChain` mindig be van kapcsolva, a managed mix thread megszűnt. Az effectek a mixerben paramétertükörként viselkednek: a control tick (~15 ms) átküldi az értékeket, a DSP a Rust oldalon fut.

Ez **nem jelenti**, hogy a C# oldalon ne lenne audio-feldolgozás.

| Út | Hol fut a DSP? | Gyakorlat |
|---|---|---|
| Mixer lejátszás, master/track effect | Rust | Alapszabály **teljesül** |
| VST3 a mixerben | Native `VST3Plugin_ProcessAudio` (Rust hívja) | Alapszabály **teljesül** |
| `IEffectProcessor.Process()` közvetlen hívás | C# | Alapszabály **sérül** |
| `SourceWithEffects.ReadSamples()` | C# effect lánc | Alapszabály **sérül** (lejátszáskor nem hívódik, API-kompatibilitás miatt él) |
| Matchering offline render | C# `Process()` | Alapszabály **sérül** — produkciós feature |
| DSP tesztcsomag | C# `Process()` | Szándékos, de a natív utat **nem** méri |
| SmartMaster mérés, Matchering FFT, chord detect, spectrum tap | C# (offline / másolat) | Hideg út; nem a realtime bus |

A legnagyobb kockázat nem az, hogy a mixer C#-ban kever. Hanem az, hogy **két teljes DSP-implementáció él egymás mellett**, ugyanazokkal az osztálynevekkel, eltérő paraméternevekkel, és a dokumentáció / a `Process()` szerződés még mindig azt sugallja, hogy a hang a managed oldalon születik.

**Választott javítás (effect út):** mindkét hívási út megmarad (`IEffectProcessor.Process` és a mixer natív twin), de **mindkettő a natív motort** futtatja. A paraméterlisták egyik oldalon sem bővülnek és nem szűkülnek — lásd [7. Javítási terv](#7-javítási-terv).

---

## 2. Jelenlegi architektúra

```
Nyilvános API (kompatibilitás)
  OwnaudioNET.Effects.*  : IEffectProcessor
      │  teljes C# DSP a Process()-ben
      │  a mixer NEM hívja Process()-t
      ▼
  RustEffectAdapters.Mirror()   control tick, ~15 ms
      │  paraméter id + float
      ▼
Natív twin
  Ownaudio.Audio.Effects.*  : IDisposable wrapper (nincs Process)
      │  ownaudio_v1_effect_set_param
      ▼
  ownaudio-core (Rust)      : a tényleges DSP
```

Két párhuzamos effect-világ:

| Réteg | Névtér | Szerződés | DSP |
|---|---|---|---|
| Nyilvános, kompatibilis | `OwnaudioNET.Effects` | `IEffectProcessor` (`Process`, `Enabled`, `Mix`) | Igen, C#-ban |
| HighLevel / natív | `Ownaudio.Audio.Effects` | `IsEnabled`, `EffectType`, nincs `Process` | Nincs — csak setter → FFI |
| Motor | `ownaudio-core` | `Effect` trait, param id | Rust |

A híd: `OwnAudio/Source/Mixing/RustEffectAdapters.cs`. Típus alapján párosít, és a managed objektumot **paramétermmodellként** kezeli. A fájl kommentje pontos:

> In the rust-native chain the managed Process never runs — the managed object is just the parameter model the UI binds to.

Ez a mondat a kód egy részében igaz, a nyilvános szerződésben és több produkciós feature-ben **nem**.

`RustNativeChain.Enabled` 4.0 óta mindig `true` (az `Override` csak tesztkapcsoló). Legacy mix thread **nincs**. `AudioMixer.MixThread.cs` ma már csak hibakezelő, nem keverőhurok.

---

## 3. Az alapszabály a valós idejű úton

### 3.1 Ami rendben van

- `AudioMixer` rust-native módban nem indít mix threadet. A minták a `MultiTrackSession.OpenOutput` után **nem lépnek be managed memóriába**.
- Track és master effect hozzáadásakor `AttachMasterEffectToRust` / `_rebuildTrackChain` natív twin-t épít, a managed `Process` nem fut.
- File / Sample / Input source rust-native trackre ül. `InputSource.ReadSamples()` szándékosan csendet ad vissza (obsolete), mert a capture natív ringbe megy.
- VST3 a mixerben: Rust hívja a `VST3Plugin_ProcessAudio` C ABI belépőt. A managed `VST3EffectProcessor.Process` (interleaved ↔ planar konverzió) **lejátszáskor nem fut**.
- Tempo / pitch / gain / pan a natív tracken van.
- Effect tap: a Rust mixer másolatot tesz lock-free ringbe. Az `EffectSpectrumAnalyzer` **másolaton** FFT-zik — ez elemzés, nem a busz feldolgozása.

### 3.2 Ami a realtime úton is problémás

**Egyedi `IEffectProcessor` adapter nélkül némán elesik.**

Ha a hívó saját effectet ad a lánchoz, és nincs bejegyzése a `RustEffectAdapters._adapters` táblában:

```
Effect 'X' has no native twin and stays silent — the managed Process path does not run on the rust chain
```

A 4.0 előtti viselkedés: a saját `Process()` lefutott a mix threaden. Most: log + csend. Ez kompatibilitástörés, csak nem API-szinten, hanem hangban.

**A `Process()` továbbra is a nyilvános szerződés része.**

`IEffectProcessor` ezt ígéri:

```csharp
void Process(Span<float> buffer, int frameCount);
```

A dokumentáció (`docs/documents/api-effects.html`) „zero-allocation in-place processing”-ként írja le. A mixer ezt **nem hívja**. Aki a doksi alapján `Process()`-t hív, C# DSP-t kap; aki mixerre rakja, natív DSP-t.

---

## 4. Ahol C# oldalon mégis audio adat megy át

Két kategória: **produkciós hangút** (az alapszabályt sérti) és **hideg / elemző út** (nem a busz, de audio adat).

### 4.1 Produkciós hangút — alapszabály sérül

#### A) Minden beépített `IEffectProcessor.Process`

`OwnAudio/Source/Effects/` alatt **teljes, működő DSP** van:

AutoGain, Chorus, Compressor, Delay, Distortion, DynamicAmp, Enhancer, Equalizer, Equalizer30Band, Flanger, Limiter, Overdrive, OwnReverb, Phaser, Reverb, Rotary, SmartMaster (`SmartMasterAudioChain` + Biquad, Crossover, FIR, PEQ, Subharmonic, Subsonic, PhaseAlignment).

Ezek nem stubok. Comb-szűrős reverb, lookahead limiter, 30 sávos biquad, FDN reverb — mind C#-ban, zero-alloc hot path kommentekkel. A Rust portok **ebből** készültek („reference C#”).

A mixer nem hívja őket. De:

- a nyilvános API igen (bárki meghívhatja),
- a tesztek igen,
- a Matchering igen.

#### B) `SourceWithEffects.ReadSamples`

```csharp
if (effect.Enabled) effect.Process(buffer, framesRead);
```

A dekorátor kommentje még mindig „hot path”-nak nevezi. Lejátszáskor a mixer nem olvassa a source-ot — a natív track szól. Ha viszont valaki `ReadSamples()`-t hív (elemzés, saját pump, régi kód), a **C# DSP fut**, nem a natív twin. Két hívási mód, két hang.

#### C) Matchering offline render — a legkomolyabb produkciós sérülés

`Audiomatchering.equalizer.cs` és `Audiomatchering.preset.cs`:

```csharp
directEQ.Process(chunk, frames);
globalCompressor.Process(chunk, frames);
dynamicAmplifier.Process(chunk, frames);
outputLimiter.Process(chunk, frames);
```

Ez fájlra renderelt mastering. **C# DSP**, nem a mixer natív lánca. Következmény:

- ugyanaz az EQ/comp/limiter a mixerben máshogy szólhat, mint a Matchering kimenete (a Rust szándékosan eltér, pl. frame-linked stereo a compressor/limiterben);
- a Matchering a „holt” C# implementációt tartja életben produkciós feature-ként;
- az alapszabály a mastering úton nem érvényes.

#### D) SmartMaster managed lánc

`SmartMasterEffect.Initialize` **mindig** felépíti a `SmartMasterAudioChain`-t (teljes C# DSP, scratch bufferek). Rust-native módban a `Process` nem fut a mixeren, de:

- a lánc allokálva van,
- `Process()` közvetlen hívásra C#-ban dolgozik (NaN szanitizálás + teljes lánc),
- a DSP tesztek ezt hívják,
- a README „managed mirror of the chain for non-native mode”-ot említ — non-native mód produkcióban **nincs**.

`SmartMasterAudioChain.Process` túlméretezett blokkra **allokál** (`new float[]`). Realtime-ban ez tilos; a mixer nem hívja, de a metódus továbbra is „hot path, must not allocate”-ként van dokumentálva.

#### E) VST3 managed `Process`

`VST3EffectProcessor.Process` interleaved ↔ planar konverziót és plugin hívást végez. A mixer natív belépőt használ. A VST README viszont még mindig ezt írja:

> Audio thread: `VST3EffectProcessor.Process`

Ez már nem igaz a mixer úton.

### 4.2 Hideg / elemző út — audio adat, de nem a busz

Ezek nem sértik a realtime alapszabályt, ha **egyértelműen offline elemzésnek** vannak jelölve. Ma részben azok, részben nem.

| Komponens | Mit csinál | Megjegyzés |
|---|---|---|
| `OwnAudioFft` | Managed FFT | Matchering, SmartMaster kalibráció, effect tap spektrum |
| Matchering `AnalyzeAudioFile` | Teljes fájl PCM + FFT | Offline, rendben; a **render** nem (lásd 4.1 C) |
| SmartMaster mérés | Tesztzaj, mic, FFT | Cold path, README szerint is |
| ChordDetect / BasicPitch / MT3 | Transzkripció, akkord | Offline / stream elemzés |
| `FileSource.ReadSamples` / `GetFloatAudioData` | Managed decoder elemzéshez | Komment szerint szándékos |
| `WaveAvaloniaDisplay` | Waveform rajzolás másolatból | UI |
| `EffectSpectrumAnalyzer` | FFT a tap másolatán | Rendben |
| `BpmDetect` (Safe) | Natív BPM | ChordDetect README még SoundTouch-ot említ — elavult |

Javaslat: ezeket **explicit offline/analysis** sávba tenni, és a Matchering **render** lépését kivenni a C# DSP-ből.

---

## 5. Félrevezető duplikációk

### 5.1 Azonos osztálynevek, két világ

Ugyanaz a típusnév két névtérben:

| `OwnaudioNET.Effects` (nyilvános) | `Ownaudio.Audio.Effects` (HighLevel) |
|---|---|
| `ReverbEffect` | `ReverbEffect` |
| `OwnReverbEffect` | `OwnReverbEffect` |
| `DelayEffect` | `DelayEffect` |
| `CompressorEffect` | `CompressorEffect` |
| `LimiterEffect` | `LimiterEffect` |
| `ChorusEffect`, `FlangerEffect`, `PhaserEffect`, `RotaryEffect` | ugyanaz |
| `DistortionEffect`, `OverdriveEffect`, `EnhancerEffect` | ugyanaz |
| `AutoGainEffect`, `DynamicAmpEffect` | ugyanaz |
| `EqualizerEffect` | `EqualizerEffect` |
| `Equalizer30BandEffect` | `Equalizer30Effect` (**más név**) |
| `SmartMasterEffect` (teljes lánc) | `NativeSmartMasterEffect` (üres token) |
| — | `GateEffect`, `PitchShiftEffect` (csak itt) |
| `VST3EffectProcessor` | `NativeVstEffect` |

`using` nélkül vagy mindkét assembly láthatóságánál ez könnyen rossz típust ad. A HighLevel típusoknak nincs `Process` metódusuk; a nyilvánosaknak van. A HighLevel `IsEnabled`, a nyilvános `Enabled`.

### 5.2 Két teljes DSP

A Rust modulok nyíltan a C#-t hívják referenciának:

> Rust DSP derived from the reference C# `OwnaudioNET.Effects.CompressorEffect`

Ugyanakkor **szándékos eltérések** vannak (compressor/limiter: frame-linked stereo vs. C# per-sample envelope). A `dsp-contract.json` ezt kezelni próbálja, de:

- a C# tesztcsomag (`EffectInvariantTests`, `EffectCatalog`) a **managed `Process()`-t** futtatja;
- a natív `dsp_contract.rs` a **Rust** DSP-t;
- a két implementációt **egymáshoz** csak részben kötik.

Ha a C# DSP-t „holt kódnak” tekintjük, a tesztek a holt kódot védik. Ha referenciának, akkor a szándékos eltérések miatt a referencia **nem** a produkciós hang.

### 5.3 Két effect-API a fejlesztőnek

- Nyilvános: `new ReverbEffect { RoomSize = 0.8f }; source.AddEffect(reverb);`
- HighLevel: `track.Effects.Add(EffectType.Reverb, 48000f)` → `Ownaudio.Audio.Effects.ReverbEffect`

Mindkettő publikus. A HighLevel README a HighLevel API-t mutatja; a webes doksi a `OwnaudioNET.Effects` API-t. Paraméternevek nem egyeznek (lásd 6.2).

### 5.4 SmartMaster kétszer

- Managed: `SmartMasterAudioChain` + 9 komponens, SIMD, „must not allocate”.
- Native: `ownaudio-core/src/effects/smartmaster/`, 0–91 paraméterid.
- Tükör: `_mirrorSmartMaster` a config mezőit tologatja.

A managed lánc Initialize-kor mindig létrejön, rust-native módban a hanghoz nem kell.

### 5.5 Mix viselkedés — ugyanaz a property, háromféle jelentés

`IEffectProcessor.Mix` minden effecten ott van. A valóság:

| Effect | C# `Process` | Natív | Adapter küldi a Mix-et? |
|---|---|---|---|
| Reverb, Delay, Chorus, Distortion, Overdrive, Flanger, Phaser, Rotary, Enhancer, OwnReverb | Használja | Használja | Igen |
| Equalizer (10 sáv) | Használja (dry/wet) | Használja | Igen |
| Equalizer30Band | **Nem olvassa** | **Dry/wet van** (B.6.2) | Igen — natívan hat, C# Process-ben nem |
| Compressor | No-op getter (`1.0`) | Van `PARAM_MIX` | Igen, de a setter elnyeli, mindig 1.0 megy |
| Limiter | No-op getter | HighLevel **ki sem teszi** a Mix-et | Adapter 1.0-t küld |
| AutoGain | No-op getter | HighLevel nincs Mix param | Adapter 1.0-t küld |
| DynamicAmp | Settable, komment szerint „nincs dry path” | Van Mix | Igen |
| SmartMaster | Settable, Process nem kever | Adapter küldi | Igen |
| VST3 | Managed Process kever; natív úton Enabled = host bypass, Mix nem így megy | Külön | Nem a Mirror-on |

Az `EffectCatalog` a C# viselkedést rögzíti (`MixHonored = false` a 30 sávos EQ-ra). A natív 30 sávos EQ viszont már kever. **A teszt a nem-produkciós utat kanonizálja.**

---

## 6. Ellentmondások

### 6.1 Dokumentáció vs. kód

| Állítás | Valóság |
|---|---|
| `AudioMixer` class comment: „top-priority mix thread does the real-time blending” | Mix thread nincs; csak rust control tick + recorder drain |
| `AudioMixer` ctor comment: „The mix thread only actually spins up on the first Start” | Nem spin-ol fel |
| `IEffectProcessor.Process` / webes doksi: in-place processing | Mixerben nem hívódik |
| `SourceWithEffects`: „intercepts ReadSamples to run the fx” | Lejátszáskor a natív lánc fut; `ReadSamples` külön út |
| Webes doksi / index: „16 built-in DSP effects” | CHANGELOG szerint a managed API 15, a Rust 17+ (Gate, PitchShift, SmartMaster, Vst, OwnReverb). A számok csúszkálnak |
| VST README: audio thread = `VST3EffectProcessor.Process` | Mixerben native function pointer |
| ChordDetect README: BPM = SoundTouch | `Ownaudio.Safe.BpmDetect` natív |
| SmartMaster README: „managed mirror for non-native mode” | Non-native mód produkcióban nincs |
| HighLevel `Equalizer30Effect`: „every band takes -12 – +12 dB” | C# és Rust clamp: **±18 dB** |
| `EqualizerEffect.Mix` komment: „EQ has no dry path, this stays at 1.0” | A 10 sávos `Process` **kever**; a komment a 30 sávosról másolódott |
| `Equalizer30BandEffect.Mix` komment: „this stays at 1.0” | A property settable, a C# Process ignorálja, a natív kever |
| `MasterClock`: „MixThread advances” | A mix thread nem létezik |

### 6.2 Paraméternevek: nyilvános vs. HighLevel vs. natív

Ugyanaz a hangparaméter három néven. A tükör a **nyilvános** propertyket olvassa, a HighLevel a sajátjait írja a FFI-re. A param **id**-k egyeznek, a **nevek** nem.

| Effect | Nyilvános (`OwnaudioNET`) | HighLevel (`Ownaudio.Audio`) | Natív id |
|---|---|---|---|
| Közös | `Enabled` | `IsEnabled` | 0 |
| Delay idő | `Time` (`int`) | `TimeMs` (`float`) | 2 |
| Delay feedback | `Repeat` | `Feedback` | 3 |
| Compressor küszöb | `Threshold` (getter dB, ctor **lineáris**) | `ThresholdDb` | 2 |
| Compressor attack | `AttackTime` | `AttackMs` | 4 |
| Compressor release | `ReleaseTime` | `ReleaseMs` | 5 |
| Compressor makeup | `MakeupGain` (getter dB, ctor **lineáris**) | `MakeupDb` | 6 |
| Compressor knee | `KneeWidth` | **nincs a HighLevel wrapperen** | 7 |
| Compressor Mix | no-op | **nincs** | 1 |
| Limiter | `Threshold`, `Ceiling`, `Release`, `LookAheadMs` | `ThresholdDb`, `CeilingDb`, `ReleaseMs`, `LookaheadMs` | 2–5 |
| EQ 10 sáv | `Band0Gain` … `Band9Gain` | `Band0` … `Band9` | 2–11 |
| EQ 30 sáv típusnév | `Equalizer30BandEffect` | `Equalizer30Effect` | — |
| DynamicAmp target | `TargetRmsLevelDb` | `TargetRmsDb` | 2 |

**Compressor konstruktor vs. property (ugyanabban az osztályban):**

```csharp
// XML: "threshold and makeup are linear"
public CompressorEffect(float threshold = 0.5f, ..., float makeupGain = 1.2f, ...)

public float Threshold { get => LinearToDb(_threshold); set => ... }   // dB
public float MakeupGain { get => LinearToDb(_makeupGain); set => ... } // dB
```

`new CompressorEffect()` után `Threshold` ≈ −6 dB (mert 0.5 lineáris), `MakeupGain` ≈ +1.6 dB. Aki a propertyket dB-ben állítja, jó; aki a konstruktort a property dokumentációja szerint dB-ben hívja, rossz küszöböt kap. A HighLevel default `MakeupDb = 1.6f` a lineáris 1.2-es ctor-hoz van igazítva — rejtett, törékeny egyezés.

**Delay `Damping`:** a C# komment beismeri, hogy a név ellentétes a viselkedéssel (magasabb = világosabb, nem sötétebb), és szándékosan így marad a natív paritás miatt. Félrevezető API, dokumentált csapda.

### 6.3 Viselkedésbeli eltérés C# Process vs. natív (produkció)

A Rust kommentek **szándékos** divergenciát írnak:

- **Compressor / Limiter:** C# per-sample envelope az interleaved bufferen → sztereó kép csúszhat. Rust frame-linked peak, azonos gain mindkét csatornán.
- **Equalizer30 Mix:** natív dry/wet (B.6.2); C# Process fully wet.
- **Limiter `Initialize`:** a C# sample rate-et a ctorból veszi, `Initialize(config)` a rate-et nem írja felül (EffectCatalog is jelzi). A natív a mixer rate-jén jön létre.
- **Delay C# Process:** `Channels != 2` → azonnal return (nincs hang). A natív track a session csatornaszámán fut.

Matchering a C# ágat használja, a mixer a natívat: **ugyanaz a preset, két hang**.

### 6.4 Hiányzó nyilvános effectek

Natív `EffectType`: Gate = 13, PitchShift = 14.

- HighLevel: van `GateEffect`, `PitchShiftEffect`.
- `RustEffectAdapters`: **nincs** Gate / PitchShift.
- `OwnaudioNET.Effects`: **nincs** wrapper.

A webes API-n nem adhatók hozzá. A HighLevel sessionön igen. Track pitch (`AudioTrack.PitchSemitones`) nem ugyanaz, mint az effect-lánc `PitchShiftEffect`. A CHANGELOG 4.0.4-preview.2 ezt már rögzítette (15 vs 17), a landing page még „16 built-in”-t ír.

### 6.5 Egyedi effect: hang nélküli kompatibilitás

`IEffectProcessor` publikus. A 4.0 előtti modell: implementáld `Process`-t, tedd a láncra. Most a mixer csak az adapter-táblában lévő típusokat engedi a natív buszra. Saját típus = csend. Nincs compile error, nincs kivétel a `AddEffect` végén (csak warning log a rebuildkor).

Ez az alapszabály és a kompatibilitás ütközésének tiszta példája.

### 6.6 „Reference C#” vs. „holt paramétermmodell”

A kód egyszerre állítja:

1. A managed object csak paramétermmodell, `Process` nem fut.
2. A Rust DSP a C# referenciából készült, a C# tesztek a referenciát védik.
3. A Matchering a C# `Process`-t hívja produkcióban.

Ez három szerep egy kódtömegre. Egyik sem van a típusokon jelölve.

---

## 7. Javítási terv — effect út

### 7.1 Döntés

Marad **mindkét megvalósítás mint hívási út**. A DSP **egy**: a natív motor.

| Hívási út | Ma | Cél |
|---|---|---|
| Mixer / `AddEffect` / master chain | Natív twin, `Process` nem fut | Változatlan: natív twin, command queue, audio thread |
| `IEffectProcessor.Process()` (Matchering, teszt, közvetlen hívás, `SourceWithEffects.ReadSamples`) | C# DSP | Ugyanaz a szignatúra, belül natív `process` |

```
Mixer lejátszás                    fx.Process(buffer)
        │                                   │
        ▼                                   ▼
 track / master twin              standalone native instance
 (set_param queue,                (azonnali set_param,
  cpal audio thread)               hívó thread)
        │                                   │
        └───────────────┬───────────────────┘
                        ▼
                 ownaudio-core
                 Effect::process()
```

A mixer **továbbra sem** hívja a managed `Process`-t. A `Process` **élve marad**, csak nem C# szűrőket futtat. A két natív példány szándékosan külön handle: a twin az audio threaden él, a standalone a `Process` hívó threadjén — egy handle megosztása adatverseny lenne.

A nyilvános típusok, propertyk, presetek, konstruktorok, `AddEffect` / `Process` / `Reset` / `Dispose` **megmaradnak**.

### 7.2 Paraméterlista — befagyasztva

**Egyik oldal paraméterlistája sem változik.** Nincs új property, nincs törlés, nincs alias (`TimeMs`, `Feedback`, `ThresholdDb`, `AttackMs`, …), nincs új managed wrapper (Gate, PitchShift), nincs új HighLevel mező (pl. `KneeWidth` a HighLevel compressoron), nincs új konstruktor-overload a „tisztább egység” kedvéért.

A meglévő tükör (`RustEffectAdapters`) a **már összepárosított** mezőket viszi tovább, a már használt neveken (`Time` → natív id 2, `Repeat` → id 3, stb.). Ez nem lista-bővítés, csak a mai híd.

#### Szabály A — managed van, natív nincs

A property **marad** a managed API-n (bináris / forrás kompatibilitás). A tükör **nem küldi** a natív oldalra. Hangot **nem** változtat — a setter/getter él, a DSP figyelmen kívül hagyja.

Ide tartozik minden olyan publikus mező, amire nincs natív param id, vagy a natív effect nem ismeri. Példák a mai felületről:

| Managed | Miért marad, miért no-op a motor felé |
|---|---|
| `IEffectProcessor.Id`, `Name` | Nem DSP. Marad. |
| `ResetGeneration` | Mixer-híd a `Reset()`-hez, nem hangparaméter. Marad. |
| Effect-specifikus `SampleRate` setter (Delay, Compressor, …) | A natív rate konstrukciókor dől el, nincs param id. A property marad; utólagos állítás a natív példányt nem retune-olja. |
| Minden további managed-only mező, ami később kiderül | Ugyanez: bent marad, tükör kihagyja. |

A `Mix` no-op getteres effecteken (Compressor, Limiter, AutoGain): a property **marad**. Ha a natív oldalon van `PARAM_MIX`, a mai adapter viselkedése marad (küldi, amit a getter ad — jellemzően 1.0). Ha egy effecten a managed `Mix` létezik, de a natív **nem** ismeri az id-t, a tükör ne küldje; a property akkor is a managed osztályon marad.

#### Szabály B — natív van, managed nincs

A natív paraméter **alapértelmezett értéken marad**. **Nem** jelenik meg a managed osztályon. A HighLevel wrapper listája sem bővül.

| Natív | Managed ma | Teendő |
|---|---|---|
| `EffectType.Gate`, `PitchShift` | Nincs `IEffectProcessor` | Nem készül wrapper. HighLevel / `EffectType` úton továbbra is elérhetők, a webes `OwnaudioNET.Effects` listán nem. |
| Compressor `PARAM_KNEE` (id 7) | `OwnaudioNET` *van* `KneeWidth` — azt tükrözzük. HighLevel `CompressorEffect`-en **nincs** | HighLevel-re **nem** tesszük rá. Aki HighLevel compressort ad a láncra, a natív default knee-t kapja (6 dB). |
| HighLevel-en hiányzó `Mix` (Compressor, Limiter, AutoGain wrapper) | `IEffectProcessor.Mix` a nyilvános típuson megvan | HighLevel wrapperre **nem** kerül Mix. A mixeres `OwnaudioNET` úton a mai Mirror megy. |
| Bármely későbbi natív-only id | Nincs managed property | Default, nincs managed implementáció. |

#### Ami nem számít lista-változásnak

- Ugyanaz a hangparaméter **más néven** a két publikus típuson (`Time` / `TimeMs`, `Enabled` / `IsEnabled`, `Band0Gain` / `Band0`): mindkét név **marad**, az adapter / HighLevel setter a meglévő id-re ír.
- A C# `Process` törzsének cseréje natív hívásra: a metódus lista nem változik.
- Belső, nem publikus mezők, delay line tömbök, IIR állapot **kimehetnek** — ezek nem a paraméterlista.

### 7.3 Hiányzó FFI

Ma nincs mixer-független process belépő. Effect csak track/master láncra tehető, a `set_param` command queue-n megy.

Kell standalone FFI (additive, az ABI szabály szerint nem törő):

- `create(effectType, sampleRate, channels) → handle`
- `set_param` / `get_param` — azonnal, nem queue
- `process(handle, buffer, frameCount, channels)` — in-place
- `reset` / `destroy`

A Rust `Effect` trait-en a `process` már megvan. A mixer FFI (`ownaudio_v1_track_add_effect`, `ownaudio_v1_effect_set_param(mixer, …)`) **nem változik**.

A mixer twin és a `Process()` standalone példánya **ne osszon handle-t**.

### 7.4 Managed osztály szerepe a célállapotban

`OwnaudioNET.Effects.*` marad a kompatibilis publikus típus:

1. Propertyk + preset + konstruktor = a mai paramétermmodell, **ugyanazzal a listával**.
2. `Initialize` / első `Process` létrehoz egy standalone natív effectet (sample rate / channels a configból).
3. `Process`: a szabály A/B szerinti tükör, aztán natív `process` a bufferen.
4. A C# comb / biquad / delay line / `SmartMasterAudioChain` DSP **kimegy a hangútból**. A mérés, preset, config objektumok maradnak.
5. Mixerbe rakva: a mai twin + `RustEffectAdapters.Mirror` marad. `Process` a saját standalone példányát hajtja (a Matchering mixer nélkül hívja; a két út nem ugyanazon a handle-n osztozik).

`SourceWithEffects.ReadSamples` innentől is `Process`-t hív — tehát automatikusan natív DSP-t kap, a dekorátor API-ja nem változik.

Matchering: továbbra is `new Equalizer30BandEffect` + `Process`. Ugyanaz a forrás, innentől a mixerrel azonos motor. A Matchering API nem változik.

### 7.5 Hangváltozás — csak a `Process()` úton

A mixer már Rust. Ami megváltozik, az a közvetlen `Process()` / Matchering / DSP teszt hangja, mert C# DSP helyett natívra vált:

| Ma `Process()` | Cél (natív, ugyanaz mint a mixer) |
|---|---|
| Compressor/limiter per-sample envelope | Frame-linked sztereó |
| Equalizer30 `Mix` ignorálva | Natív dry/wet, ha a managed `Mix` tükrözve van |
| Delay csak 2 csatornán | Natív csatornaszám |
| Matchering ≠ mixer | Ugyanaz a motor |

Ez nem paraméterlista-változás. A mixer kimenete bit szerint nem ez a feladat.

A `dsp-contract.json` mindkét runnerje ugyanarra a Rust kódra futhat. A managed `EffectCatalog` innentől a natív `Process` wrappert méri — ez a cél.

### 7.6 Fázisok

**Fázis 1 — standalone FFI + egy effect próbaút**

Standalone create/process/set_param. Egy egyszerű effect (pl. Equalizer vagy Distortion) `Process()` átkötése. Mixer twin érintetlen. Paraméterlista érintetlen.

**Fázis 2 — minden beépített `IEffectProcessor` (VST kivétel)**

A `RustEffectAdapters` táblában lévő típusok `Process`-e natív. Tükör: csak a ma is létező, mindkét oldalon ismert párok. Managed-only: no-op a motor felé. Native-only: default, nincs új managed mező.

SmartMaster: `Process` a natív SmartMaster effectet hívja a meglévő config-tükörrel. A managed chain nem a hangút. Mérés / preset / mic monitor marad.

**Fázis 3 — Matchering és tesztek**

Matchering forrást nem kell írni, ha a `Process` már natív. Contract / invariant tesztek a natív wrappert mérik. Ahol a régi C# viselkedés eltért, a várható érték a natív (produkciós) viselkedés.

**Fázis 4 — doksi, nem API**

- `Process` dokumentáció: élő belépő, a motor natív; a mixer a twinen keresztül ugyanazt a motort használja, `Process` nélkül.
- Elavult mix-thread / „C# hot path” kommentek.
- Effect-szám: managed lista vs. natív `EffectType` (Gate, PitchShift) — **nem** tesszük át a hiányzókat a managed listára.
- Egyedi `IEffectProcessor` mixerbe: továbbra is nincs natív twin. Dobjon, ne némuljon. Ez nem paraméterlista-változás.

VST: a mixer marad native process pointer. A managed `VST3EffectProcessor.Process` külön host-út; ennek összevonása nem része ennek a tervnek, és nem bővíti a paraméterlistát.

### 7.7 Amit ez a terv kifejezetten nem csinál

- Nem törli a `Process` metódust, nem jelöli obsolete-nak, nem vezet be `ProcessOffline`-t.
- Nem húzza vissza a C# DSP-t a mixer audio threadjére.
- Nem bővíti aliasokkal a managed propertyket, még akkor sem, ha a HighLevel más nevet használ.
- Nem ad Gate / PitchShift `IEffectProcessor` wrappert.
- Nem teszi internalizálja a HighLevel effect típusokat, és nem simítja egy típusnévre az `Equalizer30BandEffect` / `Equalizer30Effect` párt.
- Nem ad dB-s compressor konstruktort a lineáris mellé.
- Nem osztja meg a mixer twin handle-jét a `Process()` hívással.
- Nem emulál standalone process-t rejtett offline mixerrel.

---

## 8. Prioritás

| Prioritás | Tétel | Miért |
|---|---|---|
| P0 | Standalone FFI (`create` + `process` + azonnali `set_param`) | Enélkül a `Process()` nem tud natív motort hívni |
| P0 | Paramétertükör szabály A/B rögzítése az adapterben | Lista nem változhat; a no-op / default viselkedés legyen tudatos |
| P1 | Minden beépített `IEffectProcessor.Process` → standalone natív | Matchering, teszt, `ReadSamples` ugyanazt a motort kapja |
| P1 | SmartMaster `Process` natív twin/standalone, managed chain le a hangútról | Dupla DSP megszűnik |
| P2 | Contract / invariant tesztek a natív `Process` wrappre | A teszt a produkciós motort mérje |
| P2 | Doksi: két hívási út, egy motor; Gate/PitchShift nem kerül a managed listára | Számok és `Process` szerződés |
| P2 | Egyedi effect mixerbe: explicit hiba, ne csend | Hang nélkül „működik” |
| P3 | Elavult mix-thread / C# hot path kommentek | Nem API |

---

## 9. Zárszó

A realtime mixer **már** a natív motort használja. A `Process()` / Matchering **még** a C# DSP-t. A javítás nem az egyik út megszüntetése, hanem hogy **mindkettő ugyanazt a motort** hívja.

A kompatibilitás két zára:

1. A hívási felület marad: `AddEffect`, `Process`, presetek, propertyk.
2. A paraméterlista marad: ami ma managed, az ott marad (nincs natív párja → no-op a motor felé); ami ma csak natív, az defaulton marad (nincs managed implementáció).

Amit nem szabad: két DSP-t csendben eltérően futtatni, vagy a listákat „rendbe tenni” aliasokkal és hiányzó wrapperekkel. A rend a motorban van, nem az API átnevezésében.
