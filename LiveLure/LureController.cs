using System;
using System.Collections.Generic;
using UnityEngine;
using LureState = Game.Lure.LureState;
using HookResult = Game.Fish.ChaseState.HookResult;

namespace LiveLure
{
    /// <summary>Flags the attraction patches read while Lure.AttractFish runs.</summary>
    static class Gate
    {
        /// <summary>We are calling AttractFish ourselves because the lure earned it.</summary>
        public static bool Pulse;
        /// <summary>The game's own timed AttractFish is running for a gated lure: let it scare fish, not attract them.</summary>
        public static bool BlockAttract;
        /// <summary>The lure a pulse is being fired for, so the per-species range patch knows how it is moving.</summary>
        public static LureCtl Ctl;
    }

    sealed class HudState
    {
        /// <summary>Unscaled time the player's lure last ticked in the water.</summary>
        public float Stamp = -99f;
        /// <summary>Unscaled time the player last had a fish on, and how loaded the line was (0..1).</summary>
        public float FightStamp = -99f, Tension;
        public string Title = "", Hint = "";
        public float Quality, MinQuality;
        public bool Gated;
        /// <summary>Game time until which the bite prompt shows.</summary>
        public float BiteUntil = -99f;
        public string BiteText = "BITE!  jerk the rod up";
        public string Flash = "";
        public float FlashUntil = -99f;

        /// <summary>Unscaled time a fish was last seen following the lure, what it is and whether the lure is doing what it wants (-1, 0, +1).</summary>
        public float FishStamp = -99f;
        public string FishText = "";
        public int FishFit;
        /// <summary>A short remark about a fish (it bumped the lure, it was spooked), shown until this game time.</summary>
        public string Note = "";
        public float NoteUntil = -99f;
        public int NoteMood;

        public void Say(string text, float seconds)
        {
            Flash = text;
            FlashUntil = Time.time + seconds;
        }

        public void Tell(string text, int mood, float seconds)
        {
            if (!Settings.FishTell.Value) return;
            Note = text;
            NoteMood = mood;
            NoteUntil = Time.time + seconds;
        }
    }

    /// <summary>Everything the mod tracks about one lure for one cast.</summary>
    sealed class LureCtl
    {
        public Game.Lure Lure;
        public Game.Rod Rod;
        public Game.Angler Angler;
        public Game.LureData Data;
        public Rigidbody2D Body;
        public string LureId, TypeId, AltNote = "";
        public Technique Tech;
        /// <summary>The presentation this lure is known for, plus the plainer ones the game says also work on it.</summary>
        public Analyzer Analyzer, AltJig, AltSteady;
        public float MaxSpeed;

        /// <summary>Fished by a person / by the person holding the mouse.</summary>
        public bool Human, Mine, Gated;
        public float PulseAcc, SlackTime, Quality;

        public Game.Fish PendingFish;
        public float PendingFrom, PendingUntil;

        // ---- fish behaviour ----
        public readonly Motion Motion = new Motion();
        public float Loudness;
        /// <summary>A lure that is meant to stay where it is (float, drop shot): lying still is not neglect.</summary>
        public bool StillLure;
        /// <summary>Hard strokes are part of how this lure is fished, so they do not scare the fish it is made for.</summary>
        public bool RipLure;
        public bool Splashed;
        /// <summary>No reaction strikes or spooking before this time: the rod is busy setting a hook.</summary>
        public float QuietUntil = -99f, NextScan, NextFollowerScan;
        public readonly Dictionary<IntPtr, FishMem> Fish = new Dictionary<IntPtr, FishMem>();
        public readonly Dictionary<IntPtr, bool> BonusCache = new Dictionary<IntPtr, bool>();
        public HashSet<IntPtr> Sizes;
        /// <summary>The nearest fish following this lure, if any.</summary>
        public Game.Fish Lead;
        public Species LeadSpecies;
        public FishMem LeadMem;
        public int Followers, NoticeLogged;

        // ---- topwater (see Popper) ----
        /// <summary>This lure is being fished as a popper.</summary>
        public bool Popper;
        /// <summary>Line currently let out to soften the rod's pull on it.</summary>
        public float PopGive;
        public bool PopBusy, PopArmed = true, PopFired;
        public float PopT0, PopPeak, PopX0;
        public int PopCount;

