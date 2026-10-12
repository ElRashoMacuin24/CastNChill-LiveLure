using System;
using System.Collections.Generic;

namespace LiveLure
{
    /// <summary>One physics tick of what the lure is doing in the water.</summary>
    struct Sample
    {
        public float dt, now;
        public float pull;      // speed toward the rod tip (px/s); negative = falling away on slack line
        public float vx, vy, speed;
        public float px, py;
        public float retrieve;  // reel power 0..1
        public bool inWater;
        public float pop;       // a topwater lure popped on this tick: how hard, 0..1 (0 = no pop)
    }

    /// <summary>
    /// Watches the lure's motion and scores how well it matches one real-world presentation.
    /// Quality is 0..1 and drives how often fish get a chance to notice the lure.
    /// Speeds are judged relative to MaxSpeed, the fastest this lure/rod combination can be pulled.
    /// </summary>
    abstract class Analyzer
    {
        public float Quality;
        public float MaxSpeed = 50f;
        public string LastEvent;

        public abstract void Step(ref Sample s);

        protected static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;

        /// <summary>0 below a, rising to 1 at b, 1 until c, falling to 0 at d.</summary>
        protected static float Trap(float x, float a, float b, float c, float d)
        {
            if (x <= a || x >= d) return 0;
            if (x < b) return (x - a) / (b - a);
            if (x <= c) return 1;
            return (d - x) / (d - c);
        }

        protected static float Smooth(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        protected static float Approach(float cur, float target, float maxDelta)
        {
            if (cur < target) return Math.Min(target, cur + maxDelta);
            return Math.Max(target, cur - maxDelta);
        }
    }

    /// <summary>Floats: the bait should sit still. Dragging it around kills the presentation.</summary>
    sealed class StillAnalyzer : Analyzer
    {
        float m_speed;

        public override void Step(ref Sample s)
        {
            m_speed += (s.speed - m_speed) * Math.Min(1, s.dt / 0.5f);
            float target = s.inWater ? 1 - Smooth(0.16f * MaxSpeed, 0.55f * MaxSpeed, m_speed) : 0;
            Quality = Approach(Quality, target, s.dt / 1.0f);
        }
    }

    /// <summary>Cranks, spinners, swimbaits: an even retrieve inside a speed band. Jerking it breaks the action.</summary>
    sealed class SteadyAnalyzer : Analyzer
    {
        readonly float m_lo, m_hi;
        float m_mean, m_var;

        public SteadyAnalyzer(float lo, float hi) { m_lo = lo; m_hi = hi; }

        public override void Step(ref Sample s)
        {
            float k = Math.Min(1, s.dt / 0.6f);
            m_mean += (s.pull - m_mean) * k;
            float dev = s.pull - m_mean;
            m_var += (dev * dev - m_var) * k;

            float cv = m_mean > 1f ? MathF.Sqrt(m_var) / m_mean : 9f;
            float band = Trap(m_mean / MaxSpeed, m_lo * 0.6f, m_lo, m_hi, m_hi * 1.25f);
            float steady = 1 - Smooth(0.3f, 0.7f, cv);
            Quality = Approach(Quality, s.inWater ? band * steady : 0, s.dt / 0.5f);
        }
    }

    sealed class CadenceParams
    {
        public float StartFrac = 0.2f;      // pull speed (fraction of MaxSpeed) that begins a stroke
        public float PeakLo, PeakHi;        // good stroke peak speed, fraction of MaxSpeed
        public float PeriodLo, PeriodHi;    // good time between strokes, seconds
        public float StrokeDurMax;          // longer than this is dragging, not a stroke
        public float Gain = 0.45f;          // meter gained by one perfect stroke
        public float TooHardPenalty = 0.25f;
        public bool FallBonus;              // a slow sink on slack line is itself the attraction
    }

