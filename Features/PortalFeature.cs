using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Splatform;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnifiedTargetPortal.Features;

/// <summary>
/// Pick a destination when using a portal, replacing Smoothbrain-TargetPortal.
///
/// The server owns the portal list: a client only knows the ZDOs in the zones
/// it has loaded, so it cannot see far-away portals on its own. The server
/// gathers every portal from <see cref="ZDOMan.GetPortalList"/> and pushes
/// (id, tag, position, rotation) to all peers whenever the set changes.
///
/// The picker is Valheim's own large map. Every portal becomes a temporary pin
/// drawn with the game's portal icon (<see cref="Minimap.PinType.Icon4"/>), and
/// clicking a pin teleports. No new UI is built.
/// </summary>
internal sealed class PortalFeature : IFeatureModule, IUpdatableFeature
{
    private const string ListRpc = "VU_PortalList";
    private const string RequestRpc = "VU_PortalRequest";
    private const string ChangeModeRpc = "VU_PortalMode";
    // Use TargetPortal's original keys so worlds previously touched by that
    // mod retain their public/private choices and owner labels.
    private const string ModeKey = "TargetPortal PortalMode";
    private const string OwnerIdKey = "TargetPortal PortalOwnerId";
    private const string OwnerNameKey = "TargetPortal PortalOwnerName";
    private const string FavoritesKey = "TargetPortal Favorites";

    private enum PortalMode
    {
        Public,
        Private,
    }

    private enum ItemRestrictionMode
    {
        Never,
        Default,
        Always,
    }

    private sealed class PortalEntry
    {
        internal ZDOID Id;
        internal string Tag = string.Empty;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal PortalMode Mode;
        internal string OwnerId = string.Empty;
        internal string OwnerName = string.Empty;
    }

    private static PortalFeature? current;
    private static readonly MethodInfo? ScreenToWorldMethod = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint");
    private static readonly MethodInfo? InteractRadiusGetter = AccessTools.PropertyGetter(typeof(Minimap), "PinInteractRadius");

    private readonly ConfigEntry<bool> enabled;
    private readonly ConfigEntry<float> broadcastInterval;
    private readonly ConfigEntry<bool> showUntagged;
    private readonly ConfigEntry<bool> allowPrivatePortals;
    private readonly ConfigEntry<KeyboardShortcut> modeToggleShortcut;
    private readonly ConfigEntry<KeyboardShortcut> mapPinsShortcut;
    private readonly ConfigEntry<bool> showPlayerPinsDuringPortal;
    private readonly ConfigEntry<bool> allowMapPinToggleWhenClosed;
    private readonly ConfigEntry<int> portalNameLength;
    private readonly ConfigEntry<int> maximumNumberOfPortals;
    private readonly ConfigEntry<ItemRestrictionMode> itemRestrictionMode;
    private readonly ConfigEntry<PortalMode> defaultPortalMode;

    private readonly List<PortalEntry> portals = new List<PortalEntry>();
    private readonly List<Minimap.PinData> pickerPins = new List<Minimap.PinData>();
    private readonly Dictionary<Minimap.PinData, PortalEntry> pinToPortal = new Dictionary<Minimap.PinData, PortalEntry>();
    private readonly List<Minimap.PinData> persistentPins = new List<Minimap.PinData>();
    private TeleportWorld? pickerSource;
    private bool pickerFromTrigger;
    private const float LeavePortalDistance = 4f;
    private ConfigEntry<bool> openMapOnEnter = null!;
    private ConfigEntry<bool> portalAnimation = null!;
    private ConfigEntry<bool> hidePinsDuringPortal = null!;
    private bool[]? savedIconTypes;
    private static readonly FieldInfo? VisibleIconTypesField = AccessTools.Field(typeof(Minimap), "m_visibleIconTypes");
    private static readonly FieldInfo? PinUpdateRequiredField = AccessTools.Field(typeof(Minimap), "m_pinUpdateRequired");
    private static readonly FieldInfo? PlacementGhostField = AccessTools.Field(typeof(Player), "m_placementGhost");
    private GameObject? favoriteList;
    // ZRoutedRpc is rebuilt for every world session. A plain "registered" flag
    // survived the return to the main menu, so the second world a client
    // joined never registered its handlers and never received a portal list.
    private ZRoutedRpc? registeredWith;
    private bool rpcRegistered => registeredWith != null && registeredWith == ZRoutedRpc.instance;
    private bool listReceived;
    private float nextListRequest;
    private float sessionStarted;
    private bool showPortalPins;
    private float nextBroadcast;
    private int lastBroadcastHash;