        // telemetry, reset every second
        public float TelNext, PullMin, PullMax, PullSum, SpeedSum, VyMin, VyMax;
        public int TelCount, Strokes, Pulses, Attracted, Reactions, Spooked;

        public FishMem Mem(IntPtr fish)
        {
            if (!Fish.TryGetValue(fish, out FishMem mem))
            {
                mem = new FishMem();
                Fish[fish] = mem;
            }
            return mem;
        }
    }

    static class LureController
    {
        static readonly Dictionary<IntPtr, LureCtl> s_ctls = new Dictionary<IntPtr, LureCtl>();
        static readonly System.Random s_random = new System.Random();

        public static readonly HudState Hud = new HudState();

        public static bool IsGated(Game.Lure lure)
        {
            return s_ctls.TryGetValue(lure.Pointer, out var ctl) && ctl.Gated;
        }

        static readonly Dictionary<IntPtr, float> s_baseMaxSpeed = new Dictionary<IntPtr, float>();

        /// <summary>
        /// The lure's own top drag speed as the game shipped it. Remembered the first time each lure is seen, because
        /// the yank boost changes the live value for the length of one call.
        /// </summary>
        public static float BaseMaxSpeed(Game.LureData data)
        {
            if (!s_baseMaxSpeed.TryGetValue(data.Pointer, out float speed))
            {
                speed = data.retrieveMaxSpeed;
                s_baseMaxSpeed[data.Pointer] = speed;
            }
            return speed;
        }

        /// <summary>
        /// How much faster than the base game's limit the line may drag this lure right now. Above 1 only for the
        /// player's own lure while they are ripping the rod upward.
        /// </summary>
        public static float PullBoost(Game.Lure lure)
        {
            if (!Settings.Enabled.Value || RodPose.Yank < 0.01f) return 1f;
            if (!s_ctls.TryGetValue(lure.Pointer, out var ctl) || !ctl.Mine) return 1f;
            if (ctl.Popper) return 1f + Math.Max(0f, Settings.PopSnap.Value) * RodPose.Yank; // a popper darts, it is never ripped
            return 1f + (Math.Max(1f, Settings.YankPower.Value) - 1f) * RodPose.Yank;
        }