    /// <summary>
    /// Stroke-and-rest presentations: jigging, hopping, ripping, walking the dog, pull-and-pause.
    /// A stroke is a burst of the lure being pulled toward the rod; the pause after it is where it falls or glides.
    /// </summary>
    sealed class CadenceAnalyzer : Analyzer
    {
        readonly CadenceParams P;
        bool m_inStroke;
        float m_strokeStart, m_peak, m_lastStart = -99f;
        int m_strokes;

        public CadenceAnalyzer(CadenceParams p) { P = p; }

        /// <summary>The hardest stroke this presentation still counts as good, as a fraction of MaxSpeed.</summary>
        public float PeakHi => P.PeakHi;

        public override void Step(ref Sample s)
        {
            float start = P.StartFrac * MaxSpeed;
            if (!m_inStroke)
            {
                if (s.inWater && s.pull > start) { m_inStroke = true; m_strokeStart = s.now; m_peak = s.pull; }
            }
            else
            {
                if (s.pull > m_peak) m_peak = s.pull;
                float dur = s.now - m_strokeStart;
                if (s.pull < start * 0.5f) { m_inStroke = false; Score(dur); }
                else if (dur > P.StrokeDurMax * 2.5f) Quality = Approach(Quality, 0, s.dt / 1.0f); // just reeling it in
            }

            // Hold the meter between strokes, then let it drain once the cadence is clearly broken.
            float sinceStroke = s.now - m_lastStart;
            Quality = Math.Max(0, Quality - s.dt / (sinceStroke > P.PeriodHi * 1.3f ? 1.5f : P.PeriodHi * 4f));

            if (P.FallBonus && !m_inStroke && s.inWater && s.retrieve < 0.05f)
            {
                float fall = -s.vy;
                if (fall > 0.06f * MaxSpeed && fall < 0.6f * MaxSpeed)
                {
                    Quality = Math.Min(1, Quality + s.dt / 1.2f);
                    m_lastStart = Math.Max(m_lastStart, s.now - P.PeriodHi); // a good sink keeps the meter alive
                }
            }
        }

        void Score(float dur)
        {
            float pk = m_peak / MaxSpeed;
            float peakScore = Trap(pk, P.PeakLo * 0.6f, P.PeakLo, P.PeakHi, P.PeakHi * 1.35f);
            float period = m_strokeStart - m_lastStart;
            float periodScore = m_strokes == 0 || period > P.PeriodHi * 3f
                ? 0.5f
                : Trap(period, P.PeriodLo * 0.55f, P.PeriodLo, P.PeriodHi, P.PeriodHi * 1.6f);
            float durScore = dur <= P.StrokeDurMax ? 1 : Math.Max(0, 1 - (dur - P.StrokeDurMax) / P.StrokeDurMax);
            float cycle = peakScore * periodScore * durScore;

            Quality = Math.Min(1, Quality + P.Gain * cycle);
            if (pk > P.PeakHi * 1.35f) Quality = Math.Max(0, Quality - P.TooHardPenalty);

            LastEvent = string.Format("stroke peak={0:0.00} dur={1:0.00}s period={2:0.00}s score={3:0.00} (peak {4:0.0} period {5:0.0} dur {6:0.0})",
                pk, dur, period > 50 ? 0 : period, cycle, peakScore, periodScore, durScore);
            m_lastStart = m_strokeStart;
            m_strokes++;
        }
    }

    /// <summary>
    /// Topwater: pop, pause, pop. What counts is the pops themselves (reported by Popper) and the rests between them;
    /// dragging the lure along or ripping it clear of the water is not popping.
    /// </summary>
    sealed class PopperAnalyzer : Analyzer
    {
        float m_last = -99f;
        int m_count;

