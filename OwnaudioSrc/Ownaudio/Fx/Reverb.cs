using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Professzionális minőségű reverb effekt implementáció Freeverb algoritmus alapján.
    /// Alkalmas valós idejű audio feldolgozásra és professzionális hangminőség előállítására.
    /// </summary>
    public class Reverb : SampleProcessorBase
    {
        /// <summary>
        /// All-pass filter implementáció, amely fázisváltást végez a jelben 
        /// anélkül, hogy megváltoztatná a frekvencia spektrumot.
        /// </summary>
        private class AllPassFilter
        {
            private readonly float[] buffer;        // Késleltetési buffer
            private int index;                      // Aktuális buffer pozíció
            private readonly float gain;            // Filter erősítés

            /// <summary>
            /// Inicializál egy új all-pass filtert.
            /// </summary>
            /// <param name="size">Buffer méret mintavételekben.</param>
            /// <param name="gain">Filter erősítés (általában 0.5f körüli érték).</param>
            public AllPassFilter(int size, float gain)
            {
                buffer = new float[size];
                this.gain = gain;
            }

            /// <summary>
            /// Feldolgoz egy input mintát.
            /// </summary>
            /// <param name="input">Bemeneti minta.</param>
            /// <returns>Feldolgozott minta.</returns>
            public float Process(float input)
            {
                float bufout = buffer[index];
                float temp = input * -gain + bufout;
                buffer[index] = input + (bufout * gain);
                index = (index + 1) % buffer.Length;
                return temp;
            }

            /// <summary>
            /// Törli a filter belső állapotát.
            /// </summary>
            public void Clear() => Array.Clear(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Comb filter implementáció, amely ismétlődő visszacsatolásokat hoz létre
        /// változtatható feedback és damping értékekkel.
        /// </summary>
        private class CombFilter
        {
            private readonly float[] buffer;        // Késleltetési buffer
            private int index;                      // Aktuális buffer pozíció
            private float feedback;                 // Visszacsatolás mértéke
            private float damp1;                    // Csillapítás paramétere
            private float damp2;                    // 1 - damp1
            private float filtered;                 // Előző szűrt minta

            /// <summary>
            /// Inicializál egy új comb filtert.
            /// </summary>
            /// <param name="size">Buffer méret mintavételekben.</param>
            public CombFilter(int size)
            {
                buffer = new float[size];
                feedback = 0.5f;
                damp1 = 0.2f;
                damp2 = 1f - damp1;
            }

            /// <summary>
            /// Beállítja a visszacsatolás mértékét.
            /// </summary>
            /// <param name="value">Visszacsatolás értéke (0.0 - 1.0).</param>
            public void SetFeedback(float value) => feedback = value;

            /// <summary>
            /// Beállítja a csillapítás mértékét.
            /// </summary>
            /// <param name="value">Csillapítás értéke (0.0 - 1.0).</param>
            public void SetDamp(float value)
            {
                damp1 = value;
                damp2 = 1f - value;
            }

            /// <summary>
            /// Feldolgoz egy input mintát.
            /// </summary>
            /// <param name="input">Bemeneti minta.</param>
            /// <returns>Feldolgozott minta.</returns>
            public float Process(float input)
            {
                float output = buffer[index];
                filtered = (output * damp2) + (filtered * damp1);
                buffer[index] = input + (filtered * feedback);
                index = (index + 1) % buffer.Length;
                return output;
            }

            /// <summary>
            /// Törli a filter belső állapotát.
            /// </summary>
            public void Clear()
            {
                Array.Clear(buffer, 0, buffer.Length);
                filtered = 0f;
            }
        }

        // Freeverb konstansok
        private const int NUM_COMBS = 8;           // Comb filterek száma
        private const int NUM_ALLPASSES = 4;       // All-pass filterek száma

        // Filter komponensek
        private readonly CombFilter[] combFilters;
        private readonly AllPassFilter[] allPassFilters;

        // Késleltetési idők mintavételekben (44.1kHz-re optimalizálva)
        private readonly float[] combTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private readonly float[] allPassTunings = { 556, 441, 341, 225 };

        // Effekt paraméterek
        private float roomSize;       // Terem mérete (0.0 - 1.0)
        private float damping;        // Magas frekvenciák csillapítása (0.0 - 1.0)
        private float width;          // Sztereó szélesség (0.0 - 1.0)
        private float wetLevel;       // Effektezett jel szintje (0.0 - 1.0)
        private float dryLevel;       // Száraz jel szintje (0.0 - 1.0)
        private float gain;           // Bemeneti erősítés
        private float sampleRate;     // Mintavételi frekvencia
        private readonly object parametersLock = new object();    // Thread-safety

        /// <summary>
        /// Terem méretének beállítása. Nagyobb érték nagyobb virtuális teret eredményez.
        /// </summary>
        public float RoomSize
        {
            get { lock (parametersLock) return roomSize; }
            set
            {
                lock (parametersLock)
                {
                    roomSize = Math.Clamp(value, 0.0f, 1.0f);
                    UpdateCombFilters();
                }
            }
        }

        /// <summary>
        /// Magas frekvenciák csillapításának mértéke.
        /// </summary>
        public float Damping
        {
            get { lock (parametersLock) return damping; }
            set
            {
                lock (parametersLock)
                {
                    damping = Math.Clamp(value, 0.0f, 1.0f);
                    UpdateDamping();
                }
            }
        }

        /// <summary>
        /// Sztereó szélesség beállítása.
        /// </summary>
        public float Width
        {
            get { lock (parametersLock) return width; }
            set { lock (parametersLock) width = Math.Clamp(value, 0.0f, 1.0f); }
        }

        /// <summary>
        /// Effektezett (wet) jel szintje.
        /// </summary>
        public float WetLevel
        {
            get { lock (parametersLock) return wetLevel; }
            set { lock (parametersLock) wetLevel = Math.Clamp(value, 0.0f, 1.0f); }
        }

        /// <summary>
        /// Száraz (dry) jel szintje.
        /// </summary>
        public float DryLevel
        {
            get { lock (parametersLock) return dryLevel; }
            set { lock (parametersLock) dryLevel = Math.Clamp(value, 0.0f, 1.0f); }
        }

        /// <summary>
        /// Mintavételi frekvencia beállítása Hz-ben.
        /// </summary>
        public float SampleRate
        {
            get { lock (parametersLock) return sampleRate; }
            set
            {
                lock (parametersLock)
                {
                    if (value <= 0)
                        throw new ArgumentException("Sample rate must be positive");

                    if (Math.Abs(sampleRate - value) > 0.01f)
                    {
                        sampleRate = value;
                        InitializeFilters();
                    }
                }
            }
        }

        /// <summary>
        /// Létrehoz egy új Professional Reverb effektet.
        /// </summary>
        /// <param name="size">A terem mérete</param>
        /// <param name="damp">A magas csillapítása</param>
        /// <param name="wet">Effektezett jel színtje</param>
        /// <param name="dry">Eredeti jel szintje</param>
        /// <param name="stereoWidth">A sztereó tér szélessége</param>
        /// <param name="gainLevel">Bemeneti erősítés</param>
        /// <param name="sampleRate">Mintavételi frekvencia Hz-ben.</param>
        public Reverb(float size = 0.5f, float damp = 0.5f, float wet = 0.33f, float dry = 0.7f, float stereoWidth = 1.0f,float gainLevel = 0.015f, float sampleRate = 44100)
        {
            this.sampleRate = sampleRate;
            combFilters = new CombFilter[NUM_COMBS];
            allPassFilters = new AllPassFilter[NUM_ALLPASSES];

            // Alapértelmezett paraméterek beállítása
            roomSize = size;
            damping = damp;
            width = stereoWidth;
            wetLevel = wet;
            dryLevel = dry;
            gain = gainLevel;

            InitializeFilters();
        }

        /// <summary>
        /// Inicializálja vagy újrainicializálja a filtereket a jelenlegi mintavételi frekvenciához.
        /// </summary>
        private void InitializeFilters()
        {
            float sampleRateScale = sampleRate / 44100f;

            // Comb filterek inicializálása
            for (int i = 0; i < NUM_COMBS; i++)
            {
                int size = (int)(combTunings[i] * sampleRateScale);
                combFilters[i] = new CombFilter(size);
            }

            // All-pass filterek inicializálása
            for (int i = 0; i < NUM_ALLPASSES; i++)
            {
                int size = (int)(allPassTunings[i] * sampleRateScale);
                allPassFilters[i] = new AllPassFilter(size, 0.5f);
            }

            UpdateCombFilters();
            UpdateDamping();
        }

        /// <summary>
        /// Frissíti a comb filterek visszacsatolási értékeit a teremméret alapján.
        /// </summary>
        private void UpdateCombFilters()
        {
            float roomFeedback = 0.7f + (roomSize * 0.28f);
            foreach (var comb in combFilters)
            {
                comb.SetFeedback(roomFeedback);
            }
        }

        /// <summary>
        /// Frissíti a comb filterek csillapítási értékeit.
        /// </summary>
        private void UpdateDamping()
        {
            float dampValue = damping * 0.4f;
            foreach (var comb in combFilters)
            {
                comb.SetDamp(dampValue);
            }
        }

        /// <summary>
        /// Feldolgoz egy buffer-nyi audio mintát.
        /// </summary>
        /// <param name="samples">Audio minták buffere.</param>
        public override void Process(Span<float> samples)
        {
            float currentWet, currentDry, currentWidth;
            lock (parametersLock)
            {
                currentWet = wetLevel;
                currentDry = dryLevel;
                currentWidth = width;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];
                float dry = input;
                float wet = 0;

                // Bemeneti erősítés alkalmazása
                input *= gain;

                // Freeverb algoritmus
                float mono = 0;
                foreach (var comb in combFilters)
                {
                    mono += comb.Process(input);
                }

                foreach (var allPass in allPassFilters)
                {
                    mono = allPass.Process(mono);
                }

                // Sztereó szélesség és keverés alkalmazása
                wet = mono * currentWidth;

                // Végső keverés
                samples[i] = wet * currentWet + dry * currentDry;
            }
        }

        /// <summary>
        /// Alaphelyzetbe állítja az effektet, törölve minden belső állapotot.
        /// </summary>
        public void Reset()
        {
            foreach (var comb in combFilters)
            {
                comb.Clear();
            }

            foreach (var allPass in allPassFilters)
            {
                allPass.Clear();
            }
        }
    }
}