        /// <summary>Called after every Lure.FixedUpdate.</summary>
        public static void Tick(Game.Lure lure)
        {
            Gate.BlockAttract = false;
            LureState state = lure.State;
            IntPtr key = lure.Pointer;
            s_ctls.TryGetValue(key, out LureCtl ctl);

            Fight.Watch(lure, state);
            if (state != LureState.Fishing)
            {
                if (ctl != null) s_ctls.Remove(key);
                if (state == LureState.Hooked) Fight.Tick(lure);
                return;
            }
            if (ctl == null)
            {
                ctl = Create(lure);
                if (ctl == null) return;
                s_ctls[key] = ctl;
            }

            float dt = Time.fixedDeltaTime, now = Time.time;
            ctl.Human = Settings.Enabled.Value && RodInput.Human(ctl.Angler);
            if (!ctl.Human)
            {
                ctl.Gated = false;
                ctl.PendingFish = null;
                if (!SelfTest.Observing) return;
            }
            Game.Angler mouseAngler = RodInput.Angler;
            ctl.Mine = ctl.Human && mouseAngler != null && mouseAngler.Pointer == ctl.Angler.Pointer;

            // ---- what is the lure doing? ----
            Vector2 v = ctl.Body.linearVelocity;
            Vector2 p = ctl.Body.position;
            Vector3 tip = ctl.Rod.TipPos;
            float tx = tip.x - p.x, ty = tip.y - p.y;
            float dist = MathF.Sqrt(tx * tx + ty * ty);
            var s = new Sample
            {
                dt = dt, now = now,
                vx = v.x, vy = v.y, px = p.x, py = p.y,
                speed = MathF.Sqrt(v.x * v.x + v.y * v.y),
                pull = dist > 0.001f ? (v.x * tx + v.y * ty) / dist : 0f,
                retrieve = ctl.Rod.RetrievePower,
                inWater = lure.m_inWater,
            };

            ctl.Popper = ctl.Human && ctl.TypeId == "topwater" && Settings.PopperHandling.Value;
            if (ctl.Popper) Popper.Tick(ctl, ref s, dt);
            else if (ctl.Human && s.inWater && ctl.TypeId == "topwater" && Settings.TopwaterFloat.Value) Float(ctl, ref s);
            if (ctl.Mine) TendLine(ctl, ref s, dt);

            float quality = 0f;
            if (ctl.Analyzer != null)
            {
                quality = Run(ctl, ctl.Analyzer, ref s, 1f, "");
                float cap = Settings.SecondaryCap.Value;
                if (ctl.AltJig != null) quality = Math.Max(quality, Run(ctl, ctl.AltJig, ref s, cap, "jig "));
                if (ctl.AltSteady != null) quality = Math.Max(quality, Run(ctl, ctl.AltSteady, ref s, cap, "retrieve "));
            }

            ctl.Quality = quality;
            ctl.Gated = ctl.Human && Settings.RequireTechnique.Value && ctl.Tech != null && !SelfTest.BlockAllAttraction;

            // ---- how the fish around it take that movement ----
            if (ctl.Mine)
            {
                ctl.Motion.Step(ref s, ctl.Loudness, Math.Max(20f, Settings.RipSpeed.Value));
                if (Settings.FishBehaviour.Value)
                {
                    FishSense.Tick(ctl, ref s);
                    if (lure.State != LureState.Fishing) return;
                }
            }

            // ---- attraction: earned by lure action instead of handed out on a timer ----
            if (ctl.Gated && s.inWater)
            {
                float minQ = Settings.MinQuality.Value;
                if (quality >= minQ)
                {
                    float maxRate = Settings.MaxPulseRate.Value * ctl.Tech.MaxRateScale;
                    ctl.PulseAcc += dt * maxRate * (0.35f + 0.65f * (quality - minQ) / Math.Max(0.01f, 1f - minQ));
                    // a pop is itself the call: fish look when they hear it, not on a timer
                    if (s.pop > 0f) ctl.PulseAcc += Math.Clamp(Settings.PopCall.Value, 0f, 1f);
                }
                if (ctl.PulseAcc >= 1f)
                {
                    ctl.PulseAcc -= 1f;
                    FirePulse(ctl, quality);
                    if (lure.State != LureState.Fishing) return;
                }
            }

            // ---- hookset: answer a bite with a sharp upward jerk ----
            if (ctl.PendingFish != null)
            {
                if (now > ctl.PendingUntil)
                {
                    ctl.PendingFish = null;
                    Hud.Tell("TOO SLOW  -  it spat the lure", -1, 1.8f);
                    Tel.Event(ctl, "bite not answered");
                }
                else if (RodPose.LastJerkTime >= ctl.PendingFrom && now >= RodPose.LastJerkTime + 0.1f)
                {
                    // a tenth of a second's grace lets the jerk finish, so its full strength is counted
                    Game.Fish fish = ctl.PendingFish;
                    ctl.PendingFish = null;
                    ResolveHookset(ctl, fish, RodPose.LastJerkTime - ctl.PendingFrom, RodPose.LastJerkStrength, p);
                    if (lure.State != LureState.Fishing) return;
                }
            }

            if (ctl.Mine)
            {
                Hud.Stamp = Time.unscaledTime;
                Hud.Title = ctl.Tech != null ? ctl.Tech.Name : "";
                Hud.Hint = ctl.Tech != null ? ctl.Tech.Hint + (ctl.AltNote.Length > 0 ? "   /   " + ctl.AltNote : "") : "";
                Hud.Quality = quality;
                Hud.MinQuality = Settings.MinQuality.Value;
                Hud.Gated = ctl.Gated;
                if (ctl.PendingFish == null && Hud.BiteUntil > now && !SelfTest.HoldBitePrompt) Hud.BiteUntil = -99f;
            }

            Tel.Sample(ctl, ref s, quality);
        }

        const float BailSlack = 6f, SlackKeep = 3f, YankTakeUp = 600f;
        static float s_waterLine = float.NaN;