        public override void Step(ref Sample s)
        {
            if (s.pop > 0f)
            {
                float gap = s.now - m_last;
                float spacing = m_count == 0 || gap > 6f ? 0.6f : Trap(gap, 0.2f, 0.45f, 2.5f, 4.5f);
                float size = s.pop <= 0.85f ? 1f : 0.6f; // a great chug every time is too much of a good thing
                float cycle = spacing * size;
                Quality = Math.Min(1f, Quality + 0.36f * cycle);
                LastEvent = string.Format("pop strength={0:0.00} gap={1:0.00}s score={2:0.00}", s.pop, gap > 50f ? 0f : gap, cycle);
                m_last = s.now;
                m_count++;
            }
            if (s.now - m_last > 3.5f) Quality = Math.Max(0f, Quality - s.dt / 2f);
            if (!s.inWater || s.speed > 2.2f * MaxSpeed) Quality = Math.Max(0f, Quality - s.dt / 0.6f);
        }
    }

    /// <summary>Drop shot: quiver the bait in place with small rod-tip shakes, without dragging it away.</summary>
    sealed class ShakeAnalyzer : Analyzer
    {
        const float Window = 1.5f;
        readonly Queue<float> m_crossings = new Queue<float>();
        int m_sign;
        bool m_hasPos;
        float m_x, m_y, m_drift;

        public override void Step(ref Sample s)
        {
            float dead = 0.07f * MaxSpeed;
            int sign = s.pull > dead ? 1 : s.pull < -dead ? -1 : 0;
            if (sign != 0 && sign != m_sign)
            {
                if (m_sign != 0) m_crossings.Enqueue(s.now);
                m_sign = sign;
            }
            while (m_crossings.Count > 0 && s.now - m_crossings.Peek() > Window) m_crossings.Dequeue();
            float hz = m_crossings.Count / Window / 2f;

            if (!m_hasPos) { m_x = s.px; m_y = s.py; m_hasPos = true; }
            float k = Math.Min(1, s.dt / 0.6f);
            float nx = m_x + (s.px - m_x) * k, ny = m_y + (s.py - m_y) * k;
            float driftNow = MathF.Sqrt((nx - m_x) * (nx - m_x) + (ny - m_y) * (ny - m_y)) / s.dt;
            m_x = nx; m_y = ny;
            m_drift += (driftNow - m_drift) * Math.Min(1, s.dt / 0.5f);

            float rhythm = Trap(hz, 0.8f, 1.6f, 7f, 10f);
            float inPlace = 1 - Smooth(0.2f * MaxSpeed, 0.5f * MaxSpeed, m_drift);
            Quality = Approach(Quality, s.inWater ? rhythm * inPlace : 0, s.dt / 0.7f);
        }
    }

    sealed class Technique
    {
        public string Name, Hint;
        public float MaxRateScale = 1f; // multiplies the configured max pulse rate
        public Func<Analyzer> Make;
    }

