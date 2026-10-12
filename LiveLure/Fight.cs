using System;
using UnityEngine;
using LureState = Game.Lure.LureState;

namespace LiveLure
{
    /// <summary>
    /// The fight, once a fish is on.
    ///
    /// In the base game a hooked fish tires on a clock: its fight lasts a set number of seconds whatever the angler
    /// does, and its strength is read off a curve of how much of that time has passed. The reel has a drag, but a
    /// fixed one that only gives line while you are not reeling; hold the reel against a running fish and the line
    /// sits at its limit until it snaps (four seconds there, in total, breaks it).
    ///
    /// Here the angler decides the fight:
    ///   - the drag is adjustable and always works, reeling or not. Tension above the setting pulls line off the reel
    ///     instead of climbing to breaking point, so a loose drag is safe and a tight one is not;
    ///   - the fish tires faster the harder the line is loaded, and barely at all on a slack line, so that safety
    ///     costs time and line;
    ///   - heaving the rod (the same sharp lift that sets a hook) takes a bite out of the fish's remaining fight,
    ///     more the more the line is loaded when you do it, and can turn the head of a fish that is running away;
    ///   - heaving on a line that is already at its limit strains it.
    /// </summary>
    static class Fight
    {
        public const float DragMin = 0.2f, DragMax = 1f;

        // ---- read by the HUD ----
        public static bool Active, Slipping, TooBig;
        /// <summary>Line load, the fish's remaining fight and how close the line is to parting, all 0..1.</summary>
        public static float Tension, Stamina = 1f, Strain;
        /// <summary>Unscaled time of the last drag change and the last rod pump that counted.</summary>
        public static float DragChanged = -99f, PumpFlash = -99f;
        public static int Pumps, Turns;
        /// <summary>The last pump: the most the line was loaded during it, and the seconds of fight it took.</summary>
        public static float LastPumpLoad, LastPumpSeconds;

        /// <summary>Seconds of fight left at which a fish counts as spent.</summary>
        const float Spent = 0.05f;
        const string StrainText = "LINE STRAIN  -  ease off, loosen the drag";
        const string SlackText = "WEAK PUMP  -  reel up the slack first";

        static readonly System.Random s_random = new System.Random();
        static IntPtr s_lure;
        static string s_fishId = "?";
        static float s_start, s_lastPump = -99f, s_pumpTime, s_pumpPeak, s_jerkSeen, s_telNext, s_lineTaken;
        static float s_tensionSum, s_tensionMax;
        static int s_telCount;
        static bool s_pumpPending, s_tiredSaid;

        // one Rod.UpdateLineForce call
        static bool s_inCall, s_powerChanged;
        static Game.RodData.Response s_response;
        static float s_savedRatio, s_lineBefore, s_timerBefore;

        /// <summary>Where the reel starts to give line, as a fraction of what the line can take.</summary>
        public static float Drag => Math.Clamp(SelfTest.DragOverride >= 0f ? SelfTest.DragOverride : Settings.Drag.Value, DragMin, DragMax);

        public static void AdjustDrag(int steps)
        {
            if (steps == 0) return;
            float step = Math.Clamp(Settings.DragStep.Value, 0.01f, 0.25f);
            float drag = Math.Clamp(MathF.Round((Drag + steps * step) / step) * step, DragMin, DragMax);
            if (Math.Abs(drag - Settings.Drag.Value) < 0.0005f) return;
            Settings.Drag.Value = drag;
            DragChanged = Time.unscaledTime;
            Plugin.Note(string.Format("drag {0:0}%", drag * 100f));
        }

        static bool Mine(Game.Rod rod)
        {
            Game.Angler mine = RodInput.Angler;
            if (mine == null) return false;
            Game.Rod mineRod = mine.rod;
            return mineRod != null && mineRod.Pointer == rod.Pointer && RodInput.Human(mine);
        }

        // ---- the drag: around the game's own line update ----