        /// <summary>
        /// A topwater lure belongs on the surface. The game gives it neutral buoyancy, so it hangs at whatever depth
        /// it stops at; this lifts it to just under the waterline and holds it there like a floating plug.
        /// </summary>
        static void Float(LureCtl ctl, ref Sample s)
        {
            if (float.IsNaN(s_waterLine))
            {
                try { s_waterLine = Game.LureConfig.Instance.inWaterElevation; } catch (Exception) { s_waterLine = 2f; }
            }
            // Stay a little below the level the game counts as "out of the water": it reels in by itself above that.
            float target = s_waterLine - 1.5f;
            float accel = Math.Clamp(14f * (target - s.py) - 5f * s.vy, -60f, 90f);
            Vector2 force = default;
            force.y = accel * ctl.Body.mass;
            ctl.Body.AddForce(force);
        }

        /// <summary>
        /// Line management a real angler does without thinking, now that the rod itself moves:
        /// taking hold of the rod engages the reel after a cast; an open bail only gives line that something is
        /// actually pulling off the spool; and slack the lure is not using gets wound on, so the next movement of the
        /// rod reaches the lure. Slack a sinking lure is still falling on is left alone.
        /// </summary>
        static void TendLine(LureCtl ctl, ref Sample s, float dt)
        {
            Game.Rod rod = ctl.Rod;
            float slack = rod.LineLength - rod.DistanceToLure;

            if (RodInput.BailOpen)
            {
                if (slack > BailSlack) rod.LineLength = rod.DistanceToLure + BailSlack;
                ctl.SlackTime = 0f;
                return;
            }
            if (RodInput.Current == RodInput.Mode.Rod && rod.Spooling) rod.Spooling = false;
            if (!Settings.SlackPickup.Value || ctl.TypeId == "float") return;

            // A popper is worked on a nearly tight line, so the smallest twitch reaches it; other lures keep a little
            // slack so the rod can be moved without disturbing them.
            float keep = ctl.Popper ? 1f : SlackKeep;

            // A yank starts by snatching up whatever is loose, the way you tighten up before ripping a lure. Without
            // this, slack left from lowering the rod after the last stroke swallows most of the next one.
            // (Not a popper: ripping is the one thing it must not do.)
            if (!ctl.Popper && RodPose.Yank > 0.25f && slack > keep && !rod.Spooling && rod.RetrievePower > -0.05f)
            {
                rod.LineLength -= Math.Min(slack - keep, YankTakeUp * dt);
                ctl.SlackTime = 0f;
                return;
            }

            bool reelIdle = Math.Abs(rod.RetrievePower) < 0.05f && !rod.Spooling;
            bool takingLine = s.pull < -6f; // falling or drifting away from the rod: it is using the slack
            if (!reelIdle || takingLine || slack <= keep) { ctl.SlackTime = 0f; return; }
            ctl.SlackTime += dt;
            if (ctl.SlackTime > (ctl.Popper ? 0.2f : 0.35f)) rod.LineLength -= Math.Min(slack - keep, 70f * dt);
        }

        static float Run(LureCtl ctl, Analyzer analyzer, ref Sample s, float cap, string label)
        {
            analyzer.Step(ref s);
            if (analyzer.LastEvent != null)
            {
                ctl.Strokes++;
                Tel.Event(ctl, label + analyzer.LastEvent + " q=" + (analyzer.Quality * cap).ToString("0.00"));
                analyzer.LastEvent = null;
            }
            return analyzer.Quality * cap;
        }