    /// <summary>How each lure type in the game is fished in real life.</summary>
    static class Techniques
    {
        static readonly Dictionary<string, Technique> s_byLureType = new Dictionary<string, Technique>
        {
            ["float"] = new Technique
            {
                Name = "FLOAT", Hint = "let it sit still", MaxRateScale = 0.35f,
                Make = () => new StillAnalyzer()
            },
            ["crankbait"] = new Technique
            {
                Name = "CRANKBAIT", Hint = "steady retrieve, no jerking",
                Make = () => new SteadyAnalyzer(0.45f, 1.15f)
            },
            ["spinnerbait"] = new Technique
            {
                Name = "SPINNERBAIT", Hint = "steady retrieve, keep the blade turning",
                Make = () => new SteadyAnalyzer(0.4f, 1.15f)
            },
            ["swimbait"] = new Technique
            {
                Name = "SWIMBAIT", Hint = "slow, even retrieve",
                Make = () => new SteadyAnalyzer(0.3f, 1.05f)
            },
            ["spoonbait"] = new Technique
            {
                Name = "SPOON", Hint = "lift, then let it flutter down",
                Make = () => new CadenceAnalyzer(new CadenceParams { PeakLo = 0.5f, PeakHi = 8f, PeriodLo = 0.6f, PeriodHi = 2.5f, StrokeDurMax = 0.9f })
            },
            ["jighead"] = new Technique
            {
                Name = "JIG", Hint = "hop it: lift, drop, pause",
                Make = () => new CadenceAnalyzer(new CadenceParams { PeakLo = 0.4f, PeakHi = 5.8f, PeriodLo = 0.6f, PeriodHi = 3.0f, StrokeDurMax = 0.9f })
            },
            ["bladebait"] = new Technique
            {
                Name = "BLADE BAIT", Hint = "rip it up hard, let it fall",
                Make = () => new CadenceAnalyzer(new CadenceParams { PeakLo = 1.8f, PeakHi = 8f, PeriodLo = 0.5f, PeriodHi = 2.0f, StrokeDurMax = 0.9f })
            },
            ["wacky"] = new Technique
            {
                Name = "WACKY RIG", Hint = "slow gentle lift, let it sink on slack",
                Make = () => new CadenceAnalyzer(new CadenceParams { StartFrac = 0.12f, PeakLo = 0.2f, PeakHi = 1.3f, PeriodLo = 1.2f, PeriodHi = 4.0f, StrokeDurMax = 1.2f, FallBonus = true, TooHardPenalty = 0.15f })
            },
            ["topwater"] = new Technique
            {
                Name = "TOPWATER", Hint = "pop it: short twitch, pause, repeat",
                Make = () => Settings.PopperHandling.Value
                    ? new PopperAnalyzer()
                    : new CadenceAnalyzer(new CadenceParams { StartFrac = 0.15f, PeakLo = 0.3f, PeakHi = 2.4f, PeriodLo = 0.25f, PeriodHi = 1.1f, StrokeDurMax = 0.45f, Gain = 0.3f })
            },
            ["glidebait"] = new Technique
            {
                Name = "GLIDE BAIT", Hint = "pull and pause, let it glide",
                Make = () => new CadenceAnalyzer(new CadenceParams { PeakLo = 0.4f, PeakHi = 2.2f, PeriodLo = 1.0f, PeriodHi = 3.0f, StrokeDurMax = 1.2f })
            },
            ["dropshot"] = new Technique
            {
                Name = "DROP SHOT", Hint = "shake the rod tip, keep it in place",
                Make = () => new ShakeAnalyzer()
            },
            ["texasrig"] = new Technique
            {
                Name = "TEXAS RIG", Hint = "hop it along the bottom",
                Make = () => new CadenceAnalyzer(new CadenceParams { PeakLo = 0.3f, PeakHi = 2.6f, PeriodLo = 1.0f, PeriodHi = 3.5f, StrokeDurMax = 0.9f })
            },
            ["carolinarig"] = new Technique
            {
                Name = "CAROLINA RIG", Hint = "slow drag, then pause",
                Make = () => new CadenceAnalyzer(new CadenceParams { StartFrac = 0.12f, PeakLo = 0.2f, PeakHi = 1.3f, PeriodLo = 1.5f, PeriodHi = 4.5f, StrokeDurMax = 1.8f })
            },
        };

        /// <summary>Null for lure types the mod does not know: those keep vanilla attraction.</summary>
        public static Technique For(string lureTypeId)
        {
            return lureTypeId != null && s_byLureType.TryGetValue(lureTypeId, out var t) ? t : null;
        }

        // The game's own lure table says which lures can also be jigged or simply retrieved. Those presentations
        // work on such a lure too, just not as well as the one it is known for.

        /// <summary>Plain lift-and-drop jigging, for any lure the game marks as jig-capable.</summary>
        public static Analyzer GenericJig()
        {
            return new CadenceAnalyzer(new CadenceParams { PeakLo = 0.4f, PeakHi = 8f, PeriodLo = 0.5f, PeriodHi = 3.0f, StrokeDurMax = 0.9f });
        }

        /// <summary>Plain steady retrieve, for any lure the game marks as retrievable.</summary>
        public static Analyzer GenericSteady()
        {
            return new SteadyAnalyzer(0.4f, 1.15f);
        }
    }
}
