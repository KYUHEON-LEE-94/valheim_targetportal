using HarmonyLib;

namespace UnifiedTargetPortal.Features;

internal interface IFeatureModule
{
    void Initialize(Harmony harmony);
    void DrawGui();
    void Shutdown();
}

internal interface IUpdatableFeature
{
    void Update();
}
