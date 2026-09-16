using System;

namespace UnifiedTargetPortal.Features;

/// <summary>
/// One place that decides which language the mod's own UI text uses. Every
/// visible string follows the game's selected language so the added UI reads
/// like the rest of Valheim.
/// </summary>
internal static class Loc
{
    internal static bool IsKorean
    {
        get
        {
            var localization = Localization.instance;
            return localization != null &&
                   string.Equals(localization.GetSelectedLanguage(), "Korean", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string Text(string korean, string english)
    {
        return IsKorean ? korean : english;
    }
}