        /// <summary>
        /// Rod.UpdateLineForce is about to run for a rod with a fish on. The game reads the drag threshold from the
        /// rod's data and only lets the reel slip while reel power is below 1, so for the length of this one call the
        /// threshold becomes the player's setting and a held reel counts as a hair under full.
        /// </summary>
        public static void BeforeLineForce(Game.Rod rod)
        {
            s_inCall = false;
            if (!Settings.Enabled.Value || !Settings.FightMechanics.Value || SelfTest.VanillaLine || !Mine(rod)) return;
            Game.Lure lure = rod.Lure;
            if (lure == null || lure.State != LureState.Hooked || lure.HookedFish == null) return;
            Game.RodData data = rod.Data;
            Game.RodData.Response response = data != null ? data.response : null;
            if (response == null) return;

            s_response = response;
            s_savedRatio = response.startDragRatio;
            // A hand clamps down on the spool as the rod is heaved, so a pump moves the fish instead of just pulling line.
            float drag = Drag;
            drag += (1f - drag) * Math.Clamp(Settings.PumpGrip.Value, 0f, 1f) * RodPose.Yank;
            // Landing. A spent fish has nothing left to take line with, and close in the game hauls it the last of the
            // way with three times the pull: either would run straight through a light drag and the fish would never
            // come to hand. So the drag firms up as the fight goes out of the fish and as it nears the rod.
            if (Settings.LandingGrip.Value)
            {
                float spent = Math.Clamp(1f - Stamina / 0.15f, 0f, 1f);
                float near = 0f;
                try
                {
                    Game.FloatRange close = Game.FishingLineConfig.Instance.lureCloseDistance;
                    if (close != null) near = Math.Clamp((close.max * 1.5f - rod.DistanceToLure) / Math.Max(1f, close.max * 0.5f), 0f, 1f);
                }
                catch (Exception) { }
                drag += (1f - drag) * Math.Max(spent, near);
            }
            response.startDragRatio = Math.Min(drag, 0.98f);

            s_powerChanged = rod.RetrievePower >= 1f;
            if (s_powerChanged) rod.RetrievePower = 0.999f;
            s_lineBefore = rod.LineLength;
            s_timerBefore = rod.m_springMaxTimer;
            s_inCall = true;
        }

        public static void AfterLineForce(Game.Rod rod)
        {
            if (!s_inCall) return;
            s_inCall = false;
            s_response.startDragRatio = s_savedRatio;
            s_response = null;
            // If the call ended the fight (landed, or the line went) the game has already reset the reel: leave it.
            if (s_powerChanged && Math.Abs(rod.RetrievePower - 0.999f) < 0.0005f) rod.RetrievePower = 1f;

            float taken = rod.LineLength - s_lineBefore;
            Slipping = rod.AudioLineDrag > 0.01f && taken > -0.01f;
            if (taken > 0f) s_lineTaken += taken;

            float scale = Settings.BreakTimeScale.Value, timer = rod.m_springMaxTimer;
            if (scale > 0.05f && Math.Abs(scale - 1f) > 0.001f && timer > s_timerBefore)
                rod.m_springMaxTimer = s_timerBefore + (timer - s_timerBefore) / scale;
        }

        // ---- the fish ----

        /// <summary>Any lure, any state other than hooked: notice when our fight has ended.</summary>
        public static void Watch(Game.Lure lure, LureState state)
        {
            if (Active && lure.Pointer == s_lure && state != LureState.Hooked) End(state);
        }

        /// <summary>Once a frame: the same check from the angler's side, in case the lure stops ticking once the fish is in.</summary>
        public static void Update()
        {
            if (!Active) return;
            Game.Angler mine = RodInput.Angler;
            Game.Rod rod = mine != null ? mine.rod : null;
            Game.Lure lure = rod != null ? rod.Lure : null;
            if (lure == null || lure.Pointer != s_lure) { End(LureState.None); return; }
            LureState state = lure.State;
            if (state != LureState.Hooked) End(state);
        }

        static void End(LureState state)
        {
            Active = false; Slipping = false; s_pumpPending = false;
            bool landed = state == LureState.Caught, parted = !landed && Strain >= 0.9f;
            if (parted) LureController.Hud.Say("THE LINE PARTED", 1.8f);
            Plugin.Note(string.Format("fight over: {0} {1} after {2:0.0}s, {3} pumps ({4} turned it), {5:0} px of line taken by the drag, fish had {6:0}% left",
                s_fishId, landed ? "LANDED" : parted ? "lost, the line parted" : "lost", Time.time - s_start, Pumps, Turns, s_lineTaken, Stamina * 100f));
            Strain = 0f;
        }

