# Unified Target Portal

A standalone Valheim 1.0 port of the TargetPortal functionality developed in
Valheim Unified. Walk into a portal, choose any reachable destination on the
large map, and click its pin to travel.

## Features

- Server-published portal list, including far-away unloaded zones
- Public/private portals using TargetPortal 1.2.3's original ZDO keys
- Character-local favourite portals and a native map-side favourites list
- `F8` portal-pin toggle on the large map
- Live player and generated location pins in the destination picker
- Configurable portal name length, maximum portal count, animation, item
  restrictions, default visibility and map-pin filtering
- Automatically closes the destination map when the player leaves the portal

Install it on the server/host and on every client that uses destination
selection. It is incompatible with the original `Smoothbrain-TargetPortal`;
do not enable both.

## Configuration

Edit `BepInEx/config/com.xman0922.unifiedtargetportal.cfg`. Defaults match the
behaviour previously shipped in Valheim Unified 0.46.0.

## Source

Source: <https://github.com/KYUHEON-LEE-94/valheim_targetportal>

MIT licensed. TargetPortal compatibility behaviour was implemented by studying
Smoothbrain TargetPortal 1.2.3; no original binary is distributed.
