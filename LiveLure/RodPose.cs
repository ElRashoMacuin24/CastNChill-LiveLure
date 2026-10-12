using System;
using UnityEngine;

namespace LiveLure
{
    /// <summary>
    /// Turns the real rod with the mouse. The game works out the rod tip, the line and the pull on the lure from the
    /// rod object's rotation every frame, so rotating that object is all it takes for the lure to feel the rod.
    ///
    /// The game's animator also drives that rotation (canned jig, reel and brace poses), so our angle is layered on
    /// top: added just before the game reads the pose, and taken off again at the start of the next frame so the
    /// animator always sees its own value.
    /// </summary>
    static class RodPose
    {
        /// <summary>Degrees the rod is raised from rest (negative = dipped). Target is where the hand is, Current where the rod is.</summary>
        public static float Target, Current;
        /// <summary>Game time and size (degrees raised within a fifth of a second) of the last sharp upward jerk.</summary>
        public static float LastJerkTime = -99f, LastJerkStrength;
        /// <summary>How hard the rod is being ripped upward right now, 0..1. Lingers briefly so the lure can finish the move.</summary>
        public static float Yank;

        const float YankFullRate = 140f;  // degrees per second of raise that counts as a full-blooded yank
        const float YankLinger = 0.22f;   // seconds the effect takes to fade once the rod stops
        static float s_lastCurrent;

        const float JerkSpan = 0.2f;
        const int HistorySize = 64;
        static readonly float[] s_time = new float[HistorySize], s_angle = new float[HistorySize], s_hand = new float[HistorySize];
        static int s_head;
        static bool s_jerkArmed = true;
        /// <summary>Everything the hand has asked of the rod, in degrees, without the rod's end stops.</summary>
        static float s_handTotal;

        static Transform s_applied;
        static float s_baseZ;

        /// <summary>The rotation, in degrees about z, that is on the rod transform right now on top of the game's own pose.</summary>
        public static float Applied;

        public static float Raise01
        {
            get
            {
                float dip = Settings.DipDegrees.Value, range = dip + Settings.RaiseDegrees.Value;
                return range > 0.01f ? (Current + dip) / range : 0f;
            }
        }

        /// <summary>How far the rod has risen within the last <paramref name="seconds"/>.</summary>
        public static float RaiseWithin(float seconds)
        {
            float now = Time.time, min = Current;
            for (int i = 0; i < HistorySize; i++)
                if (now - s_time[i] <= seconds && s_angle[i] < min) min = s_angle[i];
            return Current - min;
        }

        /// <summary>
        /// How far the hand has swept upward within the last <paramref name="seconds"/>, in rod degrees, whether or not
        /// the rod had room to follow. A rod already held high has nowhere left to go, but a sharp upward flick of the
        /// hand there is still a strike.
        /// </summary>
        public static float FlickWithin(float seconds)
        {
            float now = Time.time, min = s_handTotal;
            for (int i = 0; i < HistorySize; i++)
                if (now - s_time[i] <= seconds && s_hand[i] < min) min = s_hand[i];
            return s_handTotal - min;
        }

        /// <summary>Start of frame: hand the rotation back to the animator, then move the hand.</summary>
        public static void Update()
        {
            if (s_applied != null)
            {
                try
                {
                    Vector3 e = s_applied.localEulerAngles;
                    e.z = s_baseZ;
                    s_applied.localEulerAngles = e;
                }
                catch (Exception) { }
                s_applied = null;
                Applied = 0f;
            }

            float dt = Time.unscaledDeltaTime;
            Game.Angler angler = RodInput.Angler;
            bool holding = RodInput.Current == RodInput.Mode.Rod && angler != null && CanMoveRod(angler);
            if (holding)
            {
                float up = Settings.InvertY.Value ? -RodInput.Dy : RodInput.Dy;
                float back = -angler.FishingDir * RodInput.Dx; // drawing the rod back from the water also raises it
                float pixels = up + back * Settings.HorizontalWeight.Value;
                float range = Settings.DipDegrees.Value + Settings.RaiseDegrees.Value;
                float sweep = Math.Max(40f, Screen.height * Settings.SweepFraction.Value / Math.Max(0.05f, Settings.Sensitivity.Value));
                Target += pixels / sweep * range;
                Target = Math.Clamp(Target, -Settings.DipDegrees.Value, Settings.RaiseDegrees.Value);
                s_handTotal += pixels / sweep * range;
            }
            else
            {
                float step = Settings.ReturnSpeed.Value * dt;
                Target = Target > 0f ? Math.Max(0f, Target - step) : Math.Min(0f, Target + step);
            }

            float lag = Settings.Smoothing.Value;
            Current = lag > 0.001f ? Current + (Target - Current) * (1f - MathF.Exp(-dt / lag)) : Target;

            float raiseRate = dt > 0.0001f ? (Current - s_lastCurrent) / dt : 0f;
            s_lastCurrent = Current;
            Yank = Math.Max(Math.Clamp(raiseRate / YankFullRate, 0f, 1f), Yank * MathF.Exp(-dt / YankLinger));

            s_time[s_head] = Time.time; s_angle[s_head] = Current; s_hand[s_head] = s_handTotal;
            s_head = (s_head + 1) % HistorySize;

            // A jerk is the rod snapping upward, or, when the rod is already against its stop, the hand doing so.
            float rise = Math.Max(RaiseWithin(JerkSpan), holding ? FlickWithin(JerkSpan) : 0f), need = Settings.HooksetJerkDegrees.Value;
            if (s_jerkArmed && rise >= need) { s_jerkArmed = false; LastJerkTime = Time.time; LastJerkStrength = rise; }
            else if (!s_jerkArmed)
            {
                if (rise > LastJerkStrength && Time.time - LastJerkTime < 0.25f) LastJerkStrength = rise; // the jerk is still building
                if (rise < need * 0.4f) s_jerkArmed = true;
            }
        }

        static bool CanMoveRod(Game.Angler angler)
        {
            Game.Rod rod = angler.rod;
            Game.Lure lure = rod != null ? rod.Lure : null;
            if (lure == null) return false;
            Game.Lure.LureState state = lure.State;
            if (state == Game.Lure.LureState.Fishing) return true;
            return state == Game.Lure.LureState.Hooked && Settings.RodWhileHooked.Value;
        }

        /// <summary>Just before the game reads the rod's pose (Rod.LateUpdate).</summary>
        public static void Apply(Game.Rod rod)
        {
            if (Math.Abs(Current) < 0.02f) return;
            Game.Angler angler = RodInput.Angler;
            if (angler == null) return;
            Game.Rod mine = angler.rod;
            if (mine == null || mine.Pointer != rod.Pointer) return;

            Transform tf = rod.transform;
            Vector3 e = tf.localEulerAngles;
            s_baseZ = e.z;
            // The rod leans toward the water; raising it swings it back toward upright and past it.
            float lean = e.z > 180f ? e.z - 360f : e.z;
            float raiseSign = lean > 1f ? -1f : lean < -1f ? 1f : angler.FishingDir;
            e.z = s_baseZ + raiseSign * Current;
            tf.localEulerAngles = e;
            s_applied = tf;
            Applied = raiseSign * Current;
        }
    }
}
