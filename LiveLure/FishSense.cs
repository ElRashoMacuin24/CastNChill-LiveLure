using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveLure
{
    // The game's own fish table gives every species a temperament (how it chases: its "chase action") and a
    // lifestyle (how it lives when left alone: its "idle action"), plus speed, vision and hearing levels. Everything
    // here is derived from those, so the behaviour follows what the game already says about each fish.

    enum Temper { Tentative, Steady, Aggressive, Skittish }
    enum Role { School, Recluse, Predator, Ambusher, Hoverer, Drifter }

    /// <summary>What a fish that has come to look at the lure needs to see before it takes it.</summary>
    enum Trigger
    {
        /// <summary>Shy and bottom feeders: the lure lying still or sinking freely.</summary>
        Pause,
        /// <summary>Ambush predators: the lure stopping dead or dropping just after it moved.</summary>
        Stall,
        /// <summary>Chasers: the lure speeding up, running away from them.</summary>
        Flee,
        /// <summary>Steady feeders: the lure's proper action, kept going without a break.</summary>
        Keep,
        /// <summary>Wary followers: any sudden change of pace.</summary>
        ChangeUp,
    }

    sealed class Species
    {
        public string Id = "?";
        public Temper Temper;
        public Role Role;
        public Trigger Trigger;
        public int Speed = 2, Vision = 2, Hearing = 2;
        /// <summary>Chance to charge a lure ripped past it, and chance that the same rip scares it instead (0..1).</summary>
        public float React, Spook;
        /// <summary>How long it holds the lure compared with the configured hookset window.</summary>
        public float BiteWindow = 1f;
        /// <summary>What the HUD calls it and what it tells the player to do.</summary>
        public string Label = "FISH", Want = "";

        static readonly Dictionary<IntPtr, Species> s_cache = new Dictionary<IntPtr, Species>();

        public static Species For(Game.FishData data)
        {
            if (data == null) return null;
            IntPtr key = data.Pointer;
            if (!s_cache.TryGetValue(key, out Species sp))
            {
                sp = Build(data);
                s_cache[key] = sp;
            }
            return sp;
        }

        static Species Build(Game.FishData data)
        {
            var sp = new Species();
            try
            {
                sp.Id = data.id ?? "?";
                Game.FishChaseActionData chase = data.chaseAction;
                Game.FishIdleActionData idle = data.idleAction;
                string c = chase != null ? chase.id : null, i = idle != null ? idle.id : null;
                sp.Temper = c == "aggressive" ? Temper.Aggressive : c == "steady" ? Temper.Steady : c == "skittish" ? Temper.Skittish : Temper.Tentative;
                sp.Role = i == "ambusher" ? Role.Ambusher : i == "predator" ? Role.Predator : i == "recluse" ? Role.Recluse
                        : i == "hoverer" ? Role.Hoverer : i == "drifter" ? Role.Drifter : Role.School;
                sp.Speed = data.speed; sp.Vision = data.vision; sp.Hearing = data.hearing;
            }
            catch (Exception e) { Plugin.Error("Species", e); }
            Derive(sp);
            return sp;
        }

        /// <summary>Turns temperament and lifestyle into behaviour. Kept separate so it can be checked without a game object.</summary>
        public static void Derive(Species sp)
        {
            bool bottom = sp.Role == Role.Hoverer || sp.Role == Role.Drifter;

            // An ambusher lies in wait and hits what stalls in front of it; a bottom feeder wants it lying there; the
            // rest follow their temperament.
            if (sp.Temper == Temper.Skittish) sp.Trigger = Trigger.ChangeUp;
            else if (bottom || sp.Temper == Temper.Tentative) sp.Trigger = Trigger.Pause;
            else if (sp.Role == Role.Ambusher) sp.Trigger = Trigger.Stall;
            else sp.Trigger = sp.Temper == Temper.Aggressive ? Trigger.Flee : Trigger.Keep;

            // Reaction strikes: instinct, not hunger. Strongest in the ambushers, absent in shy fish.
            float react;
            switch (sp.Temper)
            {
                case Temper.Aggressive:
                    react = sp.Role == Role.Ambusher ? 0.75f : sp.Role == Role.Predator ? 0.65f : sp.Role == Role.Recluse ? 0.5f : 0.45f;
                    break;
                case Temper.Steady:
                    react = sp.Role == Role.Ambusher ? 0.35f : sp.Role == Role.Predator ? 0.3f : sp.Role == Role.Recluse ? 0.2f
                          : sp.Role == Role.School ? 0.15f : 0.05f;
                    break;
                case Temper.Skittish: react = 0.2f; break;
                default: react = 0f; break;
            }
            sp.React = react > 0f ? Math.Clamp(react + 0.04f * (sp.Speed - 3), 0.02f, 0.9f) : 0f;

            // The same violent movement sends shy fish the other way.
            switch (sp.Temper)
            {
                case Temper.Tentative: sp.Spook = sp.Role == Role.Hoverer ? 0.8f : sp.Role == Role.Drifter ? 0.7f : 0.6f; break;
                case Temper.Skittish: sp.Spook = 0.5f; break;
                case Temper.Steady: sp.Spook = sp.Role == Role.Recluse ? 0.4f : sp.Role == Role.School ? 0.3f : 0.2f; break;
                default: sp.Spook = sp.Role == Role.Recluse ? 0.1f : 0f; break;
            }

            // Aggressive fish clamp down and hold on; shy ones mouth the lure and spit it.
            sp.BiteWindow = sp.Temper == Temper.Aggressive ? 1.1f : sp.Temper == Temper.Steady ? 1f : sp.Temper == Temper.Tentative ? 0.85f : 0.8f;

            switch (sp.Trigger)
            {
                case Trigger.Pause:
                    sp.Label = bottom ? "BOTTOM FEEDER" : "SHY FISH";
                    sp.Want = "stop, let it take";
                    break;
                case Trigger.Stall: sp.Label = "AMBUSHER"; sp.Want = "pop it, then pause"; break;
                case Trigger.Flee: sp.Label = "CHASER"; sp.Want = "speed up, make it run"; break;
                case Trigger.Keep: sp.Label = "STEADY FEEDER"; sp.Want = "keep that rhythm"; break;
                default: sp.Label = "WARY FISH"; sp.Want = "change the pace"; break;
            }
        }

        public string Describe()
        {
            return string.Format("{0}: {1}/{2} -> {3} ({4}), react {5:0.00} spook {6:0.00} speed {7} vision {8} hearing {9}",
                Id, Temper, Role, Trigger, Label, React, Spook, Speed, Vision, Hearing);
        }
    }

    /// <summary>
    /// The lure's movement as a fish would read it: how fast, whether it just bolted, stopped, changed pace, is
    /// sinking freely, and how much commotion it is making.
    /// </summary>
    sealed class Motion
    {
        /// <summary>Speed over the last tenth of a second and over the last second and a half (px/s).</summary>
        public float Fast, Slow;
        public float StillFor;
        public float LastMove = -99f, LastBurst = -99f, LastRip = -99f, LastChange = -99f;
        public bool Ripping, Falling;
        public float RipPeak;
        public int RipId;
        /// <summary>0..1: how lively the lure has been lately, and how much noise and vibration it is putting out.</summary>
        public float Energy, Vibration;

        bool m_moving;
        float m_since = -99f;

        public void Step(ref Sample s, float loudness, float ripSpeed)
        {
            Fast += (s.speed - Fast) * Math.Min(1f, s.dt / 0.1f);
            Slow += (s.speed - Slow) * Math.Min(1f, s.dt / 1.5f);
            Energy = Math.Clamp(Slow / 90f, 0f, 1f);
            Vibration = s.inWater ? loudness * Math.Clamp(Fast / 60f, 0f, 1f) : 0f;
            if (!s.inWater)
            {
                // lifted clear of the water: nothing down there can see it
                Ripping = false; Falling = false; StillFor = 0f;
                return;
            }

            StillFor = Fast < 9f ? StillFor + s.dt : 0f;
            if (Fast >= 22f) LastMove = s.now;
            Falling = s.vy < -4f && s.pull < 5f && s.retrieve < 0.05f;

            // Changes of pace. A regular jigging rhythm is not one; doing something different is: starting off after
            // a real rest, killing a long steady pull, or suddenly speeding up something already moving.
            if (!m_moving && Fast >= 18f)
            {
                if (s.now - m_since >= 1.2f) LastChange = s.now;
                m_moving = true; m_since = s.now;
            }
            else if (m_moving && Fast < 9f)
            {
                if (s.now - m_since >= 1.5f) LastChange = s.now;
                m_moving = false; m_since = s.now;
            }
            if (Fast >= 28f && Fast >= 1.25f * Slow)
            {
                LastBurst = s.now;
                if (Slow >= 25f && Fast >= 1.6f * Slow) LastChange = s.now;
            }

            if (!Ripping)
            {
                if (Fast >= ripSpeed)
                {
                    Ripping = true;
                    RipId++;
                    RipPeak = Fast;
                    if (s.now - LastRip >= 1.5f) LastChange = s.now;
                    LastRip = s.now; LastBurst = s.now;
                }
            }
            else
            {
                if (Fast > RipPeak) RipPeak = Fast;
                LastRip = s.now;
                if (Fast < 0.6f * ripSpeed) Ripping = false;
            }
        }

        public string Kind(float now)
        {
            return Ripping ? "rip" : Falling ? "fall" : StillFor >= 0.3f ? "still" : now - LastBurst < 0.3f ? "burst" : Fast >= 18f ? "move" : "slow";
        }
    }

    /// <summary>What the mod remembers about one fish for the length of one cast.</summary>
    sealed class FishMem
    {
        public float ReactUntil = -99f, Cooldown = -99f;
        public int Refusals, RipId = -1;
    }

    /// <summary>
    /// Fish behaviour around the player's lure: reaction strikes and spooking when it is ripped past them, what a
    /// follower wants to see before it bites, how far off each kind of fish notices it, and the HUD's fish read-out.
    /// </summary>
    static class FishSense
    {
        static readonly System.Random s_random = new System.Random();
        public const string ChargingText = "REACTION STRIKE  -  it is charging, be ready";

        /// <summary>One roll of the dice, 0..1. The self-test can fix it.</summary>
        public static float Luck()
        {
            return SelfTest.ForcedLuck >= 0f ? SelfTest.ForcedLuck : (float)s_random.NextDouble();
        }

        /// <summary>How much thump, rattle and splash each kind of lure makes when it moves (0..1).</summary>
        public static float Loudness(string lureType)
        {
            switch (lureType)
            {
                case "bladebait": return 1f;
                case "crankbait": case "spinnerbait": return 0.8f;
                case "topwater": return 0.7f;
                case "spoonbait": return 0.5f;
                case "swimbait": return 0.4f;
                case "glidebait": return 0.3f;
                case "jighead": return 0.25f;
                default: return 0.1f;
            }
        }

        /// <summary>
        /// Is the lure doing what this fish wants right now? +1 yes, 0 neither here nor there, -1 the opposite.
        /// </summary>
        public static int Fit(Species sp, Motion m, float quality, bool gated, bool stillLure, float now)
        {
            switch (sp.Trigger)
            {
                case Trigger.Pause:
                    if (m.StillFor >= 0.3f || m.Falling) return 1;
                    return m.Fast > 45f ? -1 : 0;
                case Trigger.Stall:
                    return (m.Fast < 14f || m.Falling) && now - m.LastMove <= 2.2f ? 1 : 0;
                case Trigger.Flee:
                    if (m.Fast >= 18f && now - m.LastBurst <= 1.2f) return 1;
                    return m.StillFor > 2.5f && !stillLure ? -1 : 0;
                case Trigger.Keep:
                    if (quality >= 0.5f) return 1;
                    return gated && quality < 0.8f * Settings.MinQuality.Value ? -1 : 0;
                default:
                    return now - m.LastChange <= 1.3f ? 1 : 0;
            }
        }

        public static int Fit(Species sp, LureCtl ctl, float now)
        {
            if (SelfTest.ForcedFit > -2) return SelfTest.ForcedFit;
            return Fit(sp, ctl.Motion, ctl.Quality, ctl.Gated, ctl.StillLure, now);
        }

        /// <summary>The game counts this species as one this lure is made for (its lure type, or named on the lure).</summary>
        public static bool Bonus(LureCtl ctl, Game.FishData data)
        {
            IntPtr key = data.Pointer;
            if (!ctl.BonusCache.TryGetValue(key, out bool bonus))
            {
                try { bonus = ctl.Data.CanAttractFish(data, Game.LureAttractType.BonusOnly); }
                catch (Exception) { bonus = false; }
                ctl.BonusCache[key] = bonus;
            }
            return bonus;
        }

        /// <summary>The lure and rod can handle a fish this size, as the game's own attraction requires.</summary>
        static bool SizeOk(LureCtl ctl, Game.Fish fish)
        {
            if (ctl.Sizes == null)
            {
                ctl.Sizes = new HashSet<IntPtr>();
                var list = ctl.Lure.attractSizes;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        Game.FishSizeData size = list[i];
                        if (size != null) ctl.Sizes.Add(size.Pointer);
                    }
            }
            Game.FishSizeData mine = fish.Size;
            return mine != null && ctl.Sizes.Contains(mine.Pointer);
        }

        /// <summary>A few fish only bite on one kind of line; the base game turns them away from any other.</summary>
        static bool LineOk(LureCtl ctl, Game.FishData data)
        {
            Game.LineTypeData wants = data.preferredLineType;
            if (wants == null) return true;
            Game.RodData rod = ctl.Rod.Data;
            Game.LineTypeData has = rod != null ? rod.lineType : null;
            return has != null && has.Pointer == wants.Pointer;
        }

        /// <summary>Called every physics tick for the player's lure, once its action has been scored.</summary>
        public static void Tick(LureCtl ctl, ref Sample s)
        {
            Motion m = ctl.Motion;
            float now = s.now;
            bool quiet = ctl.PendingFish != null || now < ctl.QuietUntil; // a hookset jerk is not a presentation

            if (!ctl.Splashed && s.inWater)
            {
                ctl.Splashed = true;
                if (Settings.Splashdown.Value && ctl.Lure.StateDuration < 2f)
                {
                    float hit = Math.Clamp(s.speed / 120f, 0.4f, 1f);
                    float near = 40f * SelfTest.RangeScale;
                    Disturb(ctl, s.px, s.py, hit, near, near, 0.4f, 0.6f, false, "splashdown");
                }
            }

            if (m.Ripping && !quiet && now >= ctl.NextScan)
            {
                ctl.NextScan = now + 0.08f;
                float rip = Math.Max(20f, Settings.RipSpeed.Value);
                float hit = Math.Clamp(0.55f + 0.45f * (m.RipPeak - rip) / (0.55f * rip), 0.55f, 1f);
                float range = Settings.ReactionRange.Value * SelfTest.RangeScale;
                Disturb(ctl, s.px, s.py, hit, range, range * 0.75f, 1f, 1f, true, "rip");
            }

            if (now >= ctl.NextFollowerScan)
            {
                ctl.NextFollowerScan = now + 0.1f;
                FindLead(ctl, s.px, s.py);
            }
            if (ctl.Lead != null && ctl.LeadSpecies != null && Settings.FishTell.Value)
            {
                HudState hud = LureController.Hud;
                bool charging = ctl.LeadMem != null && ctl.LeadMem.ReactUntil > now;
                hud.FishStamp = Time.unscaledTime;
                hud.FishFit = charging ? 1 : Fit(ctl.LeadSpecies, ctl, now);
                hud.FishText = charging ? ChargingText : ctl.LeadSpecies.Label + " FOLLOWING  -  " + ctl.LeadSpecies.Want;
            }
        }

        /// <summary>The closest fish that is after the player's lure, for the HUD.</summary>
        static void FindLead(LureCtl ctl, float px, float py)
        {
            ctl.Lead = null; ctl.LeadSpecies = null; ctl.LeadMem = null;
            ctl.Followers = 0;
            var list = ctl.Lure.m_attractedFish;
            if (list == null) return;
            IntPtr mine = ctl.Lure.Pointer;
            float best = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                Game.Fish fish = list[i];
                if (fish == null || fish.IsIdle || fish.IsHooked) continue;
                Game.Lure after = fish.Lure;
                if (after == null || after.Pointer != mine) continue;
                ctl.Followers++;
                Vector2 fp = fish.Position;
                float d = (fp.x - px) * (fp.x - px) + (fp.y - py) * (fp.y - py);
                if (d < best) { best = d; ctl.Lead = fish; }
            }
            if (ctl.Lead == null) return;
            ctl.LeadSpecies = Species.For(ctl.Lead.Data);
            ctl.LeadMem = ctl.Mem(ctl.Lead.Pointer);
        }

        /// <summary>
        /// Something sudden happened at the lure: it was ripped through the water, or it just landed. Every fish close
        /// enough decides once what to make of it. Predators may charge; shy fish may bolt.
        /// </summary>
        static void Disturb(LureCtl ctl, float px, float py, float hit, float reactRange, float spookRange,
                            float reactScale, float spookScale, bool rip, string what)
        {
            Game.Location location = Game.Location.Instance;
            if (location == null) return;
            var spawns = location.spawns;
            if (spawns == null) return;

            Game.Lure lure = ctl.Lure;
            IntPtr mine = lure.Pointer;
            Motion m = ctl.Motion;
            float now = Time.time;
            bool reactions = Settings.ReactionStrikes.Value, spooking = Settings.Spooking.Value;
            float reactChance = Settings.ReactionChance.Value * reactScale, spookChance = Settings.SpookChance.Value * spookScale;
            Vector2 at = default;
            at.x = px; at.y = py;
            int seen = 0;

            for (int si = 0; si < spawns.Count; si++)
            {
                Game.LocationObjectSpawn spawn = spawns[si];
                if (spawn == null) continue;
                Game.FishData data = spawn.FishData;
                Species sp = Species.For(data);
                if (sp == null || (sp.React <= 0f && sp.Spook <= 0f)) continue;
                float reach = reactRange * (0.7f + 0.1f * Math.Min(sp.Vision, 6));
                float furthest = Math.Max(reach, spookRange);
                var entries = spawn.Entries;
                if (entries == null) continue;

                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    Game.Fish fish = entry != null ? entry.fish : null;
                    if (fish == null || fish.m_isDestroying) continue;
                    Vector2 fp = fish.Position;
                    float dx = fp.x - px, dy = fp.y - py;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d > furthest) continue;

                    FishMem mem = ctl.Mem(fish.Pointer);
                    if (rip)
                    {
                        if (mem.RipId == m.RipId) continue; // one decision per fish per rip
                        mem.RipId = m.RipId;
                    }
                    seen++;

                    if (!fish.IsIdle)
                    {
                        // Already following this lure. Ripping it away from a shy fish sends it off; the others are
                        // judged on what the lure is doing when they reach it.
                        Game.Lure after = fish.IsHooked ? null : fish.Lure;
                        if (!rip || !spooking || after == null || after.Pointer != mine) continue;
                        if (sp.Trigger != Trigger.Pause || d > spookRange || (ctl.RipLure && Bonus(ctl, data))) continue;
                        float p = sp.Spook * hit * spookChance;
                        float roll = Luck();
                        if (roll >= p) continue;
                        fish.SetIdle();
                        fish.AvoidPosition(at);
                        ctl.Spooked++;
                        TotalSpooked++;
                        LureController.Hud.Tell("SPOOKED IT  -  too rough for a shy fish", -1, 2f);
                        Tel.Event(ctl, string.Format("{0}: spooked the following {1} at {2:0} px (chance {3:0.00})", what, sp.Id, d, p));
                        continue;
                    }

                    if (now < mem.Cooldown || fish.States.Idle.AvoidTimer > 0f) continue;

                    if (reactions && sp.React > 0f && d <= reach && SizeOk(ctl, fish) && LineOk(ctl, data))
                    {
                        float p = sp.React * hit * (1f - 0.5f * d / reach) * reactChance;
                        float roll = Luck();
                        if (roll < p)
                        {
                            if (Charge(ctl, fish, mem, rip))
                            {
                                Tel.Event(ctl, string.Format("{0}: {1} {2} from {3:0} px (speed {4:0} px/s, chance {5:0.00}, roll {6:0.00})",
                                    what, sp.Id, rip ? "REACTION STRIKE" : "came to look", d, rip ? m.RipPeak : m.Fast, p, roll));
                                continue;
                            }
                        }
                        else
                        {
                            mem.Cooldown = now + 4f; // it looked and let it go; it will not be fooled by the very next one
                            if (rip) Tel.Event(ctl, string.Format("{0}: {1} at {2:0} px watched it go by (chance {3:0.00}, roll {4:0.00})", what, sp.Id, d, p, roll));
                        }
                    }

                    // A lure being worked the way its own fish like it does not scare them, however lively. A landing
                    // lure is nobody's idea of a presentation.
                    if (spooking && sp.Spook > 0f && d <= spookRange && !(rip && ctl.RipLure && Bonus(ctl, data)))
                    {
                        float p = sp.Spook * hit * (1f - 0.5f * d / spookRange) * spookChance;
                        if (Luck() < p)
                        {
                            fish.AvoidPosition(at);
                            mem.Cooldown = now + 4f;
                            ctl.Spooked++;
                            TotalSpooked++;
                            Tel.Event(ctl, string.Format("{0}: scared off a {1} at {2:0} px (chance {3:0.00})", what, sp.Id, d, p));
                        }
                    }
                }
            }
            TotalSeen += seen;
            if (SelfTest.Observing) Tel.Event(ctl, string.Format("{0}: {1} fish within reach, strength {2:0.00}", what, seen, hit));
        }

        /// <summary>Running totals, for the self-test to check against.</summary>
        public static int TotalSeen, TotalReactions, TotalSpooked;

        /// <summary>
        /// Sends an idle fish at the lure. After a rip it comes at full strike speed and bites whatever the lure is,
        /// but it will not chase for long.
        /// </summary>
        public static bool Charge(LureCtl ctl, Game.Fish fish, FishMem mem, bool reaction)
        {
            Game.Lure lure = ctl.Lure;
            if (!fish.AttractToLure(lure)) return false;
            var following = lure.m_attractedFish;
            if (following != null) following.Add(fish); // the game's own attraction does this, so the lure can call the fish off later
            ctl.Attracted++;
            if (!reaction) return true;

            mem.ReactUntil = Time.time + 6f;
            Game.Fish.ChaseState chase = fish.States.Chase;
            chase.m_speed = chase.m_strikeSpeed;
            chase.m_chaseTimer = Math.Min(chase.m_chaseTimer, 5f);
            ctl.Reactions++;
            TotalReactions++;
            LureController.Hud.Tell(ChargingText, 1, 2f);
            return true;
        }

        /// <summary>
        /// How much further (above 1) or less far (below 1) this kind of fish notices the lure, given how it is being
        /// moved. Lively lures pull aggressive fish in from further off and calm ones bring the shy fish; noise and
        /// vibration carry to fish with good hearing well beyond where they could see it.
        /// </summary>
        public static float Notice(LureCtl ctl, Game.LocationObjectSpawn spawn)
        {
            if (!ctl.Mine || !Settings.FishBehaviour.Value) return 1f;
            Game.FishData data = spawn.FishData;
            Species sp = Species.For(data);
            if (sp == null) return 1f;
            Motion m = ctl.Motion;
            float f = sp.Temper == Temper.Aggressive ? 0.8f + 0.5f * m.Energy
                    : sp.Temper == Temper.Tentative ? 1.2f - 0.5f * m.Energy
                    : sp.Temper == Temper.Skittish ? 1.1f - 0.4f * m.Energy
                    : 1f;
            f *= 1f + 0.15f * Math.Max(0, sp.Hearing - 2) * m.Vibration;
            if (f < 1f && Bonus(ctl, data)) f = 1f; // the lure's own action never counts against the fish it is made for
            if (SelfTest.Observing && ctl.NoticeLogged < 16 && MathF.Abs(f - 1f) > 0.01f)
            {
                ctl.NoticeLogged++;
                Tel.Event(ctl, string.Format("notice: {0} sees it from x{1:0.00} the usual distance (energy {2:0.00}, vibration {3:0.00})", sp.Id, f, m.Energy, m.Vibration));
            }
            return f;
        }
    }
}
