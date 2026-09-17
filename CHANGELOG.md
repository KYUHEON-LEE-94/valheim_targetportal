# Changelog

## 1.0.2

- **Fixed the mod stopping during startup**, the actual cause of "no other portal": it patched `TeleportWorldTrigger.OnTriggerExit`, which Valheim 1.0 does not have. Harmony threw inside `Awake`, so everything after that patch, including the portal-list handlers, never started.
- Every patch now goes through a null-safe helper: a method missing from a future Valheim build is logged and skipped instead of stopping the mod.
- Backing out of the portal without choosing still closes the map, now detected by distance from the portal.

## 1.0.1

- Fixed "no other portal" for players connected to a server. The client asked for the portal list once, from `Game.Start`, which can run before the connection finishes; the server only pushed the list when portals changed, so that client could stay empty all session. Clients now keep asking until the list arrives, and the server sends it to every newly connected player.
- Fixed the list never arriving in the second world joined after returning to the main menu. RPC handlers are now registered for each session instead of once per game launch.
- On a server without this mod, the picker falls back to the portals the client already knows about.
- Existing portals are no longer switched to Private when they load. Only newly placed portals get `DefaultPortalMode`, as in TargetPortal; portals without a mode stay Public. Portals already switched by 1.0.0 keep their mode and can be toggled back with Shift + Use.
- An empty picker now says why: still waiting for the server, or other players' private portals hidden.

## 1.0.0

- Split destination portals out of Valheim Unified 0.46.0 into a standalone mod.
- Preserved TargetPortal-compatible public/private ZDO data and favourite data.
- Included destination picking, map pins, favourites, player/location pins,
  portal-exit cleanup, animations and detailed portal settings.