        /// <summary>
        /// Called from the TryHooking patch when a fish reaches the lure and mouths it. The base game has already rolled
        /// its own dice (<paramref name="result"/>): hooked, just a nibble, or not interested in this kind of lure.
        /// What the lure is doing at that moment now decides most of it: give the fish what its kind wants to see and
        /// it takes the lure; do the opposite and it bumps it, and after a second refusal turns away.
        /// </summary>
        public static void OnStrike(Game.Fish fish, Game.Lure lure, ref HookResult result)
        {
            if (!s_ctls.TryGetValue(lure.Pointer, out LureCtl ctl) || !ctl.Human) return;
            HookResult vanilla = result;
            if (!ctl.Mine)
            {
                Tel.Event(ctl, "strike -> " + vanilla);
                return;
            }

            float now = Time.time;
            Species sp = Settings.FishBehaviour.Value ? Species.For(fish.Data) : null;
            FishMem mem = ctl.Mem(fish.Pointer);
            bool charging = sp != null && mem.ReactUntil > now;
            int fit = sp != null ? FishSense.Fit(sp, ctl, now) : 0;
            float minQ = Settings.MinQuality.Value;
            float worked = Math.Clamp((ctl.Quality - minQ) / Math.Max(0.01f, 1f - minQ), 0f, 1f);

            bool bite;
            string why;
            if (charging)
            {
                bite = true; // instinct: it hits whatever the lure is
                why = "reaction strike";
            }
            else if (vanilla == HookResult.Disinterested)
            {
                // Not a fish for this kind of lure. Worked well, and doing what the fish likes, it may be tempted anyway.
                float chance = Settings.TemptChance.Value * (sp == null ? worked : fit > 0 ? Math.Max(0.6f, worked) : fit == 0 ? 0.6f * worked : 0f);
                float roll = FishSense.Luck();
                bite = roll < chance;
                why = string.Format("wrong kind of lure, tempt chance {0:0.00} roll {1:0.00}", chance, roll);
            }
            else if (vanilla == HookResult.Hooked)
            {
                bite = fit >= 0;
                why = "took it";
            }
            else
            {
                bite = fit > 0;
                why = "mouthed it";
            }
            Tel.Event(ctl, string.Format("strike by {0}: game says {1}, lure is {2} ({3}), meter {4:0.00} -> {5} ({6})",
                sp != null ? sp.Id + " [" + sp.Label + ", wants " + sp.Trigger + "]" : "a fish", vanilla,
                fit > 0 ? "doing what it wants" : fit < 0 ? "doing the opposite" : "neutral", ctl.Motion.Kind(now), ctl.Quality,
                bite ? "BITE" : "no bite", why));

            if (!bite)
            {
                if (vanilla == HookResult.Disinterested)
                {
                    if (sp != null) Hud.Tell("TURNED AWAY  -  not its kind of lure", -1, 2f);
                    return;
                }
                result = HookResult.Nibbled;
                if (sp == null) return;
                if (fit < 0 && ++mem.Refusals >= 2)
                {
                    result = HookResult.Disinterested; // the game turns it round and sends it off
                    Hud.Tell("TURNED AWAY  -  " + sp.Want, -1, 2.5f);
                    Tel.Event(ctl, sp.Id + " gave up on it");
                }
                else Hud.Tell("BUMPED IT  -  " + sp.Want, fit < 0 ? -1 : 0, 2.5f);
                return;
            }

            if (!Settings.Hookset.Value)
            {
                result = HookResult.Hooked;
                return;
            }
            if (vanilla == HookResult.Hooked && !Settings.HooksetManualOnly.Value && !charging) return; // hooked itself, as in the base game

            // The fish has the lure in its mouth for a moment. It is on the player to drive the hook home.
            float hold = charging ? 0.7f : sp != null ? sp.BiteWindow : 1f;
            result = HookResult.Nibbled;
            ctl.PendingFish = fish;
            ctl.PendingFrom = now;
            ctl.PendingUntil = now + Settings.HooksetWindow.Value * hold;
            ctl.QuietUntil = ctl.PendingUntil + 0.6f;
            mem.ReactUntil = -99f;
            Hud.BiteUntil = ctl.PendingUntil;
            Hud.BiteText = charging ? "SMASH!  jerk the rod up" : hold < 0.95f ? "LIGHT BITE!  jerk the rod up" : "BITE!  jerk the rod up";
            Hud.NoteUntil = -99f;
            Tel.Event(ctl, string.Format("bite! {0:0.00}s to set the hook", ctl.PendingUntil - now));
        }

