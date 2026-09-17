using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnifiedTargetPortal.Features;

namespace UnifiedTargetPortal;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInIncompatibility("org.bepinex.plugins.targetportal")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.xman0922.unifiedtargetportal";
    public const string PluginName = "Unified Target Portal";
    public const string PluginVersion = "1.0.2";

    internal static ManualLogSource Log = null!;

    private Harmony? harmony;
    private PortalFeature? feature;

    private void Awake()
    {
        Log = Logger;
        harmony = new Harmony(PluginGuid);
        feature = new PortalFeature(Config);
        feature.Initialize(harmony);
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
    }

    private void Update()
    {
        feature?.Update();
    }

    private void OnDestroy()
    {
        feature?.Shutdown();
        harmony?.UnpatchSelf();
    }
}