    internal PortalFeature(ConfigFile config)
    {
        enabled = config.Bind("Portals", "Enabled", true,
            "Choose a destination on the map when using a portal. Hold Use to rename instead.");
        broadcastInterval = config.Bind("Portals", "BroadcastInterval", 5f,
            "Seconds between server checks for portal changes.");
        showUntagged = config.Bind("Portals", "ShowUntagged", true,
            "List portals that have no tag.");
        allowPrivatePortals = config.Bind("Portals", "AllowPrivatePortals", true,
            "Allow portal owners to switch between Public and Private with Shift + Use.");
        modeToggleShortcut = config.Bind("Portals", "ModeToggleShortcut",
            new KeyboardShortcut(KeyCode.LeftShift),
            "Modifier held while using a portal to switch its visibility mode.");
        mapPinsShortcut = config.Bind("Portals", "MapPinsShortcut", new KeyboardShortcut(KeyCode.F8),
            "Show or hide portal pins while the large map is open. F8 avoids Valheim 1.0's P-to-ping binding.");
        openMapOnEnter = config.Bind("Portals", "OpenMapOnEnter", true,
            "Walk into a portal to open the destination map, like TargetPortal. Off: press Use to open it instead.");
        portalAnimation = config.Bind("Portals", "PortalAnimation", true,
            "Show the active portal swirl on every portal, since any portal can now reach any other.");
        hidePinsDuringPortal = config.Bind("Portals", "HidePinsDuringPortal", true,
            "Hide other map pins while choosing a destination, so only portals are visible.");
        showPlayerPinsDuringPortal = config.Bind("Portals", "ShowPlayerPinsDuringPortal", true,
            "Keep live player pins visible while choosing a destination, matching TargetPortal.");
        allowMapPinToggleWhenClosed = config.Bind("Portals", "AllowMapPinToggleWhenClosed", false,
            "Allow the portal-pin hotkey while the small map is active, matching TargetPortal's optional setting.");
        portalNameLength = config.Bind("Portals", "PortalNameLength", 10,
            new ConfigDescription("Maximum portal-name length.", new AcceptableValueRange<int>(5, 100)));
        maximumNumberOfPortals = config.Bind("Portals", "MaximumNumberOfPortals", 0,
            new ConfigDescription("Maximum portals allowed in the world. 0 means unlimited.",
                new AcceptableValueRange<int>(0, 10000)));
        itemRestrictionMode = config.Bind("Portals", "IgnoreItemTeleportRestrictions", ItemRestrictionMode.Default,
            "Never: enforce restrictions for every portal. Default: use the portal's vanilla rule. Always: ignore restrictions.");
        defaultPortalMode = config.Bind("Portals", "DefaultPortalMode", PortalMode.Private,
            "Visibility mode assigned to newly built portals.");
    }

    private static bool Active => current?.enabled.Value == true;

    /// <summary>
    /// Patch one method, or log and skip it if this Valheim build does not
    /// have it. A single missing method must never abort initialisation: that
    /// is exactly what left every portal list empty in 1.0.0.
    /// </summary>
    private static void Patch(Harmony harmony, System.Reflection.MethodBase? original,
        HarmonyMethod? prefix = null, HarmonyMethod? postfix = null)
    {
        if (original == null)
        {
            Plugin.Log.LogWarning("TargetPortal: a game method was not found; that part of the feature stays inactive.");
            return;
        }
        try
        {
            harmony.Patch(original, prefix: prefix, postfix: postfix);
        }
        catch (Exception error)
        {
            Plugin.Log.LogError($"TargetPortal: could not patch {original.DeclaringType?.Name}.{original.Name}: {error.Message}");
        }
    }

