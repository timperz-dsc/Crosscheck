<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand/crosscheck-logo-dark.svg">
  <img src="assets/brand/crosscheck-logo.svg" alt="Crosscheck" width="360">
</picture>

# Crosscheck

Crosscheck is an open-source shared cockpit tool for Microsoft Flight Simulator 2024, starting with the Fenix A320. It keeps two simulators in step so that each pilot sees the same switch positions, knobs and system states as the other. Its core rule is verified sync: every change is read back from the sim and confirmed, never silently assumed. A session server relays and arbitrates changes between pilots, and a Windows bridge on each pilot's machine talks to the sim via SimConnect.

## Status

Phase 0 – spike, not usable yet.

## Planned stack

- Bridge: C# / .NET 8, using SimConnect
- Server: Node.js / TypeScript
- UI: React

## Repository layout

- `spike/` – throwaway experiments against SimConnect and the Fenix A320
- `bridge/` – the per-pilot Windows bridge (C# / .NET 8)
- `server/` – the session server that relays and arbitrates changes (Node / TypeScript)
- `ui/` – the web interface (React)
- `profiles/` – aircraft profiles in YAML