        static void Begin(Game.Lure lure, Game.Rod rod, Game.Fish fish, Game.Fish.HookedState hooked)
        {
            Active = true;
            s_lure = lure.Pointer;
            s_start = Time.time;
            s_jerkSeen = RodPose.LastJerkTime; // the jerk that set the hook is not a pump
            s_lastPump = -99f; s_pumpPending = false; s_tiredSaid = false;
            s_lineTaken = 0f; s_tensionSum = 0f; s_tensionMax = 0f; s_telCount = 0; s_telNext = Time.time + 1f;
            Pumps = 0; Turns = 0; Stamina = 1f; Strain = 0f;
            TooBig = hooked.m_isTooBig;

            Game.FishData data = fish.Data;
            s_fishId = data != null ? data.id : "?";
            Game.FishFightActionData action = hooked.m_data;
            Game.RodData rodData = rod.Data;
            Game.RodStrengthData strength = rod.StrengthData;
            Plugin.Note(string.Format("fight: {0} weight {1:0.0}, a {2} with {3:0}s of fight in it (stamina x{4:0.00}){5}; {6} line strength {7}, drag {8:0}%",
                s_fishId, fish.Weight, action != null ? action.id : "?", hooked.m_fightDuration, hooked.m_staminaMultiplier,
                TooBig ? " - TOO BIG for this tackle" : "", rodData != null ? rodData.id : "?", strength != null ? strength.weight : 0, Drag * 100f));
            if (TooBig) LureController.Hud.Tell("TOO BIG FOR THIS TACKLE  -  it will break", -1, 3f);
        }

        /// <summary>Called after every Lure.FixedUpdate while that lure has something hooked.</summary>
        public static void Tick(Game.Lure lure)
        {
            if (!Settings.Enabled.Value) return;
            Game.Rod rod = lure.Rod;
            if (rod == null || !Mine(rod)) return;

            HudState hud = LureController.Hud;
            hud.FightStamp = Time.unscaledTime;
            Tension = Math.Clamp(rod.SpringRatio, 0f, 1f);
            hud.Tension = Tension;

            Game.Fish fish = lure.HookedFish;
            if (fish == null) return; // flotsam: nothing to wear down
            Game.Fish.HookedState hooked = fish.States.Hooked;
            if (hooked == null) return;
            if (!Active || s_lure != lure.Pointer) Begin(lure, rod, fish, hooked);

            float dt = Time.fixedDeltaTime, now = Time.time;
            float duration = hooked.m_fightDuration;
            Strain = Math.Clamp(rod.SpringMaxBreakRatio, 0f, 1f);
            if (duration < 0.01f) return;
            float timer = hooked.m_fightTimer;

            if (Settings.FightMechanics.Value && !TooBig)
            {
                // Pressure. The game takes one second off the fight per second; this adds to or eats into that.
                // (A fish that is already spent stays spent: a slack line does not hand it time back.)
                float rate = Settings.SlackTire.Value + Settings.PressureTire.Value * Tension;
                if (rate >= 1f || timer > Spent) timer -= dt * (rate - 1f);

                // Pumps. A jerk is judged a moment after it starts, so its full height and the load it put on the line count.
                if (RodPose.LastJerkTime > s_jerkSeen + 0.001f && !s_pumpPending)
                {
                    s_jerkSeen = RodPose.LastJerkTime;
                    s_pumpPending = true;
                    s_pumpTime = RodPose.LastJerkTime;
                    s_pumpPeak = Tension;
                }
                if (s_pumpPending)
                {
                    if (Tension > s_pumpPeak) s_pumpPeak = Tension;
                    if (now >= s_pumpTime + 0.15f)
                    {
                        s_pumpPending = false;
                        timer = Pump(rod, fish, hooked, timer, duration);
                    }
                }

                timer = Math.Clamp(timer, 0f, duration);
                hooked.m_fightTimer = timer;

                if (Strain > 0.15f) hud.Tell(StrainText, -1, 0.5f);
                else if (timer <= Spent && !s_tiredSaid)
                {
                    s_tiredSaid = true;
                    hud.Tell("WORN OUT  -  bring it in", 1, 3f);
                    Plugin.Note(string.Format("fight: {0} worn out after {1:0.0}s", s_fishId, now - s_start));
                }
            }
            Stamina = timer / duration;

            s_tensionSum += Tension; s_telCount++;
            if (Tension > s_tensionMax) s_tensionMax = Tension;
            if (Settings.Telemetry.Value && now >= s_telNext)
            {
                s_telNext = now + 1f;
                Plugin.Note(string.Format("fight: tension {0:0.00} avg {1:0.00} max, drag {2:0}%{3}, fish {4:0}%, line {5:0} px, reel {6:0.00}, rod {7:0} deg, strain {8:0.00}, pumps {9}",
                    s_tensionSum / Math.Max(1, s_telCount), s_tensionMax, Drag * 100f, Slipping ? " (giving line)" : "", Stamina * 100f,
                    rod.LineLength, rod.RetrievePower, RodPose.Current, Strain, Pumps));
                s_tensionSum = 0f; s_tensionMax = 0f; s_telCount = 0;
            }
        }

