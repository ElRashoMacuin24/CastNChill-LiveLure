using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveLure
{
    /// <summary>
    /// Swing-to-cast. With the rod idle, holding the grip button raises the rod into the game's aim pose. Pulling the
    /// mouse back from the water draws the rod back and loads it; swinging toward the water and letting go releases
    /// the line.
    ///
    /// Cast power is mostly how far the rod was drawn back when the forward swing began, which is easy to judge and
    /// repeat, scaled by how fast the swing was. Swing speed is remembered for a moment, so letting go just after the
    /// swing ends still counts, but hanging on too long wastes it.
    ///
    /// The game's own aim state normally ramps power with time held; while a swing cast is in progress that state's
    /// update is replaced by <see cref="AimingUpdate"/>.
    /// </summary>
    static class Casting
    {
        const float SpeedWindow = 0.06f;   // seconds of movement averaged into one speed reading
        const float HoldFull = 0.12f;      // a swing still counts in full this long after it peaked...
        const float HoldGone = 0.40f;      // ...and not at all after this long
        const float MinSwing = 0.5f;       // screen heights per second below which it is not a swing
        const float MinPower = 0.05f;

        public static bool Active { get; private set; }
        /// <summary>For the HUD: how far the rod is drawn back now, and how much power is loaded for the next swing.</summary>
        public static float Windup, Load;
        /// <summary>For the HUD: power of the cast just made, and until when to show it.</summary>
        public static float LastPower, ShowUntil = -99f;

        static Game.Angler s_angler;
        static bool s_released;
        static float s_power, s_began, s_swing;
        static readonly Queue<KeyValuePair<float, float>> s_moves = new Queue<KeyValuePair<float, float>>();   // time, px toward the water
        static readonly Queue<KeyValuePair<float, float>> s_speeds = new Queue<KeyValuePair<float, float>>();  // time, screen heights per second

        public static void Begin(Game.Angler angler)
        {
            Active = true;
            s_angler = angler;
            s_released = false;
            s_began = Time.unscaledTime;
            Windup = 0f;
            Load = 0f;
            s_moves.Clear();
            s_speeds.Clear();
            angler.InteractInput = 1f; // the game sees "cast held" and brings the rod up to aim
        }

        static void Cancel()
        {
            Active = false;
            try { if (s_angler != null) s_angler.InteractInput = 0f; } catch (Exception) { }
            s_angler = null;
        }

        /// <summary>Every frame, after the input has been read.</summary>
        public static void Update()
        {
            if (!Active) return;
            Game.Angler angler = s_angler;
            Game.Angler.AnglerState state;
            Game.Angler.AnglerStates states;
            try { state = angler.State; states = angler.States; }
            catch (Exception) { Cancel(); return; }

            bool idle = state != null && state.Pointer == states.RodIdle.Pointer;
            bool aiming = state != null && state.Pointer == states.RodAiming.Pointer;
            if (!idle && !aiming)
            {
                // On the ice there is no cast: the game just drops the line. Either way our part is over.
                Active = false; angler.InteractInput = 0f; s_angler = null;
                return;
            }
            if (idle)
            {
                // released before the rod came up, or the game will not let us cast right now
                if (!RodInput.GripDown || Time.unscaledTime - s_began > 0.6f) { Cancel(); return; }
                angler.InteractInput = 1f;
                return;
            }
            if (s_released) return;

            float now = Time.unscaledTime, h = Math.Max(1f, Screen.height), sens = Math.Max(0.05f, Settings.Sensitivity.Value);
            // toward the water: sideways in the facing direction, and downward as the rod comes over the top
            float forward = angler.FishingDir * RodInput.Dx - 0.35f * RodInput.Dy;

            float travel = Math.Max(40f, h * Settings.CastWindup.Value / sens);
            Windup = Math.Clamp(Windup - forward / travel, 0f, 1f);
            if (forward < 0f) Load = Windup; // the swing starts from wherever the rod was last drawn back to

            s_moves.Enqueue(new KeyValuePair<float, float>(now, forward));
            while (s_moves.Count > 0 && now - s_moves.Peek().Key > SpeedWindow) s_moves.Dequeue();
            float sum = 0f;
            foreach (var m in s_moves) sum += m.Value;
            s_speeds.Enqueue(new KeyValuePair<float, float>(now, Math.Max(0f, sum) / SpeedWindow / h));
            while (s_speeds.Count > 0 && now - s_speeds.Peek().Key > HoldGone) s_speeds.Dequeue();

            if (!RodInput.GripDown)
            {
                s_released = true;
                s_swing = RecentSwing(now);
                s_power = PowerFor(Load, s_swing, sens);
            }
        }

        /// <summary>The fastest the mouse was moving toward the water just now, fading the longer ago it was.</summary>
        static float RecentSwing(float now)
        {
            float best = 0f;
            foreach (var s in s_speeds)
            {
                float age = now - s.Key;
                float weight = age <= HoldFull ? 1f : Math.Max(0f, 1f - (age - HoldFull) / (HoldGone - HoldFull));
                if (s.Value * weight > best) best = s.Value * weight;
            }
            return best;
        }

        static float PowerFor(float load, float swing, float sens)
        {
            float full = Math.Max(MinSwing + 0.5f, Settings.CastFullSpeed.Value / sens);
            float speed01 = Math.Clamp((swing - MinSwing) / (full - MinSwing), 0f, 1f);
            // A loaded rod let go without a swing just lobs the lure. A swing with no draw-back still casts, but short.
            float power = speed01 > 0f ? load * (0.55f + 0.45f * speed01) : load * 0.35f;
            power = Math.Max(power, 0.45f * speed01);
            return Math.Clamp(power, MinPower, 1f);
        }

        /// <summary>Replaces RodAimingState.Update while a swing cast is in progress. Returns true to let the game run its own.</summary>
        public static bool AimingUpdate(Game.Angler.RodAimingState state)
        {
            if (!Active || s_angler == null) return true;
            Game.Angler angler = state.Angler;
            if (angler == null || angler.Pointer != s_angler.Pointer) return true;

            float aimDelay = 0.25f;
            try { aimDelay = Game.GameplayConfig.Instance.angler.aimDelay; } catch (Exception) { }
            if (angler.StateDuration <= aimDelay) return false; // the rod is still coming up to the aim pose

            Game.Rod rod = state.Rod;
            if (!s_released)
            {
                if (RodInput.ReelPressed)
                {
                    // left-click while winding up: change of mind
                    Active = false; s_angler = null; angler.InteractInput = 0f;
                    state.SetState(angler.States.RodAimAbort);
                    Plugin.Note("cast aborted");
                    return false;
                }
                rod.SetCastPower(Windup);                         // poses the rod along its aim animation
                angler.ScrubAnimation(state.m_animToPlay, Windup); // and the angler's arms with it
                return false;
            }

            if (Load < 0.03f && s_swing < MinSwing)
            {
                // Let go with nothing loaded and no swing: a stray click, not a cast.
                Active = false; s_angler = null; angler.InteractInput = 0f;
                state.SetState(angler.States.RodAimAbort);
                Plugin.Note("grip released without a swing: no cast");
                return false;
            }

            rod.CastPower = s_power;
            angler.InteractInput = 0f;
            Active = false; s_angler = null;
            LastPower = s_power; ShowUntil = Time.unscaledTime + 1.6f;
            state.SetState(angler.States.RodCasting);
            Plugin.Note(string.Format("cast released: power {0:0.00} (rod loaded {1:0.00}, swing {2:0.0} screen heights/s)", s_power, Load, s_swing));
            return false;
        }
    }
}
