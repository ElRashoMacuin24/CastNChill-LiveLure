using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace LiveLure
{
    /// <summary>
    /// The HUD's pixel art, drawn in code so the mod ships as a single DLL. Each picture is a grid of palette
    /// letters; one letter is one art pixel, shown at four screen pixels to match the game's own chunky look.
    /// mod-src\preview-art.ps1 renders these same grids to a PNG for checking the art without launching the game.
    /// </summary>
    static class HudArt
    {
        /// <summary>Screen pixels (at the 1080p reference) per art pixel.</summary>
        public const float Unit = 4f;

        // letter, then RRGGBBAA
        public static readonly string[] Palette =
        {
            ". 00000000",
            "k 16161fff", // outline
            "C f0c48aff", // cork, lit side
            "c d9a066ff", // cork
            "d a8733fff", // cork, shaded side
            "s 8a5a2cff", // cork fleck
            "G 6b7391ff", // graphite, lit
            "g 4a5068ff", // graphite
            "h 32364aff", // graphite, shaded
            "W f4f6fbff", // metal glint
            "m c3c8d6ff", // metal
            "M 858ba0ff", // metal, shaded
            "B 6f9463ff", // rod blank, lit
            "b 3d5637ff", // rod blank
            "r c8473fff", // thread wrap
            "y e9bd4eff", // gold trim
            "w e3e6efff", // mouse body
            "n a3a9bcff", // mouse button
            "o ffd75eff", // mouse button, highlighted
        };

        /// <summary>Rod handle pointing straight up: blank and guides, fore grip, reel seat with a spinning reel, rear grip.</summary>
        public static readonly string[] Handle =
        {
            ".....k.............",
            "....kBk............",
            "....kBk............",
            "....kBk............",
            "...kWWmk...........",
            "....kBk............",
            "....kBk............",
            "....kBk............",
            "....kBk............",
            "....kBbk...........",
            "....kBbk...........",
            "...kWWmMk..........",
            "....kBbk...........",
            "....kBbk...........",
            "....kBbk...........",
            "....kBbk...........",
            "....kBbk...........",
            "....krrk...........",
            "....kyyk...........",
            "....kBbk...........",
            "....kBbk...........",
            "...kWmmMk..........",
            "...kCcsdk..........",
            "...kCccdk..........",
            "...kCscdk..........",
            "...kCccdk..........",
            "...kWmmMk..........",
            "...kGgghk...kkkkk..",
            "...kGgghk..kWmmmMk.",
            "...kGgghkkkWmmmmmMk",
            "...kWmmMkMkmmMMMmMk",
            "...kCccdkMkmmMkMmMk",
            "...kCscdkMkmmMMMmMk",
            "...kCccdkkkMmmmmMMk",
            "...kCcsdk..kMMMMMk.",
            "...kCccdk...kkkkk..",
            "...kCscdk..........",
            "...kCccdk..........",
            "...kCcsdk..........",
            "...kCccdk..........",
            "...kWmmMk..........",
            "...kGgghk..........",
            "....kghk...........",
            ".....kk............",
        };
        /// <summary>Where the hand holds it (the reel seat), and the reel's hub, in art pixels from the top-left.</summary>
        public const float HandlePivotX = 6f, HandlePivotY = 28.5f, ReelHubX = 14.5f, ReelHubY = 31.5f;

        /// <summary>The reel's crank arm, drawn pointing right from its pivot.</summary>
        public static readonly string[] Crank =
        {
            "......kkk",
            ".kkkkkkrk",
            "kMmmmmmrk",
            ".kkkkkkrk",
            "......kkk",
        };
        public const float CrankPivotX = 1.5f, CrankPivotY = 2.5f;

        public static readonly string[] MouseRight =
        {
            "..kkkkk..",
            ".knnkook.",
            "knnnkoook",
            "knnnkoook",
            "knnnkoook",
            "kkkkkkkkk",
            "kwwwwwwwk",
            "kwwwwwwwk",
            "kwwwwwwwk",
            "kwwwwwwwk",
            "kwwwwwwwk",
            ".kwwwwwk.",
            "..kkkkk..",
        };

        public static readonly string[] MouseLeft =
        {
            "..kkkkk..",
            ".kooknnk.",
            "koooknnnk",
            "koooknnnk",
            "koooknnnk",
            "kkkkkkkkk",
            "kwwwwwwwk",
            "kwwwwwwwk",
            "kwwwwwwwk",
            "kwwwwwwwk",
            "kwwwwwwwk",
            ".kwwwwwk.",
            "..kkkkk..",
        };

        /// <summary>A fish, drawn pale so the HUD can tint it by mood.</summary>
        public static readonly string[] Fish =
        {
            "....kkkkkk.......",
            "..kkWWWWWWkk...kk",
            ".kWWWWWWWWWWk.kWk",
            "kWkWWWWWWWWWWkWWk",
            "kWWWWWWWWWWWWWWmk",
            "kmmWWWWWWWWWmkmmk",
            ".kmmmmmmmmmmk.kmk",
            "..kkmmmmmmkk...kk",
            "....kkkkkk.......",
        };

        public const int DialSize = 48;

        static Color32 Rgba(byte r, byte g, byte b, byte a)
        {
            Color32 c = default;
            c.r = r; c.g = g; c.b = b; c.a = a;
            return c;
        }

        static Color32 Lookup(char letter)
        {
            foreach (string entry in Palette)
            {
                if (entry[0] != letter) continue;
                uint v = Convert.ToUInt32(entry.Substring(2), 16);
                return Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }
            return Rgba(255, 0, 255, 255);
        }

        static Sprite ToSprite(Color32[] pixels, int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(new Il2CppStructArray<Color32>(pixels));
            tex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(tex);

            Rect rect = default;
            rect.m_XMin = 0f; rect.m_YMin = 0f; rect.m_Width = width; rect.m_Height = height;
            Vector2 pivot = default;
            pivot.x = 0.5f; pivot.y = 0.5f;
            Sprite sprite = Sprite.Create(tex, rect, pivot, 1f);
            UnityEngine.Object.DontDestroyOnLoad(sprite);
            return sprite;
        }

        /// <summary>Builds a sprite from a letter grid. Texture rows run bottom-up, the grid top-down.</summary>
        public static Sprite FromGrid(string[] rows)
        {
            int height = rows.Length, width = rows[0].Length;
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                string row = rows[y];
                for (int x = 0; x < width; x++)
                    pixels[(height - 1 - y) * width + x] = Lookup(x < row.Length ? row[x] : '.');
            }
            return ToSprite(pixels, width, height);
        }

        /// <summary>Colour of one dial pixel. Angles are degrees from straight up, positive toward the water.</summary>
        public static Color32 DialPixel(int x, int y, float lowestDeg, float highestDeg, float restDeg)
        {
            float dx = x + 0.5f - DialSize / 2f, dy = DialSize / 2f - (y + 0.5f);
            float r = MathF.Sqrt(dx * dx + dy * dy);
            if (r > 23.2f) return Rgba(0, 0, 0, 0);
            if (r > 22.1f) return Rgba(22, 22, 31, 255);                      // outline
            if (r > 20.2f) return Rgba(92, 104, 148, 255);                    // rim
            if (r > 19.4f) return Rgba(36, 41, 62, 255);
            float deg = MathF.Atan2(dx, dy) * 57.29578f;
            if (r > 15.2f)
            {
                if (MathF.Abs(deg - restDeg) < 3.2f) return Rgba(244, 246, 251, 255);                    // where the rod rests
                if (deg >= highestDeg && deg <= lowestDeg) return Rgba(104, 196, 210, 240);             // the rod's sweep
                return Rgba(47, 54, 82, 255);
            }
            if (r > 14.4f) return Rgba(36, 41, 62, 255);
            return Rgba(24, 29, 48, 255);                                     // face: solid, so nothing shows through it
        }

        public static Sprite Dial(float lowestDeg, float highestDeg, float restDeg)
        {
            var pixels = new Color32[DialSize * DialSize];
            for (int y = 0; y < DialSize; y++)
                for (int x = 0; x < DialSize; x++)
                    pixels[(DialSize - 1 - y) * DialSize + x] = DialPixel(x, y, lowestDeg, highestDeg, restDeg);
            return ToSprite(pixels, DialSize, DialSize);
        }
    }
}