        /// <summary>
        /// One heave of the rod. What it costs the fish depends on how big a lift it was, how loaded the line was while
        /// it happened (a pump against a slipping drag does little), and whether the fish was given time to feel the
        /// last one. Returns the fish's remaining fight time.
        /// </summary>
        static float Pump(Game.Rod rod, Game.Fish fish, Game.Fish.HookedState hooked, float timer, float duration)
        {
            if (timer <= Spent)
            {
                // nothing left to take out of it: say so, rather than praise a pump that did nothing
                LureController.Hud.Tell("WORN OUT  -  just reel it in", 1, 1.6f);
                return timer;
            }
            float need = Math.Max(1f, Settings.HooksetJerkDegrees.Value);
            float lift = 0.6f + 0.4f * Math.Clamp((RodPose.LastJerkStrength - need) / (need * 1.5f), 0f, 1f);
            float load = 0.3f + 0.7f * Math.Clamp(s_pumpPeak / 0.8f, 0f, 1f);
            float rhythm = Math.Clamp((s_pumpTime - s_lastPump) / Math.Max(0.1f, Settings.PumpRecover.Value), 0.2f, 1f);
            // Measured against a 40-second fight: a fish with more in it loses more seconds to each pump, but a smaller share.
            float seconds = Math.Max(0f, Settings.PumpTire.Value) * 0.5f * (duration + 40f) * lift * load * rhythm;
            s_lastPump = s_pumpTime;
            LastPumpLoad = s_pumpPeak;
            LastPumpSeconds = 0f;
            HudState hud = LureController.Hud;
            if (s_pumpPeak < 0.05f)
            {
                // nothing on the line to pull against
                hud.Tell(SlackText, 0, 1.4f);
                Plugin.Note("fight: pump on a slack line did nothing");
                return timer;
            }

            float before = timer;
            timer -= seconds;
            LastPumpSeconds = seconds;
            Pumps++;
            PumpFlash = Time.unscaledTime;

            // A fish running away from the rod can be turned.
            bool turned = false;
            Vector2 fp = fish.Position, fv = fish.Velocity;
            Vector3 tip = rod.TipPos;
            float tx = tip.x - fp.x, ty = tip.y - fp.y;
            bool running = fv.x * tx + fv.y * ty < 0f && fv.x * fv.x + fv.y * fv.y > 64f;
            float chance = 0f, roll = 1f;
            if (running)
            {
                float heft = 0.6f;
                try
                {
                    Game.RodStrengthData strength = rod.StrengthData;
                    if (strength != null && strength.weight > 0)
                        heft = Game.FishConfig.Instance.strugglingTurnChanceCurve.Evaluate(fish.Weight / strength.weight);
                }
                catch (Exception) { }
                chance = Math.Clamp(Settings.TurnChance.Value * heft * lift * load, 0f, 1f);
                roll = SelfTest.ForcedLuck >= 0f ? SelfTest.ForcedLuck : (float)s_random.NextDouble();
                if (roll < chance)
                {
                    hooked.PickNextHeading(fp.x, tx > 0f);
                    turned = true;
                    Turns++;
                }
            }

            // Heaving on a line already at its limit is how lines part.
            bool strained = s_pumpPeak >= 0.97f;
            if (strained) rod.m_springMaxTimer += Math.Max(0f, Settings.JerkStrain.Value);

            if (turned) hud.Tell("TURNED IT!  -  reel", 1, 1.6f);
            else if (strained) hud.Tell(StrainText, -1, 1.2f);
            else if (s_pumpPeak < 0.3f) hud.Tell(SlackText, 0, 1.4f);
            else hud.Tell(rhythm < 0.6f ? "PUMP  -  too soon, let it feel each one" : "GOOD PUMP  -  it is tiring", rhythm < 0.6f ? 0 : 1, 1.2f);

            Plugin.Note(string.Format("fight: pump {0:0} deg, line load {1:0.00}, rhythm {2:0.00} -> fish {3:0.0}% to {4:0.0}%{5}{6}",
                RodPose.LastJerkStrength, s_pumpPeak, rhythm, before / duration * 100f, Math.Max(0f, timer) / duration * 100f,
                running ? string.Format(", it was running: turn chance {0:0.00} roll {1:0.00}{2}", chance, roll, turned ? " TURNED" : "") : "",
                strained ? ", line strained" : ""));
            return timer;
        }
    }
}
