using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Adaptív hangerőszabályzó osztály, amely valós időben képes a hangerő dinamikus kezelésére
    /// a hangdinamika megőrzése mellett.
    /// </summary>
    public class DynamicAmp : SampleProcessorBase
    {
        /// <summary>
        /// A célzott RMS hangerőszint (0.0 - 1.0 között)
        /// </summary>
        private float targetRmsLevel;

        /// <summary>
        /// Attack idő másodpercben - mennyi idő alatt reagáljon a hangerőnövekedésre
        /// </summary>
        private float attackTime;

        /// <summary>
        /// Release idő másodpercben - mennyi idő alatt reagáljon a hangerőcsökkenésre
        /// </summary>
        private float releaseTime;

        /// <summary>
        /// Zajküszöb - ez alatti jelszintet nem erősít
        /// </summary>
        private float noiseGate;

        /// <summary>
        /// Az aktuális erősítési szint
        /// </summary>
        private float currentGain = 1.0f;

        /// <summary>
        /// Az előző RMS érték
        /// </summary>
        private float lastRms = 0.0f;

        /// <summary>
        /// Létrehoz egy új DynamicAmp példányt a megadott paraméterekkel
        /// </summary>
        /// <param name="targetLevel">Célzott RMS szint (0.0 - 1.0 között)</param>
        /// <param name="attackTimeSeconds">Attack idő másodpercben (minimum 0.001)</param>
        /// <param name="releaseTimeSeconds">Release idő másodpercben (minimum 0.001)</param>
        /// <param name="noiseThreshold">Zajküszöb értéke (0.0 - 1.0 között)</param>
        /// <exception cref="ArgumentException">Ha valamelyik paraméter értéke érvénytelen</exception>
        public DynamicAmp(float targetLevel = 0.2f, float attackTimeSeconds = 0.1f,
                         float releaseTimeSeconds = 0.3f, float noiseThreshold = 0.001f)
        {
            ValidateAndSetTargetLevel(targetLevel);
            ValidateAndSetAttackTime(attackTimeSeconds);
            ValidateAndSetReleaseTime(releaseTimeSeconds);
            ValidateAndSetNoiseGate(noiseThreshold);
        }

        private void ValidateAndSetTargetLevel(float level)
        {
            if (level < 0.0f || level > 1.0f)
            {
                throw new ArgumentException($"A célzott szint értékének 0 és 1 között kell lennie. Kapott érték: {level}");
            }
            targetRmsLevel = level;
        }

        private void ValidateAndSetAttackTime(float timeInSeconds)
        {
            if (timeInSeconds < 0.001f)
            {
                throw new ArgumentException($"Az attack időnek minimum 0.001 másodpercnek kell lennie. Kapott érték: {timeInSeconds}");
            }
            attackTime = timeInSeconds;
        }

        private void ValidateAndSetReleaseTime(float timeInSeconds)
        {
            if (timeInSeconds < 0.001f)
            {
                throw new ArgumentException($"A release időnek minimum 0.001 másodpercnek kell lennie. Kapott érték: {timeInSeconds}");
            }
            releaseTime = timeInSeconds;
        }

        private void ValidateAndSetNoiseGate(float threshold)
        {
            if (threshold < 0.0f || threshold > 1.0f)
            {
                throw new ArgumentException($"A zajküszöb értékének 0 és 1 között kell lennie. Kapott érték: {threshold}");
            }
            noiseGate = threshold;
        }

        /// <summary>
        /// Beállítja a célzott hangerőszintet
        /// </summary>
        /// <param name="level">Célzott RMS szint (0.0 - 1.0 között)</param>
        /// <exception cref="ArgumentException">Ha az érték érvénytelen</exception>
        public void SetTargetLevel(float level)
        {
            ValidateAndSetTargetLevel(level);
        }

        /// <summary>
        /// Feldolgozza a beérkező hangmintákat és beállítja a megfelelő hangerőt
        /// </summary>
        /// <param name="samples">Sztereó hangminták tömbje</param>
        public override void Process(Span<float> samples)
        {
            // RMS számítás
            float sumSquares = 0.0f;
            for (int i = 0; i < samples.Length; i++)
            {
                sumSquares += samples[i] * samples[i];
            }
            float rms = MathF.Sqrt(sumSquares / samples.Length);

            // Zajküszöb kezelése
            if (rms < noiseGate)
            {
                return;
            }

            // Cél erősítés kiszámítása
            float targetGain = targetRmsLevel / Math.Max(rms, noiseGate);

            // Időállandók alkalmazása a simább átmenetekért
            float timeConstant = (targetGain > currentGain) ? attackTime : releaseTime;
            float alpha = MathF.Exp(-1.0f / (timeConstant * 44100.0f / samples.Length));

            // Erősítés frissítése
            currentGain = alpha * currentGain + (1.0f - alpha) * targetGain;

            // Minták módosítása
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= currentGain;
            }

            lastRms = rms;
        }

        /// <summary>
        /// Beállítja az attack időt
        /// </summary>
        /// <param name="timeInSeconds">Attack idő másodpercben</param>
        /// <exception cref="ArgumentException">Ha az érték érvénytelen</exception>
        public void SetAttackTime(float timeInSeconds)
        {
            ValidateAndSetAttackTime(timeInSeconds);
        }

        /// <summary>
        /// Beállítja a release időt
        /// </summary>
        /// <param name="timeInSeconds">Release idő másodpercben</param>
        /// <exception cref="ArgumentException">Ha az érték érvénytelen</exception>
        public void SetReleaseTime(float timeInSeconds)
        {
            ValidateAndSetReleaseTime(timeInSeconds);
        }

        /// <summary>
        /// Beállítja a zajküszöb értékét
        /// </summary>
        /// <param name="threshold">Zajküszöb értéke (0.0 - 1.0 között)</param>
        /// <exception cref="ArgumentException">Ha az érték érvénytelen</exception>
        public void SetNoiseGate(float threshold)
        {
            ValidateAndSetNoiseGate(threshold);
        }
    }
}