        static LureCtl Create(Game.Lure lure)
        {
            Game.Rod rod = lure.Rod;
            if (rod == null) return null;
            Game.LureData data = lure.Data;
            Rigidbody2D body = lure.Rigidbody;
            Game.Angler angler = rod.Angler;
            if (data == null || body == null || angler == null) return null;

            Game.LureTypeData type = data.lureType;
            Game.RodData rodData = rod.Data;
            var ctl = new LureCtl
            {
                Lure = lure, Rod = rod, Angler = angler, Data = data, Body = body,
                LureId = data.id, TypeId = type != null ? type.id : null,
                // The game caps how fast the line can drag a lure at the lure's own retrieveMaxSpeed; a faster rod only
                // picks line up quicker, it does not raise that cap.
                MaxSpeed = Math.Max(10f, BaseMaxSpeed(data)),
                TelNext = Time.time + 1f,
            };
            ctl.Loudness = FishSense.Loudness(ctl.TypeId);
            ctl.StillLure = ctl.TypeId == "float" || ctl.TypeId == "dropshot";
            ctl.Tech = Techniques.For(ctl.TypeId);
            if (ctl.Tech != null)
            {
                ctl.Analyzer = ctl.Tech.Make();
                ctl.Analyzer.MaxSpeed = ctl.MaxSpeed;

                bool jig = false, retrieve = false;
                var listed = data.techniques;
                if (listed != null)
                    foreach (Game.TechniqueData t in listed)
                    {
                        string id = t != null ? t.id : null;
                        if (id == null) continue;
                        if (id == "jig") jig = true;
                        else if (id.Contains("retrieve")) retrieve = true;
                    }
                if (jig && !(ctl.Analyzer is CadenceAnalyzer)) { ctl.AltJig = Techniques.GenericJig(); ctl.AltJig.MaxSpeed = ctl.MaxSpeed; }
                if (retrieve && !(ctl.Analyzer is SteadyAnalyzer)) { ctl.AltSteady = Techniques.GenericSteady(); ctl.AltSteady.MaxSpeed = ctl.MaxSpeed; }
                ctl.AltNote = ctl.AltJig != null && ctl.AltSteady != null ? "or jig / reel it"
                            : ctl.AltJig != null ? "or jig it"
                            : ctl.AltSteady != null ? "or just reel it" : "";
                ctl.RipLure = ctl.AltJig != null
                    || (ctl.Analyzer is CadenceAnalyzer strokes && strokes.PeakHi * ctl.MaxSpeed >= 0.9f * Settings.RipSpeed.Value);
            }
            Tel.ResetWindow(ctl);
            Tel.Event(ctl, string.Format("cast: type={0} technique={1}{2} maxSpeed={3:0} rod={4}",
                ctl.TypeId, ctl.Tech != null ? ctl.Tech.Name : "(vanilla)",
                (ctl.AltJig != null ? " +jig" : "") + (ctl.AltSteady != null ? " +retrieve" : ""),
                ctl.MaxSpeed, rodData != null ? rodData.id : "?"));
            SelfTest.OnCast(ctl);
            return ctl;
        }

        static void FirePulse(LureCtl ctl, float quality)
        {
            Game.Lure lure = ctl.Lure;
            int before = lure.AttractedFishCount;
            float timer = lure.attractTimer; // our pulses must not push the game's own (scare-only) tick back
            float sight = ctl.Data.visualMultiplier;
            Gate.Pulse = true;
            Gate.Ctl = ctl;
            try
            {
                // a lure with good action gets noticed from further away; the lure data is shared, so put it straight back
                ctl.Data.visualMultiplier = sight * (1f + Settings.RangeBonus.Value * quality);
                lure.AttractFish();
            }
            finally
            {
                ctl.Data.visualMultiplier = sight;
                Gate.Pulse = false;
                Gate.Ctl = null;
                Gate.BlockAttract = false;
            }
            lure.attractTimer = timer;

            ctl.Pulses++;
            int gained = lure.AttractedFishCount - before;
            if (gained > 0)
            {
                ctl.Attracted += gained;
                Tel.Event(ctl, "fish attracted");
            }
        }