    public void Initialize(Harmony harmony)
    {
        current = this;
        Patch(harmony,
            AccessTools.Method(typeof(TeleportWorld), nameof(TeleportWorld.Interact)),
            prefix: new HarmonyMethod(typeof(PortalFeature), nameof(InteractPrefix)));
        Patch(harmony,
            AccessTools.Method(typeof(TeleportWorld), nameof(TeleportWorld.GetHoverText)),
            postfix: new HarmonyMethod(typeof(PortalFeature), nameof(HoverTextPostfix)));
        // Existing portals are no longer re-moded on load (see PortalAwakePostfix);
        // only newly placed ones get the default, through Piece.SetCreator.
        Patch(harmony,
            AccessTools.Method(typeof(ZDOMan), "AddPeer"),
            postfix: new HarmonyMethod(typeof(PortalFeature), nameof(ZdoAddPeerPostfix)));
        Patch(harmony,
            AccessTools.Method(typeof(Piece), nameof(Piece.SetCreator)),
            postfix: new HarmonyMethod(typeof(PortalFeature), nameof(PieceSetCreatorPostfix)));
        Patch(harmony,
            AccessTools.Method(typeof(Minimap), "OnMapLeftClick"),
            prefix: new HarmonyMethod(typeof(PortalFeature), nameof(MapLeftClickPrefix)));
        Patch(harmony,
            AccessTools.Method(typeof(Minimap), "RemovePinUnderPointer"),
            prefix: new HarmonyMethod(typeof(PortalFeature), nameof(RemovePinUnderPointerPrefix)));
        Patch(harmony,
            AccessTools.Method(typeof(Minimap), nameof(Minimap.SetMapMode)),
            postfix: new HarmonyMethod(typeof(PortalFeature), nameof(SetMapModePostfix)));
        Patch(harmony,
            AccessTools.Method(typeof(Game), "Start"),
            postfix: new HarmonyMethod(typeof(PortalFeature), nameof(GameStartPostfix)));
        // TargetPortal's core: entering the portal opens the map instead of
        // teleporting to a tag-matched partner.
        Patch(harmony,
            AccessTools.Method(typeof(TeleportWorldTrigger), "OnTriggerEnter"),
            prefix: new HarmonyMethod(typeof(PortalFeature), nameof(TriggerEnterPrefix)));
        // Valheim 1.0's TeleportWorldTrigger has no OnTriggerExit, so leaving
        // the portal is detected from distance in Update instead. Patching the
        // missing method threw inside Awake and stopped the whole mod.
        Patch(harmony,
            AccessTools.Method(typeof(Player), "PlacePiece"),
            prefix: new HarmonyMethod(typeof(PortalFeature), nameof(PlacePiecePrefix)));
        // Every portal can reach every other, so every portal is "connected".
        Patch(harmony,
            AccessTools.Method(typeof(TeleportWorld), "HaveTarget"),
            prefix: new HarmonyMethod(typeof(PortalFeature), nameof(HaveTargetPrefix)));
        Patch(harmony,
            AccessTools.Method(typeof(TeleportWorld), "TargetFound"),
            prefix: new HarmonyMethod(typeof(PortalFeature), nameof(TargetFoundPrefix)));
        // While choosing a destination, double/middle click must not start
        // vanilla pin naming or pings underneath the picker.
        foreach (var name in new[] { "OnMapDblClick", "OnMapMiddleClick" })
        {
            var method = AccessTools.DeclaredMethod(typeof(Minimap), name);
            if (method != null)
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PortalFeature), nameof(BlockWhilePickingPrefix)));
            }
        }
        Plugin.Log.LogInfo($"PortalFeature: {(enabled.Value ? $"enabled, open on {(openMapOnEnter.Value ? "enter" : "use")}" : "disabled")}.");
    }

    // ---- networking -------------------------------------------------------

    private static void GameStartPostfix()
    {
        current?.RegisterRpcs();
    }

    private void RegisterRpcs()
    {
        if (ZRoutedRpc.instance == null || registeredWith == ZRoutedRpc.instance)
        {
            return;
        }

        ZRoutedRpc.instance.Register<ZPackage>(ListRpc, OnPortalList);
        ZRoutedRpc.instance.Register(RequestRpc, OnPortalRequest);
        ZRoutedRpc.instance.Register<ZDOID, int, string, string>(ChangeModeRpc, OnPortalModeChange);
        registeredWith = ZRoutedRpc.instance;
        lastBroadcastHash = 0;
        nextBroadcast = 0f;
        portals.Clear();
        listReceived = false;
        nextListRequest = 0f;
        sessionStarted = Time.time;
        Plugin.Log.LogInfo("TargetPortal: portal list handlers registered for this session.");
    }

    /// <summary>
    /// A client asks the server for the portal list until one arrives. The
    /// single request that used to be sent from Game.Start could run before the
    /// connection finished and be lost, and the server only pushes on change,
    /// so such a client stayed with an empty list for the whole session.
    /// </summary>
    private void UpdateClientList()
    {
        if (ZNet.instance == null || ZNet.instance.IsServer() || listReceived || Time.time < nextListRequest)
        {
            return;
        }
        if (ZNet.instance.GetServerPeer() == null)
        {
            nextListRequest = Time.time + 1f;
            return;
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(RequestRpc);
        var waited = Time.time - sessionStarted;
        nextListRequest = Time.time + (waited < 20f ? 2f : 10f);

        // A server without this mod never answers. Fall back to the portals
        // this client already knows about, so the picker is still usable.
        if (waited > 10f && portals.Count == 0)
        {
            UseLocalPortalList();
        }
    }

    private void UseLocalPortalList()
    {
        if (ZDOMan.instance == null)
        {
            return;
        }
        var package = BuildPackage(out _);
        package.SetPos(0);
        ReadPortalList(package);
    }

    private static void ZdoAddPeerPostfix()
    {
        // Send the list to a newly connected client soon, instead of waiting
        // for the next change to the portal set.
        if (current != null && ZNet.instance != null && ZNet.instance.IsServer())
        {
            current.lastBroadcastHash = 0;
            current.nextBroadcast = Mathf.Min(current.nextBroadcast, Time.time + 2f);
        }
    }

    public void Update()
    {
        UpdateMapControls();
        UpdateLeftPortal();
        if (Active && ZRoutedRpc.instance != null && !rpcRegistered)
        {
            RegisterRpcs();
        }
        if (Active && rpcRegistered)
        {
            UpdateClientList();
        }
        if (!Active || !rpcRegistered || ZNet.instance == null || !ZNet.instance.IsServer() ||
            ZDOMan.instance == null || Time.time < nextBroadcast)
        {
            return;
        }

        nextBroadcast = Time.time + Mathf.Max(1f, broadcastInterval.Value);
        var package = BuildPackage(out var hash);
        if (hash == lastBroadcastHash)
        {
            return;
        }

        lastBroadcastHash = hash;
        Broadcast(package, ZRoutedRpc.Everybody);
    }

    private void OnPortalRequest(long sender)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        Broadcast(BuildPackage(out _), sender);
    }

    private void Broadcast(ZPackage package, long target)
    {
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ListRpc, package);
        // Routed RPCs to Everybody are not echoed to the sender, and a hosting
        // player is also a client of their own world.
        if (target == ZRoutedRpc.Everybody)
        {
            package.SetPos(0);
            OnPortalList(ZNet.GetUID(), package);
        }
    }

    private static ZPackage BuildPackage(out int hash)
    {
        var list = ZDOMan.instance.GetPortalList();
        var package = new ZPackage();
        package.Write(list.Count);
        hash = list.Count;
        foreach (var zdo in list)
        {
            var tag = zdo.GetString(ZDOVars.s_tag);
            var position = zdo.GetPosition();
            package.Write(zdo.m_uid);
            package.Write(tag);
            package.Write(position);
            package.Write(zdo.GetRotation());
            var mode = SquashMode(zdo.GetInt(ModeKey, (int)PortalMode.Public));
            var ownerId = zdo.GetString(OwnerIdKey, string.Empty);
            var ownerName = zdo.GetString(OwnerNameKey, string.Empty);
            package.Write((int)mode);
            package.Write(ownerId);
            package.Write(ownerName);
            hash = unchecked(hash * 31 + zdo.m_uid.GetHashCode());
            hash = unchecked(hash * 31 + tag.GetHashCode());
            hash = unchecked(hash * 31 + Mathf.RoundToInt(position.x) + Mathf.RoundToInt(position.z) * 977);
            hash = unchecked(hash * 31 + (int)mode);
            hash = unchecked(hash * 31 + ownerId.GetHashCode());
        }
        return package;
    }

    private void OnPortalList(long sender, ZPackage package)
    {
        listReceived = true;
        ReadPortalList(package);
    }

    private void ReadPortalList(ZPackage package)
    {
        portals.Clear();
        var count = package.ReadInt();
        for (var index = 0; index < count; index++)
        {
            portals.Add(new PortalEntry
            {
                Id = package.ReadZDOID(),
                Tag = package.ReadString(),
                Position = package.ReadVector3(),
                Rotation = package.ReadQuaternion(),
                Mode = SquashMode(package.ReadInt()),
                OwnerId = package.ReadString(),
                OwnerName = package.ReadString(),
            });
        }
        if (showPortalPins && pickerSource == null)
        {
            RefreshPersistentPins();
        }
    }

    // ---- interaction ------------------------------------------------------

    private static bool InteractPrefix(TeleportWorld __instance, Humanoid human, bool hold, ref bool __result)
    {
        if (!Active || current == null || human != Player.m_localPlayer)
        {
            return true;
        }

        if (!PrivateArea.CheckAccess(__instance.transform.position))
        {
            human.Message(MessageHud.MessageType.Center, "$piece_noaccess");
            __result = true;
            return false;
        }

        if (!hold && current.allowPrivatePortals.Value && current.modeToggleShortcut.Value.IsPressed())
        {
            current.TogglePortalMode(__instance, human);
            __result = true;
            return false;
        }

        if (current.openMapOnEnter.Value)
        {
            // TargetPortal layout: Use renames, walking in picks a destination.
            if (hold)
            {
                __result = false;
                return false;
            }
            if (!CanModifyPortal(__instance, showMessage: true))
            {
                __result = true;
                return false;
            }
            if (!TextInput.IsVisible() && TextInput.instance != null)
            {
                TextInput.instance.RequestText(__instance, "$piece_portal_tag",
                    Mathf.Clamp(current.portalNameLength.Value, 5, 100));
            }
            __result = true;
            return false;
        }

        if (hold)
        {
            if (!CanModifyPortal(__instance, showMessage: true))
            {
                __result = true;
                return false;
            }
            // Long press keeps vanilla's rename, but only opens the box once
            // per press instead of on every held frame.
            if (!TextInput.IsVisible() && TextInput.instance != null)
            {
                TextInput.instance.RequestText(__instance, "$piece_portal_tag",
                    Mathf.Clamp(current.portalNameLength.Value, 5, 100));
            }
            __result = true;
            return false;
        }

        current.OpenPicker(__instance, (Player)human, fromTrigger: false);
        __result = true;
        return false;
    }

    private static readonly FieldInfo? TriggerPortalField = AccessTools.Field(typeof(TeleportWorldTrigger), "m_teleportWorld");

    private static bool TriggerEnterPrefix(TeleportWorldTrigger __instance, Collider colliderIn)
    {
        if (!Active || current == null || !current.openMapOnEnter.Value)
        {
            return true;
        }

        var player = colliderIn.GetComponent<Player>();
        if (player == null || player != Player.m_localPlayer)
        {
            // Vanilla ignores everyone but the local player too.
            return false;
        }
        if (player.IsTeleporting() || current.pickerSource != null)
        {
            // Arriving next to the destination portal must not reopen the map.
            return false;
        }

        if (TriggerPortalField?.GetValue(__instance) is TeleportWorld source)
        {
            current.OpenPicker(source, player, fromTrigger: true);
        }
        return false;
    }

    /// <summary>
    /// TargetPortal closes the picker when the player backs out of the portal
    /// without choosing. Valheim 1.0 has no trigger-exit callback, so check the
    /// distance to the portal the picker was opened from.
    /// </summary>
    private void UpdateLeftPortal()
    {
        if (pickerSource == null || !pickerFromTrigger || Player.m_localPlayer == null)
        {
            return;
        }
        if (Vector3.Distance(Player.m_localPlayer.transform.position, pickerSource.transform.position) <= LeavePortalDistance)
        {
            return;
        }
        if (Minimap.instance != null)
        {
            // Leaving the large map open also makes vanilla reject Tab.
            Minimap.instance.m_dragView = false;
        }
        ClosePicker();
    }

    private static bool PlacePiecePrefix(Player __instance)
    {
        if (!Active || current == null || current.maximumNumberOfPortals.Value <= 0 ||
            PlacementGhostField?.GetValue(__instance) is not GameObject ghost ||
            ghost.GetComponent<TeleportWorld>() == null ||
            current.portals.Count < current.maximumNumberOfPortals.Value)
        {
            return true;
        }

        __instance.Message(MessageHud.MessageType.Center,
            Loc.Text($"이 월드에는 포털을 {current.maximumNumberOfPortals.Value}개보다 더 설치할 수 없습니다.",
                $"You cannot place more than {current.maximumNumberOfPortals.Value} portals in this world."));
        return false;
    }

    private static bool HaveTargetPrefix(ref bool __result)
    {
        if (!Active)
        {
            return true;
        }
        __result = true;
        return false;
    }

    private static bool TargetFoundPrefix(ref bool __result)
    {
        if (!Active || current == null)
        {
            return true;
        }
        __result = current.portalAnimation.Value;
        return false;
    }

    private static bool BlockWhilePickingPrefix()
    {
        return current == null || current.pickerSource == null;
    }

    private static void HoverTextPostfix(TeleportWorld __instance, ref string __result)
    {
        if (!Active)
        {
            return;
        }

        var index = __result.LastIndexOf('\n');
        var head = index >= 0 ? __result.Substring(0, index) : __result;
        var use = Localization.instance.Localize("$KEY_Use");
        if (current?.openMapOnEnter.Value == true)
        {
            __result = head + "\n" +
                       Loc.Text("포털에 들어가면 목적지를 선택합니다", "Walk in to choose a destination") + "\n" +
                       Loc.Text($"[<color=yellow><b>{use}</b></color>] 이름 변경",
                                $"[<color=yellow><b>{use}</b></color>] Rename");
        }
        else
        {
            __result = head + "\n" +
                       Loc.Text($"[<color=yellow><b>{use}</b></color>] 목적지 선택",
                                $"[<color=yellow><b>{use}</b></color>] Choose destination") + "\n" +
                       Loc.Text($"[<color=yellow><b>{use}</b></color> 길게] 이름 변경",
                                $"[Hold <color=yellow><b>{use}</b></color>] Rename");
        }
        if (current?.allowPrivatePortals.Value == true)
        {
            var zdo = __instance.GetComponent<ZNetView>()?.GetZDO();
            var mode = SquashMode(zdo?.GetInt(ModeKey, (int)PortalMode.Public) ?? 0);
            var owner = zdo?.GetString(OwnerNameKey, string.Empty) ?? string.Empty;
            var modeText = ModeText(mode);
            if (mode == PortalMode.Private && !string.IsNullOrEmpty(owner))
            {
                modeText += Loc.Text($" (소유자: {owner.RemoveRichTextTags()})",
                    $" (Owner: {owner.RemoveRichTextTags()})");
            }
            __result += "\n" + Loc.Text($"공개 범위: {modeText}", $"Visibility: {modeText}") +
                        "\n" + Loc.Text($"[<color=yellow><b>Shift + {use}</b></color>] 공개/비공개 전환",
                            $"[<color=yellow><b>Shift + {use}</b></color>] Toggle public/private");
        }
    }

    // Removed: PortalAwakePostfix gave every existing portal the local
    // player had built the default mode (Private) whenever it loaded, which
    // silently hid long-standing portals from everyone else. TargetPortal only
    // assigns the default when a portal is placed; portals without a mode stay
    // Public. Newly placed portals are handled by PieceSetCreatorPostfix.

    private static void PieceSetCreatorPostfix(Piece __instance)
    {
        if (!Active || current?.allowPrivatePortals.Value != true || Player.m_localPlayer == null ||
            __instance.GetComponent<TeleportWorld>() == null)
        {
            return;
        }
        var zdo = __instance.GetComponent<ZNetView>()?.GetZDO();
        if (zdo == null || zdo.GetInt(ModeKey, -1) != -1 ||
            __instance.GetCreator() != Player.m_localPlayer.GetPlayerID())
        {
            return;
        }
        SetMode(zdo, current.defaultPortalMode.Value, LocalOwnerId(), Player.m_localPlayer.GetPlayerName());
        if (current != null)
        {
            current.nextBroadcast = 0f;
        }
    }

    private void TogglePortalMode(TeleportWorld portal, Humanoid human)
    {
        if (!CanModifyPortal(portal, showMessage: true))
        {
            return;
        }
        var zdo = portal.GetComponent<ZNetView>()?.GetZDO();
        if (zdo == null || ZRoutedRpc.instance == null)
        {
            return;
        }
        var next = SquashMode(zdo.GetInt(ModeKey, 0)) == PortalMode.Public
            ? PortalMode.Private
            : PortalMode.Public;
        var ownerName = Player.m_localPlayer?.GetPlayerName() ?? human.GetHoverName();
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ChangeModeRpc,
            zdo.m_uid, (int)next, LocalOwnerId(), ownerName);
        // Everybody is not echoed to a hosting sender, so update the local
        // ZDO immediately as well. The operation is idempotent on clients.
        SetMode(zdo, next, LocalOwnerId(), ownerName);
        human.Message(MessageHud.MessageType.Center,
            Loc.Text($"포털을 {ModeText(next)} 모드로 변경했습니다.",
                $"Portal changed to {ModeText(next)} mode."));
        nextBroadcast = 0f;
    }

    private void OnPortalModeChange(long sender, ZDOID portalId, int rawMode, string ownerId, string ownerName)
    {
        var zdo = ZDOMan.instance?.GetZDO(portalId);
        if (zdo == null)
        {
            return;
        }
        SetMode(zdo, SquashMode(rawMode), ownerId, ownerName);
        nextBroadcast = 0f;
    }

    private static void SetMode(ZDO zdo, PortalMode mode, string ownerId, string ownerName)
    {
        zdo.Set(ModeKey, (int)mode);
        zdo.Set(OwnerIdKey, ownerId);
        zdo.Set(OwnerNameKey, ownerName);
    }

    private static bool CanModifyPortal(TeleportWorld portal, bool showMessage)
    {
        if (PrivateArea.CheckAccess(portal.transform.position))
        {
            return true;
        }
        if (showMessage)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "$piece_noaccess");
        }
        return false;
    }

    private static PortalMode SquashMode(int rawMode) =>
        rawMode == (int)PortalMode.Public ? PortalMode.Public : PortalMode.Private;

    private static string LocalOwnerId() =>
        PlatformManager.DistributionPlatform.LocalUser.PlatformUserID.ToString().Replace("Steam_", string.Empty);

    private static string ModeText(PortalMode mode) => mode == PortalMode.Public
        ? Loc.Text("공개", "Public")
        : Loc.Text("비공개", "Private");

    private void OpenPicker(TeleportWorld source, Player player, bool fromTrigger)
    {
        if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_blocked");
            return;
        }
        if (!CanTeleport(player, source))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_noteleport");
            return;
        }

        var map = Minimap.instance;
        if (map == null)
        {
            return;
        }

        ClearPersistentPins();
        ClearPickerPins();
        if (portals.Count == 0 && ZNet.instance != null && !ZNet.instance.IsServer())
        {
            // Nothing from the server yet: ask now and use what this client knows.
            nextListRequest = 0f;
            UpdateClientList();
            UseLocalPortalList();
        }

        var sourceId = source.GetComponent<ZNetView>()?.GetZDO()?.m_uid ?? ZDOID.None;
        var added = 0;
        var hiddenPrivate = 0;
        foreach (var portal in portals)
        {
            if (portal.Id == sourceId)
            {
                continue;
            }
            if (string.IsNullOrEmpty(portal.Tag) && !showUntagged.Value)
            {
                continue;
            }
            if (allowPrivatePortals.Value && portal.Mode == PortalMode.Private &&
                !string.Equals(portal.OwnerId, LocalOwnerId(), StringComparison.Ordinal))
            {
                hiddenPrivate++;
                continue;
            }

            var name = string.IsNullOrEmpty(portal.Tag)
                ? Loc.Text("(이름 없음)", "(untagged)")
                : portal.Tag.RemoveRichTextTags();
            var pin = map.AddPin(portal.Position, Minimap.PinType.Icon4, name, save: false, isChecked: false);
            pickerPins.Add(pin);
            pinToPortal[pin] = portal;
            added++;
        }

        if (added == 0)
        {
            // Say why, so an empty list is diagnosable in game.
            string message;
            if (portals.Count == 0 && !listReceived && ZNet.instance != null && !ZNet.instance.IsServer())
            {
                message = Loc.Text("서버에서 포털 목록을 받는 중입니다. 잠시 후 다시 들어가 보세요.",
                    "Waiting for the portal list from the server. Try again in a moment.");
            }
            else if (hiddenPrivate > 0)
            {
                message = Loc.Text($"이동할 수 있는 공개 포털이 없습니다. (다른 사람의 비공개 포털 {hiddenPrivate}개 제외)",
                    $"No public portal to travel to ({hiddenPrivate} private portal(s) of other players hidden).");
            }
            else
            {
                message = Loc.Text("이동할 수 있는 다른 포털이 없습니다.", "No other portal to travel to.");
            }
            Plugin.Log.LogInfo($"TargetPortal picker empty: list={portals.Count}, received={listReceived}, " +
                               $"server={ZNet.instance?.IsServer()}, hiddenPrivate={hiddenPrivate}, untaggedShown={showUntagged.Value}.");
            player.Message(MessageHud.MessageType.Center, message);
            ClearPickerPins();
            return;
        }

        pickerSource = source;
        pickerFromTrigger = fromTrigger;
        if (InventoryGui.IsVisible())
        {
            InventoryGui.instance.Hide();
        }
        // Worlds with "no map" still need the picker, as in TargetPortal.
        var noMap = Game.m_noMap;
        Game.m_noMap = false;
        if (fromTrigger)
        {
            map.ShowPointOnMap(source.transform.position);
        }
        else
        {
            map.SetMapMode(Minimap.MapMode.Large);
        }
        Game.m_noMap = noMap;
        HideOtherPins(map);
        BuildFavoriteList();
        player.Message(MessageHud.MessageType.Center,
            Loc.Text("포털 클릭: 이동 · 우클릭: 즐겨찾기", "Click: travel · Right-click: favourite"));
    }

    private static bool MapLeftClickPrefix(Minimap __instance)
    {
        if (current == null || current.pickerSource == null || current.pickerPins.Count == 0)
        {
            return true;
        }

        if (!current.TryGetClosestPickerPortal(__instance, out _, out var destination))
        {
            // Not on a portal pin: let vanilla handle the click (pin placement).
            return true;
        }

        current.Travel(destination);
        return false;
    }

    private static bool RemovePinUnderPointerPrefix(Minimap __instance)
    {
        if (current == null || current.pickerSource == null)
        {
            return true;
        }
        if (!current.TryGetClosestPickerPortal(__instance, out _, out var destination))
        {
            return true;
        }
        current.ToggleFavorite(destination);
        return false;
    }

    private bool TryGetClosestPickerPortal(Minimap map, out Minimap.PinData pin, out PortalEntry portal)
    {
        pin = null!;
        portal = null!;
        if (ScreenToWorldMethod == null || InteractRadiusGetter == null)
        {
            return false;
        }
        var world = (Vector3)ScreenToWorldMethod.Invoke(map, new object[] { ZInput.pointerPosition });
        var radius = (float)InteractRadiusGetter.Invoke(map, null);
        var best = float.MaxValue;
        foreach (var candidate in pickerPins)
        {
            var distance = Utils.DistanceXZ(world, candidate.m_pos);
            if (distance < radius && distance < best)
            {
                pin = candidate;
                best = distance;
            }
        }
        return pin != null && pinToPortal.TryGetValue(pin, out portal);
    }

    private HashSet<string> FavoriteIds()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var player = Player.m_localPlayer;
        if (player == null || !player.m_customData.TryGetValue(FavoritesKey, out var saved))
        {
            return result;
        }
        foreach (var id in saved.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(id);
        }
        return result;
    }

    private void ToggleFavorite(PortalEntry portal)
    {
        var player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }
        var favorites = FavoriteIds();
        var id = portal.Id.ToString();
        var added = favorites.Add(id);
        if (!added)
        {
            favorites.Remove(id);
        }
        player.m_customData[FavoritesKey] = string.Join(",", favorites);
        BuildFavoriteList();
        player.Message(MessageHud.MessageType.Center, added
            ? Loc.Text("포털을 즐겨찾기에 추가했습니다.", "Portal added to favourites.")
            : Loc.Text("포털을 즐겨찾기에서 제거했습니다.", "Portal removed from favourites."));
    }

    private void BuildFavoriteList()
    {
        DestroyFavoriteList();
        var map = Minimap.instance;
        var player = Player.m_localPlayer;
        if (map == null || player == null || pickerSource == null)
        {
            return;
        }
        var rowTemplate = map.m_largeRoot.transform.Find("KeyHints/keyboard_hints/AddPin")?.gameObject;
        if (rowTemplate == null)
        {
            Plugin.Log.LogWarning("Portal favourites: native AddPin key-hint template was not found.");
            return;
        }

        favoriteList = new GameObject("UnifiedTargetPortal Favorites", typeof(RectTransform), typeof(VerticalLayoutGroup));
        favoriteList.transform.SetParent(map.m_largeRoot.transform, false);
        var root = (RectTransform)favoriteList.transform;
        root.anchorMin = new Vector2(0f, 0.5f);
        root.anchorMax = new Vector2(0f, 0.5f);
        root.pivot = new Vector2(0f, 0.5f);
        root.anchoredPosition = new Vector2(15f, 0f);
        root.sizeDelta = new Vector2(240f, 500f);
        var layout = favoriteList.GetComponent<VerticalLayoutGroup>();
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.spacing = 3f;

        var favorites = FavoriteIds();
        foreach (var pair in pinToPortal)
        {
            var portal = pair.Value;
            if (!favorites.Contains(portal.Id.ToString()))
            {
                continue;
            }
            var row = UnityEngine.Object.Instantiate(rowTemplate, favoriteList.transform, false);
            row.name = "FavoritePortal_" + portal.Id;
            row.SetActive(true);
            var horizontal = row.GetComponent<HorizontalLayoutGroup>();
            if (horizontal != null)
            {
                horizontal.childAlignment = TextAnchor.MiddleLeft;
            }
            var label = row.transform.Find("Label")?.GetComponent<TMP_Text>();
            if (label != null)
            {
                label.text = string.IsNullOrEmpty(portal.Tag)
                    ? Loc.Text("(이름 없음)", "(untagged)")
                    : portal.Tag.RemoveRichTextTags();
                label.transform.SetAsLastSibling();
            }
            var icon = row.transform.Find("keyboard_hint")?.GetComponent<Image>();
            if (icon != null)
            {
                icon.sprite = pair.Key.m_icon;
            }
            var click = row.AddComponent<FavoritePortalClick>();
            click.Owner = this;
            click.Portal = portal;
        }
    }

    private sealed class FavoritePortalClick : MonoBehaviour, IPointerClickHandler
    {
        internal PortalFeature Owner = null!;
        internal PortalEntry Portal = null!;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                Owner.ToggleFavorite(Portal);
            }
            else if (eventData.button == PointerEventData.InputButton.Left)
            {
                Owner.Travel(Portal);
            }
        }
    }

    private void UpdateMapControls()
    {
        var map = Minimap.instance;
        if (!Active || map == null)
        {
            return;
        }
        if (pickerSource != null)
        {
            if (showPlayerPinsDuringPortal.Value)
            {
                map.UpdatePlayerPins(Time.deltaTime);
            }
            return;
        }
        var canToggle = map.m_mode == Minimap.MapMode.Large ||
                        allowMapPinToggleWhenClosed.Value && map.m_mode != Minimap.MapMode.None;
        if (!canToggle || !mapPinsShortcut.Value.IsDown())
        {
            return;
        }
        showPortalPins = !showPortalPins;
        if (showPortalPins)
        {
            RefreshPersistentPins();
        }
        else
        {
            ClearPersistentPins();
        }
        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, showPortalPins
            ? Loc.Text("지도에 포털을 표시합니다.", "Portal pins shown.")
            : Loc.Text("지도에서 포털을 숨깁니다.", "Portal pins hidden."));
    }

    private void RefreshPersistentPins()
    {
        ClearPersistentPins();
        var map = Minimap.instance;
        if (map == null)
        {
            return;
        }
        foreach (var portal in portals)
        {
            if (string.IsNullOrEmpty(portal.Tag) && !showUntagged.Value ||
                allowPrivatePortals.Value && portal.Mode == PortalMode.Private &&
                !string.Equals(portal.OwnerId, LocalOwnerId(), StringComparison.Ordinal))
            {
                continue;
            }
            var name = string.IsNullOrEmpty(portal.Tag)
                ? Loc.Text("(이름 없음)", "(untagged)")
                : portal.Tag.RemoveRichTextTags();
            persistentPins.Add(map.AddPin(portal.Position, Minimap.PinType.Icon4, name, false, false));
        }
    }

    private void ClearPersistentPins()
    {
        var map = Minimap.instance;
        foreach (var pin in persistentPins)
        {
            map?.RemovePin(pin);
        }
        persistentPins.Clear();
    }

    private void Travel(PortalEntry destination)
    {
        var player = Player.m_localPlayer;
        var source = pickerSource;
        ClosePicker();
        if (player == null || source == null)
        {
            return;
        }

        // Same gates as TeleportWorld.Teleport, re-checked at click time.
        if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_blocked");
            return;
        }
        if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals) &&
            (RandEventSystem.instance.GetBossEvent() != null ||
             (ZoneSystem.instance.GetGlobalKey(GlobalKeys.activeBosses, out float bosses) && bosses > 0f)))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_blockedbyboss");
            return;
        }
        if (!CanTeleport(player, source))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_noteleport");
            return;
        }

        var forward = destination.Rotation * Vector3.forward;
        var target = destination.Position + forward * source.m_exitDistance + Vector3.up;
        Plugin.Log.LogInfo($"Portal travel to '{destination.Tag}' at {destination.Position}.");
        player.TeleportTo(target, destination.Rotation, distantTeleport: true);
        Game.instance.IncrementPlayerStat(PlayerStatType.PortalsUsed);
    }

    private static void SetMapModePostfix(Minimap.MapMode mode)
    {
        if (current != null && current.pickerSource != null && mode != Minimap.MapMode.Large)
        {
            current.ClosePicker();
        }
    }

    private void HideOtherPins(Minimap map)
    {
        if (!hidePinsDuringPortal.Value || savedIconTypes != null ||
            VisibleIconTypesField?.GetValue(map) is not bool[] visible)
        {
            return;
        }

        savedIconTypes = (bool[])visible.Clone();
        var keepTypes = new HashSet<int> { (int)Minimap.PinType.Icon4 };
        if (showPlayerPinsDuringPortal.Value)
        {
            keepTypes.Add((int)Minimap.PinType.Player);
        }

        // TargetPortal keeps generated location pins (bosses, traders, etc.)
        // even while hiding the player's ordinary pins. Their PinType varies,
        // so identify them by the location sprites already present on the map.
        var locationSprites = new HashSet<Sprite>();
        foreach (var location in map.m_locationIcons)
        {
            if (location.m_icon != null)
            {
                locationSprites.Add(location.m_icon);
            }
        }
        foreach (var pin in map.m_pins)
        {
            if (pin.m_icon != null && locationSprites.Contains(pin.m_icon))
            {
                keepTypes.Add((int)pin.m_type);
            }
        }
        for (var type = 0; type < visible.Length; type++)
        {
            visible[type] = keepTypes.Contains(type);
        }
        PinUpdateRequiredField?.SetValue(map, true);
    }

    private bool CanTeleport(Player player, TeleportWorld source)
    {
        return itemRestrictionMode.Value == ItemRestrictionMode.Always ||
               player.IsTeleportable(itemRestrictionMode.Value == ItemRestrictionMode.Default && source.m_allowAllItems);
    }

    private void RestorePins()
    {
        var map = Minimap.instance;
        if (savedIconTypes == null || map == null || VisibleIconTypesField?.GetValue(map) is not bool[] visible)
        {
            savedIconTypes = null;
            return;
        }

        Array.Copy(savedIconTypes, visible, Math.Min(savedIconTypes.Length, visible.Length));
        savedIconTypes = null;
        PinUpdateRequiredField?.SetValue(map, true);
    }

    private void ClosePicker()
    {
        var wasOpen = pickerSource != null;
        pickerSource = null;
        RestorePins();
        ClearPickerPins();
        if (wasOpen && Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)
        {
            Minimap.instance.SetMapMode(Minimap.MapMode.Small);
        }
        if (showPortalPins)
        {
            RefreshPersistentPins();
        }
    }

    private void ClearPickerPins()
    {
        var map = Minimap.instance;
        foreach (var pin in pickerPins)
        {
            if (map != null)
            {
                map.RemovePin(pin);
            }
        }
        pickerPins.Clear();
        pinToPortal.Clear();
        DestroyFavoriteList();
    }

    private void DestroyFavoriteList()
    {
        if (favoriteList != null)
        {
            UnityEngine.Object.Destroy(favoriteList);
            favoriteList = null;
        }
    }

    public void DrawGui()
    {
    }

    public void Shutdown()
    {
        ClosePicker();
        ClearPersistentPins();
        portals.Clear();
        current = null;
    }
}
