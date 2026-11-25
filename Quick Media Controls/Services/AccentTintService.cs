using System;
using System.Windows.Media;

namespace Quick_Media_Controls.Services
{
    /// <summary>
    /// Text and icon colors derived from the artwork's accent color.
    /// </summary>
    /// <param name="Accent">The artwork accent, used as-is for fills.</param>
    /// <param name="Title">Tint for the title and key icons.</param>
    /// <param name="Subtitle">Tint for secondary text; always lighter than <paramref name="Title"/> in dark mode.</param>
    /// <param name="OnAccent">Very light tint for icons drawn on top of the accent fill.</param>
    public readonly record struct AccentTints(Color Accent, Color Title, Color Subtitle, Color OnAccent);

    public static class AccentTintService
    {
        // Stand-ins for the flyout background, used to check readability.
        private static readonly Color DarkBackground = Color.FromRgb(40, 40, 46);
        private static readonly Color LightBackground = Color.FromRgb(242, 242, 246);

        private static readonly Color White = Color.FromRgb(255, 255, 255);
        private static readonly Color Ink = Color.FromRgb(24, 24, 28);

        private const double TitleMinContrast = 4.5;
        private const double LightModeTitleMinContrast = 6.0;
        private const double SubtitleMinContrast = 4.5;

        /// <summary>
        /// Dark mode: mixes the accent toward white until the text is readable, then a second, lighter tint.
        /// Light mode: mixes toward near-black instead, with the subtitle a little softer than the title.
        /// </summary>
        public static AccentTints Compute(Color accent, bool isDarkMode)
        {
            Color background = isDarkMode ? DarkBackground : LightBackground;
            Color target = isDarkMode ? White : Ink;

            double titleMix = FindMix(accent, target, background, startMix: isDarkMode ? 0.45 : 0.35,
                minContrast: isDarkMode ? TitleMinContrast : LightModeTitleMinContrast);
            Color title = Mix(accent, target, titleMix);

            double subtitleStart = isDarkMode ? Math.Min(0.95, titleMix + 0.2) : Math.Max(0.0, titleMix - 0.2);
            double subtitleMix = FindMix(accent, target, background, subtitleStart, SubtitleMinContrast);
            Color subtitle = Mix(accent, target, subtitleMix);

            Color onAccent = Mix(accent, White, 0.88);

            return new AccentTints(accent, title, subtitle, onAccent);
        }

        /// <summary>Mixes <paramref name="from"/> toward <paramref name="to"/> by <paramref name="amount"/> (0 to 1).</summary>
        public static Color Blend(Color from, Color to, double amount) => Mix(from, to, amount);

        private static double FindMix(Color accent, Color target, Color background, double startMix, double minContrast)
        {
            for (double mix = startMix; mix < 1.0; mix += 0.02)
            {
                if (Contrast(Mix(accent, target, mix), background) >= minContrast)
                {
                    return mix;
                }
            }

            return 1.0;
        }

        private static Color Mix(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0.0, 1.0);
            return Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private static double Contrast(Color a, Color b)
        {
            double la = Luminance(a);
            double lb = Luminance(b);
            double lighter = Math.Max(la, lb);
            double darker = Math.Min(la, lb);
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static double Luminance(Color c)
        {
            static double Linear(byte channel)
            {
                double s = channel / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }
    }
}