        /// <summary>
        /// The odds of a hookset: best when the jerk comes quickly after the bite and is a full sweep of the rod,
        /// worse the later and weaker it is. A miss can pull the lure away from the fish and scare it off.
        /// </summary>
        static void ResolveHookset(LureCtl ctl, Game.Fish fish, float reaction, float strength, Vector2 lurePos)
        {
            Game.Lure lure = ctl.Lure;
            Hud.BiteUntil = -99f;
            ctl.QuietUntil = Time.time + 0.6f;
            Game.Lure chasing = fish != null && !fish.IsHooked ? fish.Lure : null;
            if (chasing == null || chasing.Pointer != lure.Pointer)
            {
                Hud.Say("TOO LATE", 1.0f);
                Tel.Event(ctl, "hookset: the fish had already let go");
                return;
            }

            // how long this particular fish held on: a reaction strike or a shy fish gives less time than a hungry predator
            float window = Math.Max(0.35f, ctl.PendingUntil - ctl.PendingFrom), need = Settings.HooksetJerkDegrees.Value;
            float timing = reaction <= 0.3f ? 1f : 1f - 0.55f * Math.Min(1f, (reaction - 0.3f) / Math.Max(0.05f, window - 0.3f));
            float force = 0.6f + 0.4f * Math.Clamp((strength - need) / (need * 1.5f), 0f, 1f);
            float chance = Math.Clamp(Settings.HooksetBestChance.Value * timing * force, 0.05f, 1f);
            float roll = SelfTest.ForcedRoll >= 0f ? SelfTest.ForcedRoll : (float)s_random.NextDouble();
            string detail = string.Format("reaction {0:0.00}s jerk {1:0} deg -> chance {2:0.00} roll {3:0.00}", reaction, strength, chance, roll);

            if (roll < chance)
            {
                lure.HookFish(fish);
                Game.GameEvents.Angler.FishHooked.Invoke(fish);
                Hud.Say("FISH ON!", 1.4f);
                Tel.Event(ctl, "hookset HIT: " + detail);
                return;
            }

            bool spooked = s_random.NextDouble() < Settings.HooksetSpookChance.Value;
            if (spooked)
            {
                fish.SetIdle();
                fish.AvoidPosition(lurePos);
            }
            Hud.Say(spooked ? "MISSED - spooked it" : "MISSED!", 1.4f);
            Tel.Event(ctl, "hookset MISS" + (spooked ? " (fish spooked): " : " (fish still interested): ") + detail);
        }
    }

    /// <summary>Optional log output used to tune the technique thresholds against real play.</summary>
    static class Tel
    {
        public static void ResetWindow(LureCtl c)
        {
            c.PullMin = c.VyMin = float.MaxValue;
            c.PullMax = c.VyMax = float.MinValue;
            c.PullSum = c.SpeedSum = 0f;
            c.TelCount = c.Strokes = c.Pulses = c.Attracted = c.Reactions = c.Spooked = 0;
        }

        public static void Event(LureCtl c, string text)
        {
            if (!Settings.Telemetry.Value) return;
            Plugin.Logger.LogInfo("[" + c.LureId + "] " + text);
        }

        public static void Sample(LureCtl c, ref Sample s, float quality)
        {
            if (!Settings.Telemetry.Value) return;
            if (s.pull < c.PullMin) c.PullMin = s.pull;
            if (s.pull > c.PullMax) c.PullMax = s.pull;
            if (s.vy < c.VyMin) c.VyMin = s.vy;
            if (s.vy > c.VyMax) c.VyMax = s.vy;
            c.PullSum += s.pull; c.SpeedSum += s.speed; c.TelCount++;
            if (s.now < c.TelNext) return;
            c.TelNext = s.now + 1f;

            Plugin.Logger.LogInfo(string.Format(
                "[{0}] q={1:0.00} pull {2:0}/{3:0}/{4:0} vy {5:0}..{6:0} spd {7:0} rod {8:0} deg{9} slack {10:0.0} spring {11:0} reel {12:0.00} water {13} strokes {14} pulses {15} attracted {16}{17}{18}",
                c.LureId, quality, c.PullMin, c.PullSum / c.TelCount, c.PullMax, c.VyMin, c.VyMax, c.SpeedSum / c.TelCount,
                c.Mine ? RodPose.Current : 0f, c.Mine && RodInput.Current == RodInput.Mode.Rod ? " (gripped)" : "",
                c.Rod.LineLength - c.Rod.DistanceToLure, c.Rod.SpringForce, s.retrieve, s.inWater ? 1 : 0,
                c.Strokes, c.Pulses, c.Attracted,
                c.Mine ? string.Format(" | {0} energy {1:0.00} followers {2}{3}{4}", c.Motion.Kind(s.now), c.Motion.Energy, c.Followers,
                    c.Reactions > 0 ? " reactions " + c.Reactions : "", c.Spooked > 0 ? " spooked " + c.Spooked : "") : "",
                c.Human ? "" : " (idle AI)"));
            ResetWindow(c);
        }
    }
}
