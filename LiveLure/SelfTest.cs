using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace LiveLure
{
    /// <summary>
    /// Developer harness, off unless [Debug] SelfTest is set. It blocks saving and achievements, lets the game's
    /// idle mode get a line in the water, then takes the angler over and plays scripted "mouse" input through the
    /// same path the real mouse uses: grip and move the rod, tap the bail, reel in, swing a cast, and answer forced
    /// bites with a missed and a landed hookset. Everything is checked from the log; the real pointer is not touched.
    /// </summary>
    static class SelfTest
    {
        /// <summary>Also analyse lures the idle AI is fishing, to see what vanilla motion looks like.</summary>
        public static bool Observing;
        public static bool BlockAllAttraction;
        /// <summary>Treat the angler as player-controlled even though idle mode is nominally active.</summary>
        public static bool ForceHuman;
        /// <summary>Turn the next strike into a bite regardless of what the fish decided.</summary>
        public static bool ForceNibble;
        /// <summary>When 0..1, replaces the hookset dice roll (0 always hits, 0.99 always misses).</summary>
        public static float ForcedRoll = -1f;
        /// <summary>Keep a bite prompt the HUD demo put up, even though no fish is behind it.</summary>
        public static bool HoldBitePrompt;
        /// <summary>When 0..1, replaces every fish-behaviour dice roll (0 = every chance comes off, 0.999 = none does).</summary>
        public static float ForcedLuck = -1f;
        /// <summary>When -1, 0 or +1, replaces the judgement of whether the lure is doing what a fish wants.</summary>
        public static int ForcedFit = -2;
        /// <summary>Stretches how far a ripped lure is noticed, so a test does not depend on where the fish happen to be.</summary>
        public static float RangeScale = 1f;
        /// <summary>Take the HUD off the screen, for a clear photograph of the water.</summary>
        public static bool HideHud;
        /// <summary>When 0..1, the drag setting to use instead of the player's saved one.</summary>
        public static float DragOverride = -1f;
        /// <summary>Leave the line to the base game for a moment, to compare against.</summary>
        public static bool VanillaLine;

        static bool s_enabled, s_done, s_owns;
        static float s_start = -1f, s_nextStatus, s_nextActivate;
        static int s_casts;
        static LureCtl s_ctl;
        static IEnumerator<int> s_script;

        public static void Init(Harmony harmony)
        {
            s_enabled = Settings.SelfTest.Value;
            if (!s_enabled) return;
            Observing = true;
            Type me = typeof(SelfTest);
            Plugin.Patch(harmony, typeof(Game.JournalManager), "Save", prefix: nameof(Skip), patchClass: me);
            Plugin.Patch(harmony, typeof(Game.JournalManager), "AutoSave", prefix: nameof(Skip), patchClass: me);
            Plugin.Patch(harmony, typeof(Game.Achievement), "Complete", prefix: nameof(Skip), patchClass: me);
            Plugin.Patch(harmony, typeof(Game.IdleManager), "Update", prefix: nameof(Idle_Pre), patchClass: me);
            foreach (var m in typeof(Steamworks.SteamUserStats).GetMethods())
            {
                if (m.Name != "SetAchievement" && m.Name != "StoreStats" && m.Name != "SetStat" && m.Name != "IndicateAchievementProgress") continue;
                try { harmony.Patch(m, new HarmonyMethod(AccessTools.Method(me, nameof(SkipTrue)))); }
                catch (Exception e) { Plugin.Logger.LogError("SelfTest could not block " + m.Name + ": " + e.Message); }
            }
        }

        static bool Skip() => false;
        static bool SkipTrue(ref bool __result) { __result = true; return false; }
        static bool s_letIdleRun;
        static bool Idle_Pre() => !s_owns || s_letIdleRun; // once the script has the angler, idle mode must not also drive it

        static string Name(Il2CppObjectBase o)
        {
            if (o == null) return "null";
            return Marshal.PtrToStringUTF8(IL2CPP.il2cpp_class_get_name(IL2CPP.il2cpp_object_get_class(o.Pointer)));
        }

        static void Log(string text) { Plugin.Logger.LogMessage("[selftest] " + text); }

        /// <summary>Called by the controller when a lure starts fishing.</summary>
        public static void OnCast(LureCtl ctl)
        {
            if (!s_enabled || s_done) return;
            s_ctl = ctl;
            s_casts++;
            Log("lure in the water #" + s_casts + ": " + ctl.LureId + " (" + ctl.TypeId + ")");
        }

        public static void Update()
        {
            if (!s_enabled || s_done) return;
            float now = Time.unscaledTime;
            if (s_start < 0f) { s_start = now; s_script = Script(); }
            float t = now - s_start;

            bool ready = false, idle = false;
            try { ready = Game.GameManager.IsReadyToPlay && Game.Location.IsLoaded; } catch (Exception) { }
            try { idle = Game.IdleManager.IsActive; } catch (Exception) { }

            if (now >= s_nextStatus)
            {
                s_nextStatus = now + 5f;
                string angler = "?";
                try { var a = Game.IdleManager.Angler; angler = a != null ? Name(a.State) : "none"; } catch (Exception e) { angler = e.GetType().Name; }
                Log(string.Format("t={0:0} ready={1} idle={2} owns={3} angler={4} grip={5} rod={6:0.0} bail={7}",
                    t, ready, idle, s_owns, angler, RodInput.Current, RodPose.Current, RodInput.BailOpen));
            }

            // let idle mode walk/drive/cast for us until the script takes over
            if (ready && !idle && !s_owns && now >= s_nextActivate)
            {
                s_nextActivate = now + 6f;
                try { Game.IdleManager.Instance.Activate(); Log("idle mode activated: " + Game.IdleManager.IsActive); }
                catch (Exception e) { Log("Activate failed: " + e.Message); }
            }

            bool more;
            try { more = s_script.MoveNext(); }
            catch (Exception e) { Log("SCRIPT FAILED: " + e); more = false; }
            if (!more || t > 330f)
            {
                s_done = true;
                RodInput.VGrip = RodInput.VReel = false;
                if (s_keepDrag >= 0f) Settings.Drag.Value = s_keepDrag; // the player's own drag setting, however the script ended
                DragOverride = -1f; VanillaLine = false;
                Log("SELFTEST COMPLETE" + (more ? " (timed out)" : ""));
            }
        }

        // ---------- script helpers: each yields once per frame ----------

        static IEnumerable<int> Wait(float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until) yield return 0;
        }

        /// <summary>Feeds a total mouse movement (in screen heights) evenly over a duration.</summary>
        static IEnumerable<int> Move(float dxHeights, float dyHeights, float seconds)
        {
            float h = Screen.height, left = seconds;
            while (left > 0f)
            {
                float dt = Math.Min(left, Time.unscaledDeltaTime);
                RodInput.VDx += dxHeights * h * dt / seconds;
                RodInput.VDy += dyHeights * h * dt / seconds;
                left -= dt;
                yield return 0;
            }
        }

        static bool s_timedOut;

        static IEnumerable<int> Until(Func<bool> condition, float timeout, string what)
        {
            float until = Time.unscaledTime + timeout;
            s_timedOut = false;
            while (true)
            {
                bool ok;
                try { ok = condition(); } catch (Exception) { ok = false; }
                if (ok) yield break;
                if (Time.unscaledTime > until) { s_timedOut = true; Log("TIMEOUT waiting for " + what); yield break; }
                yield return 0;
            }
        }

        static bool StateIs(Game.Angler angler, Game.Angler.AnglerState target)
        {
            Game.Angler.AnglerState s = angler.State;
            return s != null && target != null && s.Pointer == target.Pointer;
        }

        static string Check(bool ok) => ok ? "PASS" : "FAIL";

        /// <summary>Send the closest idle fish after the lure so a strike happens while the script is watching.</summary>
        static bool AttractNearest()
        {
            try
            {
                Game.Lure lure = s_ctl.Lure;
                Vector2 lp = s_ctl.Body.position;
                Game.Fish best = null; float bestDist = float.MaxValue;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Game.Fish>()))
                {
                    var f = o.TryCast<Game.Fish>();
                    if (f == null || !f.gameObject.activeInHierarchy) continue;
                    try
                    {
                        if (!f.IsIdle) continue;
                        Vector2 fp = f.Position;
                        float d = (fp.x - lp.x) * (fp.x - lp.x) + (fp.y - lp.y) * (fp.y - lp.y);
                        if (d < bestDist) { bestDist = d; best = f; }
                    }
                    catch (Exception) { }
                }
                if (best == null) { Log("no idle fish found to attract"); return false; }
                bool ok = best.AttractToLure(lure);
                Log(string.Format("sent {0} after the lure from {1:0} px away -> {2}", best.Data != null ? best.Data.id : "?", MathF.Sqrt(bestDist), ok));
                return ok;
            }
            catch (Exception e) { Log("AttractNearest failed: " + e.Message); return false; }
        }

        static Game.Fish NearestIdle(out float dist)
        {
            Game.Fish best = null; float bestDist = float.MaxValue;
            Vector2 lp = s_ctl.Body.position;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Game.Fish>()))
            {
                var f = o.TryCast<Game.Fish>();
                if (f == null || !f.gameObject.activeInHierarchy) continue;
                try
                {
                    if (!f.IsIdle) continue;
                    Vector2 fp = f.Position;
                    float d = (fp.x - lp.x) * (fp.x - lp.x) + (fp.y - lp.y) * (fp.y - lp.y);
                    if (d < bestDist) { bestDist = d; best = f; }
                }
                catch (Exception) { }
            }
            dist = MathF.Sqrt(bestDist);
            return best;
        }

        // ---------- fish lab: species table, the bite rules, and each fish reaction played out for real ----------

        static Species Kind(Temper temper, Role role)
        {
            var sp = new Species { Temper = temper, Role = role, Speed = 3 };
            Species.Derive(sp);
            return sp;
        }

        /// <summary>Plays made-up lure movement into a Motion: pairs of (speed in px/s, seconds).</summary>
        static Motion Sim(out float now, params float[] speedSeconds)
        {
            var m = new Motion();
            float t = 100f;
            for (int i = 0; i + 1 < speedSeconds.Length; i += 2)
            {
                float v = speedSeconds[i], until = t + speedSeconds[i + 1];
                while (t < until)
                {
                    t += 0.02f;
                    var s = new Sample { dt = 0.02f, now = t, speed = v, pull = v, vx = v, vy = 0f, inWater = true, retrieve = v > 0f ? 1f : 0f };
                    m.Step(ref s, 0.5f, 110f);
                }
            }
            now = t;
            return m;
        }

        static void RuleChecks()
        {
            Species pike = Kind(Temper.Aggressive, Role.Ambusher), bass = Kind(Temper.Aggressive, Role.Predator), trout = Kind(Temper.Steady, Role.Recluse),
                    perch = Kind(Temper.Tentative, Role.School), muskie = Kind(Temper.Skittish, Role.Ambusher), catfish = Kind(Temper.Steady, Role.Drifter);
            Log("RULE triggers by kind  " + Check(pike.Trigger == Trigger.Stall && bass.Trigger == Trigger.Flee && trout.Trigger == Trigger.Keep
                && perch.Trigger == Trigger.Pause && muskie.Trigger == Trigger.ChangeUp && catfish.Trigger == Trigger.Pause));
            Log(string.Format("RULE reaction/spook odds: ambusher {0:0.00}/{1:0.00} shy {2:0.00}/{3:0.00}  {4}", pike.React, pike.Spook, perch.React, perch.Spook,
                Check(pike.React > 0.7f && pike.Spook == 0f && perch.React == 0f && perch.Spook > 0.5f)));

            int F(Species sp, Motion m, float now, float quality = 0.4f) => FishSense.Fit(sp, m, quality, true, false, now);
            float t;
            Motion still = Sim(out t, 0f, 3f);
            Log("RULE lying still: shy takes, chaser loses interest, wary unmoved  " + Check(F(perch, still, t) == 1 && F(bass, still, t) == -1 && F(muskie, still, t) == 0 && F(pike, still, t) == 0));
            Motion steady = Sim(out t, 50f, 4f);
            Log("RULE steady retrieve: shy refuses, the rest wait  " + Check(F(perch, steady, t) == -1 && F(bass, steady, t) == 0 && F(pike, steady, t) == 0 && F(muskie, steady, t) == 0));
            Motion killed = Sim(out t, 50f, 4f, 0f, 0.5f);
            Log("RULE retrieve then stop: ambusher, shy and wary take, chaser waits  " + Check(F(pike, killed, t) == 1 && F(perch, killed, t) == 1 && F(muskie, killed, t) == 1 && F(bass, killed, t) == 0));
            Motion spedUp = Sim(out t, 50f, 4f, 100f, 0.3f);
            Log("RULE retrieve then speed up: chaser and wary take, shy refuses  " + Check(F(bass, spedUp, t) == 1 && F(muskie, spedUp, t) == 1 && F(perch, spedUp, t) == -1 && F(pike, spedUp, t) == 0));
            Motion ripped = Sim(out t, 0f, 3f, 170f, 0.25f, 0f, 0.4f);
            Log("RULE rip then hang: counted as a rip, ambusher and wary take  " + Check(ripped.RipId == 1 && !ripped.Ripping && F(pike, ripped, t) == 1 && F(muskie, ripped, t) == 1));
            Motion jigging = Sim(out t, 60f, 0.3f, 0f, 0.8f, 60f, 0.3f, 0f, 0.8f, 60f, 0.3f, 0f, 0.8f, 60f, 0.3f, 0f, 0.3f);
            Log("RULE regular jigging is not a change of pace, but the pause suits an ambusher  " + Check(F(muskie, jigging, t) == 0 && F(pike, jigging, t) == 1));
            Log("RULE steady feeder follows the action meter  " + Check(F(trout, steady, t, 0.7f) == 1 && F(trout, steady, t, 0.35f) == 0 && F(trout, steady, t, 0.1f) == -1));
        }

        static IEnumerable<int> ShowFishLine(string name, string text, int mood)
        {
            HudState hud = LureController.Hud;
            hud.FishText = text; hud.FishFit = mood; hud.FishStamp = Time.unscaledTime + 1.6f;
            foreach (int _ in Shot(name)) yield return 0;
        }

        static IEnumerable<int> FishLab(Game.Angler angler)
        {
            int dir = angler.FishingDir;
            float sweep = Settings.SweepFraction.Value / Math.Max(0.05f, Settings.Sensitivity.Value);
            HudState hud = LureController.Hud;

            // ---- what the mod makes of every species in the game, and which of them live here ----
            try
            {
                var lines = new List<string>();
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Game.FishData>()))
                {
                    var d = o.TryCast<Game.FishData>();
                    Species sp = d != null ? Species.For(d) : null;
                    if (sp != null) lines.Add(sp.Describe());
                }
                lines.Sort(StringComparer.Ordinal);
                foreach (string line in lines) Log("SPECIES " + line);
                var spawns = Game.Location.Instance.spawns;
                for (int i = 0; i < spawns.Count; i++)
                {
                    Game.LocationObjectSpawn spawn = spawns[i];
                    Game.FishData d = spawn != null ? spawn.FishData : null;
                    if (d == null) continue;
                    int count = 0;
                    var entries = spawn.Entries;
                    for (int k = 0; k < entries.Count; k++) if (entries[k].fish != null) count++;
                    Log(string.Format("HERE {0} x{1}  made for this lure: {2}", d.id, count, FishSense.Bonus(s_ctl, d)));
                }
            }
            catch (Exception e) { Log("species table FAILED: " + e); }

            try { RuleChecks(); }
            catch (Exception e) { Log("RULE checks FAILED: " + e); }

            // ---- splashdown: reel in and cast again, with every fish in earshot counted ----
            Log("STEP splashdown: reel in and recast");
            RodInput.VGrip = true; RodInput.VReel = true;
            foreach (int _ in Until(() => StateIs(angler, angler.States.RodIdle), 40f, "the lure to be reeled in")) yield return 0;
            RodInput.VGrip = false; RodInput.VReel = false;
            if (s_timedOut) yield break;
            foreach (int _ in Wait(1.5f)) yield return 0;
            s_ctl = null;
            RangeScale = 6f; ForcedLuck = 0.999f; // notice every fish, act on none
            int seen = FishSense.TotalSeen;
            RodInput.VGrip = true;
            foreach (int _ in Until(() => StateIs(angler, angler.States.RodAiming), 2f, "the aim pose")) yield return 0;
            foreach (int _ in Wait(0.5f)) yield return 0;
            foreach (int _ in Move(-dir * 0.8f, 0f, 0.4f)) yield return 0;
            foreach (int _ in Wait(0.1f)) yield return 0;
            foreach (int _ in Move(dir * 1.2f, 0f, 0.2f)) yield return 0;
            RodInput.VGrip = false;
            foreach (int _ in Until(() => s_ctl != null, 12f, "the cast lure to land")) yield return 0;
            if (s_ctl == null) yield break;
            foreach (int _ in Wait(2.5f)) yield return 0;
            Log(string.Format("splashdown noticed by {0} fish (reach x6)  {1}", FishSense.TotalSeen - seen, Check(s_ctl.Splashed && FishSense.TotalSeen > seen)));

            // ---- the fish read-out, in each of its looks ----
            foreach (int _ in ShowFishLine("fish-1-following", "CHASER FOLLOWING  -  speed up, make it run", 0)) yield return 0;
            foreach (int _ in ShowFishLine("fish-2-doing-it", "AMBUSHER FOLLOWING  -  pop it, then pause", 1)) yield return 0;
            hud.Tell("BUMPED IT  -  stop, let it take", -1, 1.6f);
            foreach (int _ in Shot("fish-3-bumped")) yield return 0;
            hud.Tell(FishSense.ChargingText, 1, 1.6f);
            foreach (int _ in Shot("fish-4-reaction")) yield return 0;
            HoldBitePrompt = true;
            hud.BiteText = "SMASH!  jerk the rod up"; hud.BiteUntil = Time.time + 1.6f;
            foreach (int _ in Shot("fish-5-smash")) yield return 0;
            hud.BiteUntil = -99f; HoldBitePrompt = false;

            // ---- a follower shows on the HUD; a bump, then two refusals, send it away ----
            Log("STEP follower: send a fish after the lure");
            ForcedLuck = -1f; RangeScale = 1f;
            Game.Fish fish = NearestIdle(out float dist);
            if (fish == null) { Log("no idle fish to work with"); yield break; }
            Species kind = Species.For(fish.Data);
            bool sent = FishSense.Charge(s_ctl, fish, s_ctl.Mem(fish.Pointer), false);
            Log(string.Format("sent {0} from {1:0} px -> {2}", kind != null ? kind.Describe() : "?", dist, sent));
            foreach (int _ in Wait(0.6f)) yield return 0;
            Log(string.Format("HUD reads \"{0}\" (fit {1})  {2}", hud.FishText, hud.FishFit,
                Check(sent && Time.unscaledTime - hud.FishStamp < 0.3f && kind != null && hud.FishText.StartsWith(kind.Label))));

            ForceNibble = true; ForcedFit = 0;
            foreach (int _ in Until(() => hud.NoteUntil > Time.time && hud.Note.StartsWith("BUMPED"), 30f, "the fish to reach the lure")) yield return 0;
            Log(string.Format("neutral lure, game said nibble -> \"{0}\", bite prompt {1}  {2}", hud.Note, hud.BiteUntil > Time.time,
                Check(!s_timedOut && hud.BiteUntil < Time.time && hud.NoteMood == 0)));
            for (int i = 0; i < 2 && !s_timedOut; i++)
            {
                hud.NoteUntil = -99f;
                ForceNibble = true; ForcedFit = -1;
                fish.States.Chase.m_nibbleRetryTimer = 0f;
                foreach (int _ in Until(() => hud.NoteUntil > Time.time, 20f, "the next bump")) yield return 0;
                Log(string.Format("lure doing the opposite, refusal {0} -> \"{1}\"", i + 1, hud.Note));
            }
            foreach (int _ in Wait(0.3f)) yield return 0;
            Log("after two refusals: turned away=" + hud.Note.StartsWith("TURNED AWAY") + " fish idle=" + fish.IsIdle + "  " + Check(hud.Note.StartsWith("TURNED AWAY") && fish.IsIdle));
            ForceNibble = false;

            // ---- the lure doing what the fish wants: it bites; left unanswered, it spits the lure ----
            Log("STEP bite: lure doing what the follower wants");
            foreach (int _ in Wait(1f)) yield return 0;
            fish = NearestIdle(out dist);
            if (fish == null) { Log("no idle fish to work with"); yield break; }
            kind = Species.For(fish.Data);
            FishSense.Charge(s_ctl, fish, s_ctl.Mem(fish.Pointer), false);
            ForceNibble = true; ForcedFit = 1;
            foreach (int _ in Until(() => hud.BiteUntil > Time.time, 30f, "a bite")) yield return 0;
            float hold = s_ctl.PendingUntil - s_ctl.PendingFrom;
            Log(string.Format("{0} [{1}] bit: \"{2}\", held for {3:0.00}s  {4}", kind.Id, kind.Temper, hud.BiteText, hold,
                Check(!s_timedOut && MathF.Abs(hold - Settings.HooksetWindow.Value * kind.BiteWindow) < 0.02f)));
            foreach (int _ in Until(() => hud.Note.StartsWith("TOO SLOW") && hud.NoteUntil > Time.time, 3f, "the unanswered bite to lapse")) yield return 0;
            Log("left unanswered -> \"" + hud.Note + "\"  " + Check(!s_timedOut));
            ForceNibble = false; ForcedFit = -2;
            fish.SetIdle();

            // ---- work the lure for real: the action meter fills, pulses fire, each species' sight of it is scaled ----
            bool reelIt = s_ctl.Analyzer is SteadyAnalyzer, leaveIt = s_ctl.Analyzer is StillAnalyzer;
            Log("STEP notice: " + (reelIt ? "reel" : leaveIt ? "rest" : "jig") + " the lure with attraction switched on");
            BlockAllAttraction = false;
            ForcedRoll = 0.99f; // any hookset this happens to make must miss: the lure is needed for the next step
            RodInput.VGrip = true;
            if (reelIt || leaveIt)
            {
                RodInput.VReel = reelIt;
                float until = Time.unscaledTime + 2.5f;
                while (Time.unscaledTime < until && s_ctl != null && s_ctl.Lure.State == Game.Lure.LureState.Fishing) yield return 0;
                RodInput.VReel = false;
            }
            else
            {
                for (int i = 0; i < 5; i++)
                {
                    foreach (int _ in Move(0f, sweep * 0.3f, 0.2f)) yield return 0;
                    foreach (int _ in Move(0f, -sweep * 0.3f, 0.25f)) yield return 0;
                    foreach (int _ in Wait(0.8f)) yield return 0;
                    if (s_ctl == null || s_ctl.Lure.State != Game.Lure.LureState.Fishing) break;
                }
            }
            BlockAllAttraction = true;
            if (s_ctl == null || s_ctl.Lure.State != Game.Lure.LureState.Fishing) { Log("the lure left the water during the notice step"); yield break; }
            Log(string.Format("notice step: meter {0:0.00}, energy {1:0.00}, species-specific sight applied {2} times  {3}",
                s_ctl.Quality, s_ctl.Motion.Energy, s_ctl.NoticeLogged, s_ctl.NoticeLogged > 0 ? "PASS" : "(none applied: no pulses, or no species here affected)"));
            foreach (int _ in Wait(2.5f)) yield return 0;

            // ---- rip the lure with every fish in reach sure to respond: predators charge, shy fish bolt ----
            Log("STEP reaction: rip the lure");
            try
            {
                // anything the jigging drew in would muddy the result: send it home first
                var following = s_ctl.Lure.m_attractedFish;
                for (int i = following.Count - 1; i >= 0; i--)
                {
                    Game.Fish f = following[i];
                    if (f != null && !f.IsIdle && !f.IsHooked) f.SetIdle();
                }
            }
            catch (Exception e) { Log("could not call off followers: " + e.Message); }
            foreach (int _ in Wait(0.3f)) yield return 0;
            int reactions = FishSense.TotalReactions, spooked = FishSense.TotalSpooked;
            seen = FishSense.TotalSeen;
            RangeScale = 6f; ForcedLuck = 0f;
            foreach (int _ in Move(0f, sweep * 0.8f, 0.08f)) yield return 0;
            foreach (int _ in Wait(0.3f)) yield return 0;
            RangeScale = 1f; ForcedLuck = -1f;
            foreach (int _ in Move(0f, -sweep * 0.8f, 0.25f)) yield return 0;
            Log(string.Format("rip (id {0}, peak {1:0} px/s): {2} fish in reach, {3} charged, {4} bolted  {5}", s_ctl.Motion.RipId, s_ctl.Motion.RipPeak,
                FishSense.TotalSeen - seen, FishSense.TotalReactions - reactions, FishSense.TotalSpooked - spooked,
                Check(s_ctl.Motion.RipId > 0 && FishSense.TotalSeen > seen)));

            if (FishSense.TotalReactions == reactions)
            {
                // nothing here is the reacting kind (or the right size for this lure): play the charge out with whatever is nearest
                fish = NearestIdle(out dist);
                if (fish == null) { Log("no idle fish to work with"); yield break; }
                bool ok = FishSense.Charge(s_ctl, fish, s_ctl.Mem(fish.Pointer), true);
                Log("no natural reaction here; sent " + fish.Data.id + " charging by hand -> " + ok);
            }
            foreach (int _ in Wait(0.2f)) yield return 0;
            Log("HUD reads \"" + (hud.NoteUntil > Time.time ? hud.Note : hud.FishText) + "\"  " + Check(hud.Note == FishSense.ChargingText));
            ForcedRoll = 0f;
            foreach (int _ in Until(() => hud.BiteUntil > Time.time, 12f, "the reaction strike")) yield return 0;
            if (s_timedOut) { Log("the charging fish never reached the lure"); yield break; }
            float smashHold = s_ctl.PendingUntil - s_ctl.PendingFrom;
            Log(string.Format("reaction strike: \"{0}\", held for {1:0.00}s  {2}", hud.BiteText, smashHold,
                Check(hud.BiteText.StartsWith("SMASH") && MathF.Abs(smashHold - Settings.HooksetWindow.Value * 0.7f) < 0.02f)));
            foreach (int _ in Wait(0.12f)) yield return 0;
            foreach (int _ in Move(0f, sweep * 0.5f, 0.08f)) yield return 0;
            foreach (int _ in Wait(0.5f)) yield return 0;
            bool hooked = s_ctl != null && s_ctl.Lure.State == Game.Lure.LureState.Hooked;
            Log("hookset on the reaction strike: hooked=" + hooked + "  " + Check(hooked));
            ForcedRoll = -1f;
            RodInput.VGrip = false;
            foreach (int _ in Wait(1f)) yield return 0;
        }

        // ---------- fight lab: hook a fish and work the drag, the reel and the rod against it ----------

        static HashSet<IntPtr> SizeSet(Il2CppSystem.Collections.Generic.List<Game.FishSizeData> list)
        {
            var set = new HashSet<IntPtr>();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    Game.FishSizeData size = list[i];
                    if (size != null) set.Add(size.Pointer);
                }
            return set;
        }

        /// <summary>
        /// The heaviest idle fish within reach that this tackle can actually land (a size the lure attracts and the rod
        /// can hold). A small fish on a strong rod is simply winched in and tests nothing.
        /// </summary>
        static Game.Fish HeaviestLandable(float reach, out float dist)
        {
            HashSet<IntPtr> good = SizeSet(s_ctl.Lure.attractSizes), tooBig = SizeSet(s_ctl.Lure.tooBigSizes);
            Game.Fish best = null; float bestWeight = -1f;
            Vector2 lp = s_ctl.Body.position;
            dist = 0f;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Game.Fish>()))
            {
                var f = o.TryCast<Game.Fish>();
                if (f == null || !f.gameObject.activeInHierarchy) continue;
                try
                {
                    Game.FishSizeData size = f.Size;
                    if (!f.IsIdle || size == null || !good.Contains(size.Pointer) || tooBig.Contains(size.Pointer)) continue;
                    Vector2 fp = f.Position;
                    float d = MathF.Sqrt((fp.x - lp.x) * (fp.x - lp.x) + (fp.y - lp.y) * (fp.y - lp.y));
                    if (d > reach || f.Weight <= bestWeight) continue;
                    bestWeight = f.Weight; best = f; dist = d;
                }
                catch (Exception) { }
            }
            return best;
        }

        static float s_keepDrag = -1f;

        static bool Hooked() => s_ctl != null && s_ctl.Lure.State == Game.Lure.LureState.Hooked;

        /// <summary>How far the line stretches before it is at its limit, in px.</summary>
        static float MaxStretch(Game.Rod rod)
        {
            return rod.StrengthData.weight * Game.FishingLineConfig.Instance.springStrengthMultiplier / rod.Data.response.lineSpringConstant;
        }

        /// <summary>Winds the line to just short of the fish, so whatever happens next starts from a known load.</summary>
        static void Stretch(Game.Rod rod, float px)
        {
            rod.LineLength = rod.m_distanceToHook - px;
        }

        /// <summary>
        /// Stretches the line to 60% of what it can take and watches a fifth of a second: how much line did the reel give?
        /// </summary>
        static IEnumerable<int> SlipCase(string what, float drag, bool reel, bool vanilla, float[] gave, string shot = null)
        {
            Game.Rod rod = s_ctl.Rod;
            DragOverride = drag; VanillaLine = vanilla;
            RodInput.VReel = reel;
            foreach (int _ in Wait(0.5f)) yield return 0; // let the reel come up to speed (or stop)
            if (!Hooked()) yield break;
            Stretch(rod, 0.6f * MaxStretch(rod));
            float line0 = rod.LineLength, peak = 0f;
            float until = Time.unscaledTime + 0.2f;
            bool slipped = false;
            while (Time.unscaledTime < until && Hooked())
            {
                if (Fight.Tension > peak) peak = Fight.Tension;
                slipped |= Fight.Slipping;
                yield return 0;
            }
            gave[0] = rod.LineLength - line0;
            if (shot != null) Log("SHOT " + shot);
            RodInput.VReel = false; VanillaLine = false;
            Log(string.Format("SLIP {0}: line stretched to 60% of its strength -> reel gave {1:0.0} px in 0.2s (peak tension {2:0.00}, drag slipping seen={3})", what, gave[0], peak, slipped));
        }

        /// <summary>
        /// One heave of the rod with the line kept loaded to a known share of its strength while it happens, so the
        /// result does not hang on where the fish is: heaving toward a fish that has got behind the boat slackens the line.
        /// </summary>
        static IEnumerable<int> Heave(float sweep, float load, float lowerTime)
        {
            Game.Rod rod = s_ctl.Rod;
            float stretch = load * MaxStretch(rod);
            IEnumerator<int> lift = Move(0f, sweep * 0.5f, 0.1f).GetEnumerator();
            float until = Time.unscaledTime + 0.32f;
            while (Time.unscaledTime < until && Hooked())
            {
                lift.MoveNext();
                float taut = rod.m_distanceToHook - stretch;
                if (rod.LineLength > taut) rod.LineLength = taut;
                yield return 0;
            }
            foreach (int _ in Move(0f, -sweep * 0.5f, lowerTime)) yield return 0;
        }
        static IEnumerable<int> FightLab(Game.Angler angler)
        {
            float sweep = Settings.SweepFraction.Value / Math.Max(0.05f, Settings.Sensitivity.Value);
            HudState hud = LureController.Hud;
            Game.Rod rod = s_ctl.Rod;
            s_keepDrag = Settings.Drag.Value;

            // ---- the wheel turns the drag ----
            Log("STEP drag: mouse wheel");
            Settings.Drag.Value = 0.5f;
            RodInput.VWheel = 2;
            foreach (int _ in Wait(0.1f)) yield return 0;
            float up = Fight.Drag;
            RodInput.VWheel = -3;
            foreach (int _ in Wait(0.1f)) yield return 0;
            Log(string.Format("wheel up 2: 50% -> {0:0}%, then down 3: -> {1:0}%  {2}", up * 100f, Fight.Drag * 100f,
                Check(MathF.Abs(up - 0.6f) < 0.001f && MathF.Abs(Fight.Drag - 0.45f) < 0.001f)));
            RodInput.VWheel = 99;
            foreach (int _ in Wait(0.1f)) yield return 0;
            float top = Fight.Drag;
            RodInput.VWheel = -99;
            foreach (int _ in Wait(0.1f)) yield return 0;
            Log(string.Format("wheel limits: {0:0}% .. {1:0}%  {2}", Fight.Drag * 100f, top * 100f, Check(MathF.Abs(top - Fight.DragMax) < 0.001f && MathF.Abs(Fight.Drag - Fight.DragMin) < 0.001f)));
            Settings.Drag.Value = s_keepDrag;
            DragOverride = 0.5f;
            foreach (int _ in Shot("fight-0-drag-shown-while-fishing")) yield return 0;

            // ---- hook the heaviest thing this tackle can land ----
            Log("STEP hook a fish");
            Game.RodData.Response response = rod.Data.response;
            float ownRatio = response.startDragRatio;
            Game.Fish fish = HeaviestLandable(450f, out float dist);
            if (fish == null) { fish = NearestIdle(out dist); Log("no fish of a landable size is idle within reach; using the nearest of any size"); }
            if (fish == null) { Log("no idle fish to work with"); yield break; }
            fish.States.Chase.m_chaseTimer = 60f; // it may have a long way to come
            ForcedRoll = 0f; ForceNibble = true; ForcedFit = 1;
            RodInput.VGrip = true;
            FishSense.Charge(s_ctl, fish, s_ctl.Mem(fish.Pointer), false);
            Log(string.Format("sent {0} (size {1}, weight {2:0.0}) from {3:0} px", fish.Data.id, fish.Size.id, fish.Weight, dist));
            foreach (int _ in Until(() => hud.BiteUntil > Time.time, 30f, "a bite")) yield return 0;
            if (s_timedOut) yield break;
            foreach (int _ in Wait(0.15f)) yield return 0;
            foreach (int _ in Move(0f, sweep * 0.5f, 0.08f)) yield return 0;
            foreach (int _ in Wait(0.5f)) yield return 0;
            ForcedRoll = -1f; ForceNibble = false; ForcedFit = -2;
            Log("hooked=" + Hooked() + " fight active=" + Fight.Active + "  " + Check(Hooked() && Fight.Active));
            if (!Hooked()) yield break;
            foreach (int _ in Move(0f, -sweep * 0.7f, 0.3f)) yield return 0;
            Game.Fish.HookedState state = fish.States.Hooked;
            Log(string.Format("fish has {0:0.0}s of fight ({1:0}% left), too big={2}; rod's own drag {3:0}%, line takes {4:0} px of stretch, gives {5:0}% of the excess per tick",
                state.m_fightDuration, Fight.Stamina * 100f, Fight.TooBig, ownRatio * 100f, MaxStretch(rod), (1f - response.lineDragConstant) * 100f));

            // ---- the drag: the same load on the line, four ways ----
            Log("STEP drag gives line instead of letting the load build");
            float[] loose = new float[1], locked = new float[1], reeling = new float[1], vanilla = new float[1];
            foreach (int _ in SlipCase("drag 20%, hands off the reel", 0.2f, false, false, loose, "fight-1-loose-drag-giving-line")) yield return 0;
            foreach (int _ in SlipCase("drag 100%, hands off the reel", 1f, false, false, locked)) yield return 0;
            foreach (int _ in SlipCase("drag 20%, reeling", 0.2f, true, false, reeling)) yield return 0;
            foreach (int _ in SlipCase("base game, reeling", 0.2f, true, true, vanilla)) yield return 0;
            if (!Hooked()) { Log("the fish is no longer on"); yield break; }
            Log(string.Format("loose drag gives line ({0:0.0} px), a locked one does not ({1:0.0} px)  {2}", loose[0], locked[0], Check(loose[0] > 3f && locked[0] < 1f)));
            Log(string.Format("it still gives while you reel ({0:0.0} px); the base game's does not ({1:0.0} px)  {2}", reeling[0], vanilla[0], Check(reeling[0] > vanilla[0] + 3f && vanilla[0] < 1f)));
            Log(string.Format("the rod's own drag figure is untouched between updates: {0:0.00}  {1}", response.startDragRatio, Check(MathF.Abs(response.startDragRatio - ownRatio) < 0.0001f)));

            // ---- heaving the rod on a loaded line wears the fish down ----
            Log("STEP pump the rod three times, a second apart");
            DragOverride = 0.7f;
            foreach (int _ in Wait(1.2f)) yield return 0;
            int pumps = Fight.Pumps;
            float start = Fight.Stamina, sum = 0f, least = 99f;
            for (int i = 0; i < 3 && Hooked(); i++)
            {
                float before = Fight.Stamina;
                foreach (int _ in Heave(sweep, 0.5f, 0.3f)) yield return 0;
                if (i == 1) Log("SHOT fight-3-pump");
                foreach (int _ in Wait(0.6f)) yield return 0;
                sum += Fight.LastPumpSeconds; least = Math.Min(least, Fight.LastPumpSeconds);
                Log(string.Format("pump {0}: line load {1:0.00}, fish {2:0.0}% -> {3:0.0}% ({4:0.0}s of fight), HUD \"{5}\"", i + 1, Fight.LastPumpLoad, before * 100f, Fight.Stamina * 100f, Fight.LastPumpSeconds, hud.Note));
            }
            if (!Hooked()) { Log("the fish is no longer on"); yield break; }
            float dropped = (start - Fight.Stamina) * state.m_fightDuration;
            Log(string.Format("three pumps counted {0}, took {1:0.0}s between them (least {2:0.0}s); the fish lost {3:0.0}s in all  {4}", Fight.Pumps - pumps, sum, least, dropped,
                Check(Fight.Pumps - pumps == 3 && least > 1f && dropped >= sum)));

            // a second pump straight after the first counts for much less; with the dice fixed, a running fish is turned
            ForcedLuck = 0f;
            int turns = Fight.Turns;
            foreach (int _ in Wait(0.6f)) yield return 0;
            foreach (int _ in Heave(sweep, 0.5f, 0.12f)) yield return 0;
            float first = Fight.LastPumpSeconds;
            foreach (int _ in Heave(sweep, 0.5f, 0.3f)) yield return 0;
            float second = Fight.LastPumpSeconds;
            ForcedLuck = -1f;
            Log(string.Format("two pumps {0:0.0}s then {1:0.0}s: the hurried one counted for less  {2}", first, second, Check(first > 1f && second < first * 0.75f)));
            Log("with the dice fixed: turned the fish " + (Fight.Turns - turns) + " time(s) (only a fish that is running away can be turned); HUD \"" + hud.Note + "\"");
            if (!Hooked()) { Log("the fish is no longer on"); yield break; }
            // ---- worn out, then landed ----
            Log("STEP wear it out and land it");
            foreach (int _ in Wait(1.8f)) yield return 0; // let the last pump's remark clear
            state.m_fightTimer = 0.3f;
            foreach (int _ in Until(() => hud.Note.StartsWith("WORN OUT"), 4f, "the fish to tire")) yield return 0;
            Log(string.Format("fish {0:0}% left, HUD \"{1}\"  {2}", Fight.Stamina * 100f, hud.Note, Check(!s_timedOut && hud.Note.StartsWith("WORN OUT"))));
            Log("SHOT fight-4-worn-out");
            DragOverride = 0.2f; // the lightest drag: a beaten fish must still come to hand on it
            float landFrom = Time.unscaledTime, lineFrom = rod.LineLength;
            float give = Time.unscaledTime + 70f;
            while (Hooked() && Time.unscaledTime < give)
            {
                RodInput.VReel = Fight.Strain < 0.4f;
                yield return 0;
            }
            RodInput.VReel = false;
            foreach (int _ in Wait(0.5f)) yield return 0;
            Game.Lure.LureState end = s_ctl.Lure.State;
            Log("after reeling in: lure state " + end + ", fight active=" + Fight.Active + "  " + Check(end == Game.Lure.LureState.Caught && !Fight.Active));
            float landed = Time.unscaledTime - landFrom - 0.5f;
            Log(string.Format("landing a worn-out fish on a 20% drag: {0:0} px of line in {1:0.0}s  {2}", lineFrom, landed, Check(end == Game.Lure.LureState.Caught && landed < lineFrom / 25f + 3f)));
            Log(string.Format("rod's own drag figure after the fight: {0:0.00}  {1}", response.startDragRatio, Check(MathF.Abs(response.startDragRatio - ownRatio) < 0.0001f)));
            DragOverride = -1f;
            RodInput.VGrip = false;
            foreach (int _ in Wait(1f)) yield return 0;
        }

        // ---------- pop lab: a topwater lure worked as a popper ----------

        /// <summary>Where the lure is in the game window, in pixels from its top-left corner.</summary>
        static string ScreenSpot()
        {
            try
            {
                if (!Hud.LureOnScreen(out float x, out float y, out float w)) return "?";
                float k = Screen.height / 1080f;
                return string.Format("{0:0},{1:0} of {2}x{3}", x * k, Screen.height - y * k, Screen.width, Screen.height);
            }
            catch (Exception e) { return e.GetType().Name; }
        }

        static IEnumerable<int> PopLab(Game.Angler angler)
        {
            int dir = angler.FishingDir;
            float sweep = Settings.SweepFraction.Value / Math.Max(0.05f, Settings.Sensitivity.Value);
            float range = Settings.DipDegrees.Value + Settings.RaiseDegrees.Value;

            if (s_ctl.TypeId != "topwater")
            {
                Log("POP reel in to put a topwater lure on");
                RodInput.VGrip = true; RodInput.VReel = true;
                foreach (int _ in Until(() => StateIs(angler, angler.States.RodIdle), 40f, "the lure to be reeled in")) yield return 0;
                RodInput.VGrip = false; RodInput.VReel = false;
                if (s_timedOut) yield break;
                foreach (int _ in Wait(1f)) yield return 0;
                Game.LureData lure = null;
                try { lure = Game.Database.FindLure("topwater01"); } catch (Exception e) { Log("POP FindLure failed: " + e.Message); }
                if (lure == null) { Log("POP topwater01 not found"); yield break; }
                angler.SetupTackle(angler.rod.Data, lure);
                foreach (int _ in Wait(1.5f)) yield return 0;
                s_ctl = null;
                RodInput.VGrip = true;
                foreach (int _ in Until(() => StateIs(angler, angler.States.RodAiming), 2f, "the aim pose")) yield return 0;
                foreach (int _ in Wait(0.5f)) yield return 0;
                foreach (int _ in Move(-dir * 0.8f, 0f, 0.4f)) yield return 0;
                foreach (int _ in Wait(0.1f)) yield return 0;
                foreach (int _ in Move(dir * 1.2f, 0f, 0.2f)) yield return 0;
                RodInput.VGrip = false;
                foreach (int _ in Until(() => s_ctl != null, 12f, "the cast lure to land")) yield return 0;
                if (s_ctl == null) yield break;
            }
            Log("POP lure " + s_ctl.LureId + " (" + s_ctl.TypeId + "), popper handling=" + s_ctl.Popper + ", cast power " + Casting.LastPower.ToString("0.00"));
            foreach (int _ in Wait(3.5f)) yield return 0;
            Rigidbody2D body = s_ctl.Body;
            Game.Rod rod = s_ctl.Rod;
            Log(string.Format("at rest: lure at ({0:0.0},{1:0.0}) speed {2:0.0}, line {3:0} px, slack {4:0.0}, in water={5}  {6}", body.position.x, body.position.y,
                body.linearVelocity.magnitude, rod.LineLength, rod.LineLength - rod.DistanceToLure, s_ctl.Lure.m_inWater, Check(s_ctl.Popper && s_ctl.Lure.m_inWater && Math.Abs(body.position.y - 0.5f) < 1.5f)));

            // ---- how does the game frame a surface lure once idle mode lets go of the camera? ----
            Log("lure on screen under the idle camera: " + ScreenSpot());
            s_letIdleRun = true;
            try { Game.IdleManager.UserInteracted(); } catch (Exception e) { Log("UserInteracted failed: " + e.Message); }
            foreach (int _ in Wait(3f)) yield return 0;
            s_letIdleRun = false;
            bool idleNow = true;
            try { idleNow = Game.IdleManager.IsActive; } catch (Exception) { }
            Log("after waking the game from idle mode: idle active=" + idleNow + ", angler " + Name(angler.State) + ", lure state " + s_ctl.Lure.State + ", lure on screen: " + ScreenSpot());
            if (s_ctl.Lure.State != Game.Lure.LureState.Fishing) { Log("POP the lure is no longer fishing after leaving idle mode"); yield break; }

            // ---- what the game has to make a pop out of ----
            HideHud = true;
            // the game's own splash first, as the reference for where and how big a splash is drawn
            try { s_ctl.Lure.PlaySplashEffect(); } catch (Exception e) { Log("vanilla splash failed: " + e.Message); }
            foreach (int _ in Wait(0.12f)) yield return 0;
            Log("SHOT pop-ref-vanilla");
            Log("AT pop-ref-vanilla " + ScreenSpot());
            foreach (int _ in Wait(1.3f)) yield return 0;
            foreach (var probe in new[] { new float[] { 0f, 1f }, new float[] { 20f, 1f }, new float[] { -20f, 1f }, new float[] { 0f, 0.5f }, new float[] { 0f, 1f }, new float[] { 0f, 1f } })
            {
                string about = "?";
                string tag = "pop-fx-dx" + (int)probe[0] + "-size" + (int)(probe[1] * 100f);
                try { about = Popper.Preview(s_ctl, "lure", probe[1], probe[1] >= 1f && probe[0] == 0f ? 1f : 0f, probe[0]); } catch (Exception e) { about = "FAILED: " + e; }
                foreach (int _ in Wait(0.12f)) yield return 0;
                Log("SHOT " + tag);
                Log("AT " + tag + " " + ScreenSpot());
                try { Log("INSPECT " + Popper.Inspect(s_ctl.Lure)); } catch (Exception e) { Log("INSPECT failed: " + e); }
                Log("EFFECT " + about);
                foreach (int _ in Wait(1.1f)) yield return 0;
            }
            foreach (var fx in new[] { "fish-s", "fish-m" }) Log("EFFECT " + Popper.Preview(s_ctl, fx, 1f, 0f));
            foreach (int _ in Wait(1.1f)) yield return 0;            Log("effects: " + Popper.SplashesShown + " splashes shown, " + Popper.SoundsPlayed + " sounds started, fault \"" + Popper.LastFault + "\"  " +
                Check(Popper.SplashesShown >= 8 && Popper.SoundsPlayed >= 1 && Popper.LastFault.Length == 0));

            HideHud = false;
            foreach (int _ in Wait(1.2f)) yield return 0;
            {
                bool seen = Hud.LureOnScreen(out float cx, out float cy, out float cw);
                Log(string.Format("HUD: lure at canvas ({0:0},{1:0}) of {2:0} wide, HUD stepped aside by {3:0}  (see the next shot)", cx, cy, cw, Hud.Aside));
                Log("SHOT pop-hud-steps-aside");
                Log("AT pop-hud-steps-aside " + ScreenSpot());
            }

            // ---- twitches from tiny to a full sweep: how far does the lure go, and does it stop? ----
            RodInput.VGrip = true;
            foreach (int _ in Wait(1f)) yield return 0;
            float[] twitches = { 5f, 10f, 18f, 30f, 55f };
            float[] moved = new float[twitches.Length];
            bool stayedIn = true, stopped = true, onePopEach = true;
            for (int i = 0; i < twitches.Length; i++)
            {
                float up = sweep * twitches[i] / range;
                Vector2 from = body.position, tipFrom = rod.tip.position;
                int pops = Popper.Pops;
                float peak = 0f, tipMoved = 0f, highest = from.y;
                var act = Sequence(Move(0f, up, 0.09f), Wait(0.5f)).GetEnumerator();
                while (act.MoveNext())
                {
                    Vector2 p = body.position, tp = rod.tip.position, v = body.linearVelocity;
                    peak = Math.Max(peak, MathF.Abs(v.x));
                    tipMoved = Math.Max(tipMoved, MathF.Sqrt((tp.x - tipFrom.x) * (tp.x - tipFrom.x) + (tp.y - tipFrom.y) * (tp.y - tipFrom.y)));
                    highest = Math.Max(highest, p.y);
                    if (!s_ctl.Lure.m_inWater) stayedIn = false;
                    yield return 0;
                }
                moved[i] = MathF.Abs(body.position.x - from.x);
                float coast = MathF.Abs(body.linearVelocity.x);
                if (coast > 6f) stopped = false;
                int got = Popper.Pops - pops;
                if (twitches[i] >= 10f && got != 1) onePopEach = false;
                Log(string.Format("POP twitch {0,2:0} deg in 0.09s: rod tip moved {1,5:0.0} px -> lure moved {2,5:0.0} px, top speed {3,5:0.0} px/s, half a second later {4:0.0} px/s, highest y {5:0.0}, pops {6} (strength {7:0.00})",
                    twitches[i], tipMoved, moved[i], peak, coast, highest, got, got > 0 ? Popper.LastStrength : 0f));
                if (i == 2) { Log("SHOT pop-3-medium-twitch"); Log("AT pop-3-medium-twitch " + ScreenSpot()); }
                foreach (int _ in Move(0f, -up, 0.3f)) yield return 0;
                foreach (int _ in Wait(1.0f)) yield return 0;
            }
            Log(string.Format("a bigger twitch pops it further ({0:0.0} < {1:0.0} < {2:0.0} < {3:0.0} px), a small one stays small  {4}", moved[1], moved[2], moved[3], moved[4],
                Check(moved[1] <= 8f && moved[2] <= 14f && moved[2] < moved[3] && moved[3] < moved[4])));
            Log("even a full sweep does not send it skating: " + moved[4].ToString("0.0") + " px  " + Check(moved[4] < 45f));
            Log("it stayed in the water, stopped dead after each pop, and each twitch of 10 degrees or more was one pop  " + Check(stayedIn && stopped && onePopEach));

            // ---- pop, pause, pop: the action meter ----
            float q0 = s_ctl.Quality;
            for (int i = 0; i < 5; i++)
            {
                float up = sweep * 16f / range;
                foreach (int _ in Move(0f, up, 0.08f)) yield return 0;
                foreach (int _ in Wait(0.15f)) yield return 0;
                if (i == 3) { Log("SHOT pop-4-rhythm"); Log("AT pop-4-rhythm " + ScreenSpot()); }
                foreach (int _ in Move(0f, -up, 0.25f)) yield return 0;
                foreach (int _ in Wait(0.6f)) yield return 0;
            }
            Log(string.Format("five pops a second apart: action meter {0:0.00} -> {1:0.00}  {2}", q0, s_ctl.Quality, Check(s_ctl.Quality >= 0.7f)));

            // ---- reeling still brings it in, on the surface ----
            Vector2 before = body.position;
            bool inWater = true;
            RodInput.VReel = true;
            float until = Time.unscaledTime + 1.5f;
            while (Time.unscaledTime < until) { if (!s_ctl.Lure.m_inWater) inWater = false; yield return 0; }
            RodInput.VReel = false;
            foreach (int _ in Wait(0.6f)) yield return 0;
            Log(string.Format("reeling for 1.5s brought it {0:0} px closer, on the surface={1}  {2}", MathF.Abs(body.position.x - before.x), inWater,
                Check(MathF.Abs(body.position.x - before.x) > 30f && inWater)));
            Log("effects after the pops: " + Popper.SplashesShown + " splashes, " + Popper.SoundsPlayed + " sounds, fault \"" + Popper.LastFault + "\"  " + Check(Popper.LastFault.Length == 0));
            RodInput.VGrip = false;
            foreach (int _ in Wait(1f)) yield return 0;
        }
        // ---------- lure lab: put one lure through its paces and log exactly what it does ----------

        static float s_labStart;

        static void Snap(string phase)
        {
            try
            {
                Game.Lure lure = s_ctl.Lure; Game.Rod rod = s_ctl.Rod;
                Rigidbody2D rb = s_ctl.Body;
                Vector2 p = rb.position, v = rb.linearVelocity; Vector3 tip = rod.TipPos;
                var hinge = lure.m_hingeJoint; var dj = lure.DistanceJoint;
                Log(string.Format("LAB {0,5:0.0}s {1,-10} {2,-8} pos({3,6:0.0},{4,6:0.0}) vel({5,5:0.0},{6,5:0.0}) water={7} out={8} snag={9} line={10,5:0.0} dist={11,5:0.0} slack={12,5:0.0} spring={13,6:0} reel={14,5:0.00} spool={15} rod={16,4:0} tip({17:0},{18:0}) hinge={19} dj={20} damp={21:0.0} body={22}",
                    Time.unscaledTime - s_labStart, phase, lure.State, p.x, p.y, v.x, v.y, lure.m_inWater ? 1 : 0, lure.IsOutOfWater ? 1 : 0, lure.IsSnagged ? 1 : 0,
                    rod.LineLength, rod.DistanceToLure, rod.LineLength - rod.DistanceToLure, rod.SpringForce, rod.RetrievePower, rod.Spooling ? 1 : 0,
                    RodPose.Current, tip.x, tip.y, hinge != null && hinge.enabled ? 1 : 0, dj != null && dj.enabled ? 1 : 0, rb.linearDamping, rb.bodyType));
            }
            catch (Exception e) { Log("LAB snap failed: " + e.Message); }
        }

        /// <summary>Runs <paramref name="body"/> while logging a snapshot five times a second.</summary>
        static IEnumerable<int> Watch(string phase, IEnumerable<int> body)
        {
            float next = 0f;
            foreach (int _ in body)
            {
                if (Time.unscaledTime >= next) { next = Time.unscaledTime + 0.2f; if (s_ctl != null) Snap(phase); }
                yield return 0;
            }
        }

        static IEnumerable<int> Sequence(params IEnumerable<int>[] parts)
        {
            foreach (var part in parts) foreach (int _ in part) yield return 0;
        }

        static IEnumerable<int> Lab(Game.Angler angler, string lureId)
        {
            int dir = angler.FishingDir;
            Log("LAB reel in to change tackle to " + lureId);
            RodInput.VGrip = true; RodInput.VReel = true;
            foreach (int _ in Until(() => StateIs(angler, angler.States.RodIdle), 40f, "the lure to be reeled in")) yield return 0;
            RodInput.VGrip = false; RodInput.VReel = false;
            if (s_timedOut) yield break;
            foreach (int _ in Wait(1f)) yield return 0;

            Game.LureData lure = null;
            try { lure = Game.Database.FindLure(lureId); } catch (Exception e) { Log("LAB FindLure failed: " + e.Message); }
            if (lure == null) { Log("LAB lure not found: " + lureId); yield break; }
            Log("LAB lure " + lureId + " owned=" + lure.IsOwned + " density=" + lure.density + " techniques=" + (lure.techniques != null ? lure.techniques.Length : 0));
            angler.SetupTackle(angler.rod.Data, lure);
            foreach (int _ in Wait(1.5f)) yield return 0;

            s_ctl = null;
            RodInput.VGrip = true;
            foreach (int _ in Until(() => StateIs(angler, angler.States.RodAiming), 2f, "the aim pose")) yield return 0;
            foreach (int _ in Wait(0.5f)) yield return 0;
            foreach (int _ in Move(-dir * 0.8f, 0f, 0.4f)) yield return 0;
            foreach (int _ in Wait(0.1f)) yield return 0;
            foreach (int _ in Move(dir * 1.2f, 0f, 0.2f)) yield return 0;
            RodInput.VGrip = false;
            foreach (int _ in Until(() => s_ctl != null, 12f, "the cast lure to land")) yield return 0;
            if (s_ctl == null) yield break;
            Log("LAB cast power " + Casting.LastPower.ToString("0.00") + " lure=" + s_ctl.LureId);
            s_labStart = Time.unscaledTime;
            float sweep = Settings.SweepFraction.Value / Math.Max(0.05f, Settings.Sensitivity.Value);

            foreach (int _ in Watch("rest", Wait(3f))) yield return 0;

            // how far does the lure travel for a given movement of the rod?
            RodInput.VGrip = true;
            foreach (int _ in Wait(1.2f)) yield return 0;
            foreach (var kind in new[] { "gentle-lift", "brisk-jig", "hard-yank", "hard-yank", "quick-twitch" })
            {
                float amount = kind == "quick-twitch" ? 0.2f : kind == "gentle-lift" ? 0.6f : kind == "brisk-jig" ? 0.6f : 0.8f;
                float upTime = kind == "gentle-lift" ? 0.9f : kind == "brisk-jig" ? 0.25f : 0.08f;
                Vector2 from = s_ctl.Body.position, tipFrom = s_ctl.Rod.tip.position;
                float best = 0f, tipBest = 0f, peak = 0f, until = Time.unscaledTime + upTime + 0.25f + 0.2f;
                var move = Sequence(Move(0f, sweep * amount, upTime), Wait(0.25f), Move(0f, -sweep * amount, 0.2f), Wait(1.3f)).GetEnumerator();
                float t0 = Time.unscaledTime;
                while (move.MoveNext())
                {
                    if (kind == "hard-yank" && Time.unscaledTime - t0 < 0.55f)
                    {
                        Game.Rod r = s_ctl.Rod; Vector2 lp = s_ctl.Body.position, lv = s_ctl.Body.linearVelocity, tp = r.tip.position;
                        Log(string.Format("YANK {0:0.000}s rod={1,5:0.0} yank={2:0.00} tip({3,6:0.0},{4,5:0.0}) lure({5,6:0.0},{6,6:0.0}) vel({7,6:0.0},{8,6:0.0}) line={9,6:0.0} dist={10,6:0.0} stretch={11,6:0.0} spring={12,6:0} water={13} state={14}",
                            Time.unscaledTime - t0, RodPose.Current, RodPose.Yank, tp.x, tp.y, lp.x, lp.y, lv.x, lv.y, r.LineLength, r.DistanceToLure,
                            r.DistanceToLure - r.LineLength, r.SpringForce, s_ctl.Lure.m_inWater ? 1 : 0, s_ctl.Lure.State));
                    }
                    if (Time.unscaledTime <= until)
                    {
                        Vector2 p = s_ctl.Body.position, t = s_ctl.Rod.tip.position, v = s_ctl.Body.linearVelocity;
                        best = Math.Max(best, MathF.Sqrt((p.x - from.x) * (p.x - from.x) + (p.y - from.y) * (p.y - from.y)));
                        tipBest = Math.Max(tipBest, MathF.Sqrt((t.x - tipFrom.x) * (t.x - tipFrom.x) + (t.y - tipFrom.y) * (t.y - tipFrom.y)));
                        peak = Math.Max(peak, MathF.Sqrt(v.x * v.x + v.y * v.y));
                    }
                    yield return 0;
                }
                Log(string.Format("LAB MOVE {0,-12} rod tip moved {1,5:0.0} px in {2:0.00}s -> lure moved {3,5:0.0} px, top speed {4,5:0.0} px/s", kind, tipBest, upTime, best, peak));
            }

            for (int i = 0; i < 6; i++)
                foreach (int _ in Watch("twitch", Sequence(Move(0f, sweep * 0.25f, 0.06f), Move(0f, -sweep * 0.25f, 0.15f), Wait(0.3f)))) yield return 0;

            RodInput.VReel = true;
            foreach (int _ in Watch("grip+reel", Wait(2.5f))) yield return 0;
            RodInput.VReel = false;
            foreach (int _ in Watch("grip-hold", Wait(1.5f))) yield return 0;
            RodInput.VGrip = false;
            foreach (int _ in Watch("released", Wait(2.5f))) yield return 0;

            RodInput.VGrip = true;
            foreach (int _ in Watch("raise", Sequence(Move(0f, sweep * 0.8f, 0.4f), Wait(2f)))) yield return 0;
            foreach (int _ in Watch("lower", Sequence(Move(0f, -sweep * 1.1f, 0.4f), Wait(2f)))) yield return 0;
            RodInput.VGrip = false;
            foreach (int _ in Watch("released2", Wait(2f))) yield return 0;

            RodInput.VGrip = true;
            foreach (int _ in Wait(0.08f)) yield return 0;
            RodInput.VGrip = false;
            foreach (int _ in Watch("bail-open", Wait(3f))) yield return 0;
            RodInput.VReel = true;
            foreach (int _ in Wait(0.1f)) yield return 0;
            RodInput.VReel = false;
            RodInput.VGrip = true; RodInput.VReel = true;
            foreach (int _ in Watch("reel-after", Wait(3f))) yield return 0;
            RodInput.VGrip = false; RodInput.VReel = false;
            foreach (int _ in Watch("end", Wait(1.5f))) yield return 0;
        }

        // ---------- HUD demo: walk the overlay through each of its looks, pausing for a screenshot at each ----------

        /// <summary>run.ps1 watches for this marker and photographs the game window.</summary>
        static IEnumerable<int> Shot(string name)
        {
            foreach (int _ in Wait(0.35f)) yield return 0;
            Log("SHOT " + name);
            foreach (int _ in Wait(0.9f)) yield return 0;
        }

        static IEnumerable<int> HudDemo(Game.Angler angler)
        {
            int dir = angler.FishingDir;
            float sweep = Settings.SweepFraction.Value / Math.Max(0.05f, Settings.Sensitivity.Value), draw = Settings.CastWindup.Value / Math.Max(0.05f, Settings.Sensitivity.Value);
            BlockAllAttraction = false; // show the HUD as a player sees it, technique hint and threshold included
            foreach (int _ in Wait(1.5f)) yield return 0;
            foreach (int _ in Shot("1-fishing")) yield return 0;

            RodInput.VGrip = true;
            foreach (int _ in Move(0f, sweep * 0.55f, 0.25f)) yield return 0;
            foreach (int _ in Shot("2-gripped-raised")) yield return 0;
            foreach (int _ in Move(0f, -sweep * 0.8f, 0.3f)) yield return 0;
            RodInput.VReel = true;
            foreach (int _ in Shot("3-dipped-reeling")) yield return 0;
            RodInput.VReel = false;
            foreach (int _ in Move(0f, sweep * 0.25f, 0.2f)) yield return 0;

            HoldBitePrompt = true;
            LureController.Hud.BiteUntil = Time.time + 2.5f;
            foreach (int _ in Shot("4-bite")) yield return 0;
            LureController.Hud.BiteUntil = -99f; HoldBitePrompt = false;
            LureController.Hud.Say("FISH ON!", 2.5f);
            foreach (int _ in Shot("5-fish-on")) yield return 0;
            LureController.Hud.Say("MISSED - spooked it", 2.5f);
            foreach (int _ in Shot("6-missed")) yield return 0;
            LureController.Hud.FlashUntil = -99f;

            RodInput.VReel = true;
            foreach (int _ in Until(() => StateIs(angler, angler.States.RodIdle), 40f, "the lure to be reeled in")) yield return 0;
            RodInput.VGrip = false; RodInput.VReel = false;
            if (s_timedOut) yield break;
            foreach (int _ in Wait(1.2f)) yield return 0;
            foreach (int _ in Shot("7-ready-to-cast")) yield return 0;

            s_ctl = null;
            RodInput.VGrip = true;
            foreach (int _ in Until(() => StateIs(angler, angler.States.RodAiming), 2f, "the aim pose")) yield return 0;
            foreach (int _ in Wait(0.5f)) yield return 0;
            foreach (int _ in Move(-dir * draw * 0.7f, 0f, 0.4f)) yield return 0;
            foreach (int _ in Shot("8-cast-loaded")) yield return 0;
            foreach (int _ in Move(dir * 1.2f, 0f, 0.2f)) yield return 0;
            RodInput.VGrip = false;
            foreach (int _ in Wait(0.3f)) yield return 0;
            foreach (int _ in Shot("9-cast-result")) yield return 0;
            foreach (int _ in Until(() => s_ctl != null, 12f, "the cast lure to land")) yield return 0;
            if (s_ctl == null) yield break;
            foreach (int _ in Wait(2f)) yield return 0;

            // a real fish on the line, for the fight look
            ForcedRoll = 0f; ForceNibble = true; ForcedFit = 1;
            RodInput.VGrip = true;
            if (AttractNearest())
            {
                foreach (int _ in Until(() => LureController.Hud.BiteUntil > Time.time, 30f, "a bite")) yield return 0;
                if (!s_timedOut)
                {
                    foreach (int _ in Wait(0.15f)) yield return 0;
                    foreach (int _ in Move(0f, sweep * 0.5f, 0.08f)) yield return 0;
                    foreach (int _ in Wait(0.3f)) yield return 0;
                    Log("SHOT 10-fight");
                    foreach (int _ in Wait(1.2f)) yield return 0;
                }
            }
            ForcedRoll = -1f; ForcedFit = -2;
            RodInput.VGrip = false;
            foreach (int _ in Wait(0.5f)) yield return 0;
        }

        // ---------- the script ----------

        static IEnumerator<int> Script()
        {
            foreach (int _ in Until(() => s_ctl != null, 120f, "idle mode to cast")) yield return 0;
            if (s_ctl == null) yield break;
            foreach (int _ in Wait(3f)) yield return 0;

            Game.Angler angler = s_ctl.Angler;
            Game.Rod rod = s_ctl.Rod;
            s_owns = true; ForceHuman = true; BlockAllAttraction = true;
            RodInput.Virtual = true;
            angler.InteractInput = 0f;
            int dir = angler.FishingDir;
            Log("script has the angler; facing " + dir + ", rod rest angle " + rod.transform.localEulerAngles.z.ToString("0.0"));
            foreach (int _ in Wait(1f)) yield return 0;

            // Idle mode has done its job (a line in the water). Wake the game from it, so the camera frames the water
            // as it does for a player: under the idle camera a lure on the surface sits at the very bottom of the screen.
            s_letIdleRun = true;
            try { Game.IdleManager.UserInteracted(); } catch (Exception e) { Log("could not leave idle mode: " + e.Message); }
            foreach (int _ in Wait(2.5f)) yield return 0;
            s_letIdleRun = false;
            if (s_ctl == null || s_ctl.Lure.State != Game.Lure.LureState.Fishing) { Log("the lure is no longer fishing after leaving idle mode"); yield break; }

            string labLure = (Settings.SelfTestLure.Value ?? "").Trim();
            if (labLure == "hud")
            {
                foreach (int _ in HudDemo(angler)) yield return 0;
                yield break;
            }
            if (labLure == "fish")
            {
                foreach (int _ in FishLab(angler)) yield return 0;
                yield break;
            }
            if (labLure == "fight")
            {
                foreach (int _ in FightLab(angler)) yield return 0;
                yield break;
            }
            if (labLure == "pop")
            {
                foreach (int _ in PopLab(angler)) yield return 0;
                yield break;
            }
            if (labLure.Length > 0)
            {
                foreach (int _ in Lab(angler, labLure)) yield return 0;
                yield break;
            }

            // ---- 1. grip and move the rod ----
            Log("STEP rod: grip and lift three times");
            float sweep = Settings.SweepFraction.Value / Math.Max(0.05f, Settings.Sensitivity.Value), draw = Settings.CastWindup.Value / Math.Max(0.05f, Settings.Sensitivity.Value);
            float restZ = rod.transform.localEulerAngles.z;
            Vector2 restTip = rod.tip.position;
            RodInput.VGrip = true;
            foreach (int _ in Wait(0.2f)) yield return 0;
            Log("grip mode = " + RodInput.Current + " ownsButtons=" + RodInput.OwnsButtons + "  " + Check(RodInput.Current == RodInput.Mode.Rod));
            float maxTurn = 0f, maxTipMove = 0f;
            for (int i = 0; i < 3; i++)
            {
                foreach (int _ in Move(0f, sweep * 0.6f, 0.1f)) yield return 0;
                float hold = Time.unscaledTime + 0.3f;
                while (Time.unscaledTime < hold)
                {
                    float z = rod.transform.localEulerAngles.z;
                    float turn = (z - restZ) % 360f; if (turn > 180f) turn -= 360f; if (turn < -180f) turn += 360f; turn = Math.Abs(turn);
                    Vector2 tip = rod.tip.position;
                    float moved = MathF.Sqrt((tip.x - restTip.x) * (tip.x - restTip.x) + (tip.y - restTip.y) * (tip.y - restTip.y));
                    if (turn > maxTurn) maxTurn = turn;
                    if (moved > maxTipMove) maxTipMove = moved;
                    yield return 0;
                }
                foreach (int _ in Move(0f, -sweep * 0.6f, 0.3f)) yield return 0;
                foreach (int _ in Wait(0.7f)) yield return 0;
            }
            Log(string.Format("rod turned up to {0:0.0} deg, tip moved up to {1:0.0} px  {2}", maxTurn, maxTipMove, Check(maxTurn > 20f && maxTipMove > 10f)));

            // left-click reels while gripping
            RodInput.VReel = true;
            foreach (int _ in Wait(0.8f)) yield return 0;
            float reelPower = rod.RetrievePower;
            RodInput.VReel = false;
            Log(string.Format("left-click while gripping -> reel power {0:0.00}  {1}", reelPower, Check(reelPower > 0.5f)));
            RodInput.VGrip = false;
            foreach (int _ in Wait(1.0f)) yield return 0;
            Log(string.Format("released: rod back to {0:0.0} deg from rest  {1}", RodPose.Current, Check(Math.Abs(RodPose.Current) < 1f)));

            // ---- 1b. with the rod against its top stop there is no room left to jerk it: a flick of the hand must still count ----
            RodInput.VGrip = true;
            foreach (int _ in Move(0f, sweep * 1.3f, 1.2f)) yield return 0;
            foreach (int _ in Wait(0.4f)) yield return 0;
            float jerkBefore = RodPose.LastJerkTime, atStop = RodPose.Current;
            foreach (int _ in Move(0f, sweep * 0.4f, 0.08f)) yield return 0;
            foreach (int _ in Wait(0.2f)) yield return 0;
            Log(string.Format("rod at its stop ({0:0} deg, still {1:0}): a flick of the hand registers as a jerk  {2}", atStop, RodPose.Current,
                Check(atStop > Settings.RaiseDegrees.Value - 2f && RodPose.LastJerkTime > jerkBefore)));
            foreach (int _ in Move(0f, -sweep * 1.5f, 0.5f)) yield return 0;
            RodInput.VGrip = false;
            foreach (int _ in Wait(1.0f)) yield return 0;

            // ---- 2. bail: a tap lets line out, a click stops it ----
            Log("STEP bail: tap the grip button");
            RodInput.VGrip = true;
            foreach (int _ in Wait(0.08f)) yield return 0;
            RodInput.VGrip = false;
            foreach (int _ in Wait(1.2f)) yield return 0;
            Log(string.Format("bail open={0} reel power {1:0.00}  {2}", RodInput.BailOpen, rod.RetrievePower, Check(RodInput.BailOpen && rod.RetrievePower < -0.5f)));
            RodInput.VReel = true;
            foreach (int _ in Wait(0.1f)) yield return 0;
            RodInput.VReel = false;
            foreach (int _ in Wait(0.5f)) yield return 0;
            Log(string.Format("after a click: bail open={0} reel power {1:0.00}  {2}", RodInput.BailOpen, rod.RetrievePower, Check(!RodInput.BailOpen && rod.RetrievePower > -0.1f)));

            // ---- 3. reel in, then swing a cast ----
            Log("STEP reel in");
            RodInput.VGrip = true; RodInput.VReel = true;
            foreach (int _ in Until(() => StateIs(angler, angler.States.RodIdle), 40f, "the lure to be reeled in")) yield return 0;
            RodInput.VGrip = false; RodInput.VReel = false;
            if (s_timedOut) yield break;
            s_ctl = null;
            foreach (int _ in Wait(1.5f)) yield return 0;

            Log("STEP cast: grip, draw back, swing, release  (canCast=" + angler.CanCast + ")");
            RodInput.VGrip = true;
            foreach (int _ in Until(() => StateIs(angler, angler.States.RodAiming), 2f, "the aim pose")) yield return 0;
            Log("aiming=" + StateIs(angler, angler.States.RodAiming) + " swingCastActive=" + Casting.Active + "  " + Check(Casting.Active && !s_timedOut));
            foreach (int _ in Wait(0.5f)) yield return 0;
            foreach (int _ in Move(-dir * draw * 0.7f, 0f, 0.4f)) yield return 0;    // draw the rod back to about 70%
            float windup = Casting.Windup;
            foreach (int _ in Wait(0.15f)) yield return 0;
            foreach (int _ in Move(dir * 1.2f, 0f, 0.2f)) yield return 0;            // swing at 6 screen heights per second
            foreach (int _ in Wait(0.1f)) yield return 0;                            // and let go a moment after it ends
            RodInput.VGrip = false;
            foreach (int _ in Until(() => s_ctl != null, 12f, "the cast lure to land")) yield return 0;
            Log(string.Format("windup reached {0:0.00}, cast power {1:0.00}, lure landed={2}  {3}", windup, Casting.LastPower, s_ctl != null,
                Check(s_ctl != null && Casting.LastPower > 0.45f && Casting.LastPower < 0.75f && windup > 0.6f && windup < 0.8f)));
            if (s_ctl == null) yield break;
            rod = s_ctl.Rod;
            foreach (int _ in Wait(2.5f)) yield return 0;

            // ---- 4. a bite answered too weakly to matter is a miss; a second one is landed ----
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool wantHit = attempt == 1;
                Log("STEP hookset " + (wantHit ? "(forced hit)" : "(forced miss)"));
                ForcedRoll = wantHit ? 0f : 0.99f;
                ForceNibble = true; ForcedFit = 1; // whatever the fish, the lure is doing what it wants
                RodInput.VGrip = true;
                if (!AttractNearest()) break;
                foreach (int _ in Until(() => LureController.Hud.BiteUntil > Time.time, 30f, "a bite")) yield return 0;
                if (s_timedOut) break;
                foreach (int _ in Wait(0.15f)) yield return 0;                        // reaction time
                foreach (int _ in Move(0f, sweep * 0.5f, 0.08f)) yield return 0;      // the jerk
                foreach (int _ in Wait(0.5f)) yield return 0;
                bool hooked = s_ctl != null && s_ctl.Lure.State == Game.Lure.LureState.Hooked;
                Log("after the jerk: hooked=" + hooked + " flash=\"" + LureController.Hud.Flash + "\"  " + Check(hooked == wantHit));
                if (hooked) break;
                foreach (int _ in Move(0f, -sweep * 0.7f, 0.3f)) yield return 0;      // lower the rod again
                foreach (int _ in Wait(1.5f)) yield return 0;
            }

            // ---- 5. pump the rod on the hooked fish for a few seconds ----
            ForcedRoll = -1f; ForcedFit = -2;
            for (int i = 0; i < 2; i++)
            {
                foreach (int _ in Move(0f, sweep * 0.4f, 0.2f)) yield return 0;
                foreach (int _ in Wait(0.5f)) yield return 0;
                foreach (int _ in Move(0f, -sweep * 0.4f, 0.3f)) yield return 0;
                foreach (int _ in Wait(0.5f)) yield return 0;
            }
            Log("pumped the rod on a hooked fish without errors");
            RodInput.VGrip = false;
            foreach (int _ in Wait(1f)) yield return 0;
        }
    }
}
