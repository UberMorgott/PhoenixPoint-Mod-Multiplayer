# 04 — Networking

[← Index](../README.md)

## Options to Research

### Option A — Direct IP

- Direct IP connection between host + clients.
- Requires manual port forwarding / NAT handling.

### Option B — Steam Networking / Steam Relay

- Steam relay networking (SteamNetworkingSockets / SDR).
- **Preferred if available** because:
  - NAT traversal
  - No manual port forwarding
  - Invite code / friend invite support

## Task

- Determine which networking solution is most practical for a Phoenix Point mod.
- Confirm Steamworks availability via the SDK (game is a Steam title → Steamworks.NET / Facepunch.Steamworks likely viable).
- Fallback: direct IP for non-Steam / LAN scenarios.
