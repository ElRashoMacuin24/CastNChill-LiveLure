using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace LiveLure
{
    /// <summary>
    /// The player's hands. Holding the grip button takes hold of the rod: the cursor is pinned in place and mouse
    /// movement is handed to the rod (or to a cast swing) instead. While gripping, the game's own click handling is
    /// suspended and left-click simply reels.
    ///
    /// Buttons and movement are read from Windows rather than Unity's Input System, which in this game only reports
    /// mouse movement while the pointer is over the window.
    /// </summary>
    static class RodInput
    {
        public enum Mode { None, Rod, Cast }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        const int VK_LBUTTON = 1, VK_RBUTTON = 2, VK_MBUTTON = 4, VK_XBUTTON1 = 5, VK_XBUTTON2 = 6, SM_SWAPBUTTON = 23;
        const float TapSeconds = 0.22f, TapPixels = 14f;

        // ---- this frame ----
        public static bool GripDown, GripPressed, GripReleased, ReelDown, ReelPressed;
        /// <summary>Mouse movement this frame in pixels while gripping, +y up.</summary>
        public static float Dx, Dy;
        public static Mode Current;
        /// <summary>The game's own left/right click handling is skipped this frame.</summary>
        public static bool OwnsButtons;
        /// <summary>The bail is open: line runs out freely until any click.</summary>
        public static bool BailOpen;
        /// <summary>The angler the mouse belongs to, as reported by the game's cursor.</summary>
        public static Game.Angler Angler;

        // ---- scripted input for the self-test ----
        public static bool Virtual, VGrip, VReel;
        public static float VDx, VDy;
        /// <summary>Wheel notches to play this frame: positive tightens the drag.</summary>
        public static int VWheel;

        static readonly uint s_pid = (uint)Process.GetCurrentProcess().Id;
        static POINT s_anchor;
        static float s_heldTime, s_travel;
        static bool s_wasGrip, s_wasReel, s_owned;

        static KeyControl s_toggleControl, s_sensDownControl, s_sensUpControl, s_dragLooserControl, s_dragTighterControl;
        static string s_toggleName, s_sensDownName, s_sensUpName, s_dragLooserName, s_dragTighterName;
        static float s_nextKeyResolve;

        public static bool GripIsRightMouse => GripVk() == VK_RBUTTON;

        /// <summary>The rod is in the player's hand, or still settling back after being let go.</summary>
        public static bool HoldingRod => Current == Mode.Rod || Math.Abs(RodPose.Current) > 0.5f;

        /// <summary>True while the game is the window the player is actually using.</summary>
        public static bool GameHasFocus()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == s_pid;
        }

        static int GripVk()
        {
            switch ((Settings.GripButton.Value ?? "").Trim().ToLowerInvariant())
            {
                case "middlemouse": return VK_MBUTTON;
                case "mouse4": return VK_XBUTTON1;
                case "mouse5": return VK_XBUTTON2;
                default: return VK_RBUTTON;
            }
        }

        static bool Down(int vk)
        {
            // GetAsyncKeyState reports physical buttons; honour a left-handed button swap
            if (GetSystemMetrics(SM_SWAPBUTTON) != 0)
            {
                if (vk == VK_LBUTTON) vk = VK_RBUTTON;
                else if (vk == VK_RBUTTON) vk = VK_LBUTTON;
            }
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        static bool Is(Game.Angler.AnglerState a, Game.Angler.AnglerState b)
        {
            return a != null && b != null && a.Pointer == b.Pointer;
        }

        /// <summary>The player (not idle mode) is the one fishing.</summary>
        public static bool Human(Game.Angler angler)
        {
            if (angler == null || angler.isCustomiserAngler) return false;
            if (SelfTest.ForceHuman) return true;
            try { return !Game.IdleManager.IsActive; } catch (Exception) { return true; }
        }

        public static bool IsFishingState(Game.Angler angler)
        {
            return Is(angler.State, angler.States.RodFishing);
        }

        /// <summary>Called first thing every frame.</summary>
        public static void Update()
        {
            HandleToggleKey();
            Poll();
            Step();
            HandleDrag();
        }

        /// <summary>The mouse wheel (or two keys) turns the reel's drag knob, but only while a rod is out, so menus keep their scrolling.</summary>
        static void HandleDrag()
        {
            int steps = 0;
            if (Virtual) { steps = VWheel; VWheel = 0; }
            else if (GameHasFocus())
            {
                if (Settings.WheelDrag.Value) steps += WheelNotches();
                if (Pressed(s_dragTighterControl)) steps++;
                if (Pressed(s_dragLooserControl)) steps--;
            }
            if (steps == 0 || !Settings.Enabled.Value || !Settings.FightMechanics.Value) return;
            Game.Angler angler = Angler;
            if (angler == null || !Human(angler)) return;
            Game.Angler.AnglerState state = angler.State;
            Game.Angler.AnglerStates states = angler.States;
            if (!(Is(state, states.RodFishing) || Is(state, states.RodIdle) || Is(state, states.RodAiming)
                  || Is(state, states.RodCasting) || Is(state, states.RodReleaseLine))) return;
            Fight.AdjustDrag(steps);
        }

        static int WheelNotches()
        {
            try
            {
                Mouse mouse = Mouse.current;
                if (mouse == null) return 0;
                float y = mouse.scroll.y.ReadValue();
                if (Math.Abs(y) < 0.01f) return 0;
                // Depending on the Input System's settings a notch reads as 1 or as Windows' 120.
                int notches = Math.Max(1, (int)MathF.Round(Math.Abs(y) / 120f));
                return y > 0f ? notches : -notches;
            }
            catch (Exception) { return 0; }
        }

        static void Poll()
        {
            bool grip, reel;
            float dx = 0f, dy = 0f;
            if (Virtual)
            {
                grip = VGrip; reel = VReel;
                dx = VDx; dy = VDy; VDx = VDy = 0f;
            }
            else
            {
                bool focused = GameHasFocus();
                grip = focused && Down(GripVk());
                reel = focused && Down(VK_LBUTTON);
                if (GetCursorPos(out POINT p))
                {
                    if (grip && s_wasGrip && s_owned)
                    {
                        dx = p.X - s_anchor.X;
                        dy = -(p.Y - s_anchor.Y);
                        if (p.X != s_anchor.X || p.Y != s_anchor.Y) SetCursorPos(s_anchor.X, s_anchor.Y); // keep the pointer where it was grabbed
                    }
                    else s_anchor = p;
                }
            }

            GripPressed = grip && !s_wasGrip;
            GripReleased = !grip && s_wasGrip;
            ReelPressed = reel && !s_wasReel;
            GripDown = grip; ReelDown = reel;
            s_wasGrip = grip; s_wasReel = reel;
            Dx = dx; Dy = dy;
        }

        static void Step()
        {
            Game.Angler angler = Angler;
            if (angler == null)
            {
                try { angler = Angler = Game.IdleManager.Angler; } catch (Exception) { }
            }
            bool usable = Settings.Enabled.Value && angler != null;
            if (!usable)
            {
                Current = Mode.None; s_owned = false; OwnsButtons = false; BailOpen = false;
                Dx = Dy = 0f;
                return;
            }
            bool fishing = IsFishingState(angler);

            // the bail snaps shut on any click, or when there is no longer a line in the water to feed
            if (BailOpen && (GripPressed || ReelPressed || !fishing)) CloseBail();

            if (GripPressed)
            {
                s_heldTime = 0f; s_travel = 0f;
                Current = Mode.None;
                if (!Human(angler))
                {
                    try { Game.IdleManager.UserInteracted(); } catch (Exception) { } // grabbing the rod wakes the game from idle mode
                }
                else Current = Decide(angler);
                if (Current == Mode.Cast) Casting.Begin(angler);
                s_owned = Current != Mode.None;
            }

            OwnsButtons = s_owned;
            if (s_owned)
            {
                if (GripDown)
                {
                    s_heldTime += Time.unscaledDeltaTime;
                    s_travel += Math.Abs(Dx) + Math.Abs(Dy);
                    if (Current == Mode.Cast && !Casting.Active) Current = Mode.Rod; // the lure is away: keep hold of the rod
                }
                if (GripReleased)
                {
                    bool tap = Current == Mode.Rod && fishing && s_heldTime < TapSeconds && s_travel < TapPixels;
                    if (tap && Settings.BailTap.Value && GripIsRightMouse) OpenBail();
                    Current = Mode.None;
                }

                // While the buttons are ours, left-click simply reels whenever there is a line out. This also covers
                // letting go of the grip but keeping left-click down: the reel keeps turning, and once the lure is
                // in nothing else happens until every button is released, so a held click cannot start a cast.
                if (!Casting.Active) angler.InteractInput = fishing ? (ReelDown ? 1f : BailOpen ? -1f : 0f) : 0f;
                if (!GripDown && !ReelDown) s_owned = false;
            }
            else if (BailOpen) angler.InteractInput = -1f;

            if (!GripDown) { Dx = 0f; Dy = 0f; }
        }
        static Mode Decide(Game.Angler angler)
        {
            Game.Angler.AnglerState state = angler.State;
            Game.Angler.AnglerStates states = angler.States;
            if (Is(state, states.RodFishing) || Is(state, states.RodCasting) || Is(state, states.RodReleaseLine)) return Mode.Rod;
            if (Is(state, states.RodIdle) && Settings.MouseCast.Value && angler.CanCast) return Mode.Cast;
            return Mode.None;
        }

        static void OpenBail()
        {
            BailOpen = true;
            Plugin.Note("bail open - line running out");
        }

        static void CloseBail()
        {
            BailOpen = false;
            try { if (Angler != null && Angler.InteractInput < 0f) Angler.InteractInput = 0f; } catch (Exception) { }
            Plugin.Note("bail closed");
        }

        static void HandleToggleKey()
        {
            if (s_toggleName != Settings.ToggleKey.Value || s_sensDownName != Settings.SensitivityDownKey.Value
                || s_sensUpName != Settings.SensitivityUpKey.Value || s_dragLooserName != Settings.DragLooserKey.Value
                || s_dragTighterName != Settings.DragTighterKey.Value || Time.unscaledTime >= s_nextKeyResolve)
            {
                s_nextKeyResolve = Time.unscaledTime + 2f; // keyboards can be re-plugged
                s_toggleName = Settings.ToggleKey.Value;
                s_sensDownName = Settings.SensitivityDownKey.Value;
                s_sensUpName = Settings.SensitivityUpKey.Value;
                s_dragLooserName = Settings.DragLooserKey.Value;
                s_dragTighterName = Settings.DragTighterKey.Value;
                s_toggleControl = FindKey(s_toggleName);
                s_sensDownControl = FindKey(s_sensDownName);
                s_sensUpControl = FindKey(s_sensUpName);
                s_dragLooserControl = FindKey(s_dragLooserName);
                s_dragTighterControl = FindKey(s_dragTighterName);
            }

            if (Pressed(s_toggleControl))
            {
                Settings.Enabled.Value = !Settings.Enabled.Value;
                Plugin.Logger.LogMessage("Live Lure " + (Settings.Enabled.Value ? "enabled" : "disabled"));
                if (!Settings.Enabled.Value) { BailOpen = false; Current = Mode.None; s_owned = false; }
            }

            float change = Pressed(s_sensUpControl) ? 1.15f : Pressed(s_sensDownControl) ? 1f / 1.15f : 0f;
            if (change > 0f && Settings.Enabled.Value)
            {
                Settings.Sensitivity.Value = Math.Clamp(Settings.Sensitivity.Value * change, 0.1f, 8f);
                LureController.Hud.Say("MOUSE SENSITIVITY  " + Settings.Sensitivity.Value.ToString("0.00"), 1.5f);
                Plugin.Note("mouse sensitivity " + Settings.Sensitivity.Value.ToString("0.00"));
            }
        }

        static bool Pressed(ButtonControl c)
        {
            try { return c != null && c.wasPressedThisFrame; } catch (Exception) { return false; }
        }
        /// <summary>"F8" -> Keyboard.current.f8Key, "LeftShift" -> leftShiftKey.</summary>
        static KeyControl FindKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var keyboard = Keyboard.current;
                if (keyboard == null) return null;
                name = name.Trim();
                string prop = char.ToLowerInvariant(name[0]) + name.Substring(1) + "Key";
                PropertyInfo p = typeof(Keyboard).GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                return p?.GetValue(keyboard) as KeyControl;
            }
            catch (Exception) { return null; }
        }
    }
}
