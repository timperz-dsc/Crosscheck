# Releasing Crosscheck

This page is for the person who publishes a new version. No coding needed.

## How it works in one minute

- Pilots install Crosscheck once with `Crosscheck-win-Setup.exe`. It installs for the current
  Windows user only (no admin rights) and adds Start menu and desktop shortcuts. Remove it under
  Windows Settings → Apps.
- Each time you publish a release, installed copies find it on their own, download it in the
  background and show "Update ready – restart to install" on the start screen. It installs when the
  pilot clicks the button, or at the next start. It never restarts during a session.
- Settings and logs live in `%LOCALAPPDATA%\CrosscheckData`, not in the install folder. Updates and
  uninstall leave them alone.

## 1. Bump the version

Ask Claude: "bump the bridge version to 0.3.1". Claude changes the version in both project files
(`Crosscheck.Bridge.csproj` and `Crosscheck.Bridge.Core.csproj`; they must match) and suggests a
commit message. Then commit and push to `main` in SourceTree.

Rule of thumb while we are below 1.0: two pilots can only join each other if the first two numbers
match (0.3.x with 0.3.x). A bump of the middle number (0.3.4 → 0.4.0) means everybody must update
before they can fly together. Use that only when the protocol changes. A protocol change also needs the
session server redeployed with the same commit before anyone flies with the new bridge (0.5.0 brought
protocol v5: an older server refuses a 0.5.0 bridge, and a 0.4.x bridge gets "out of date" from the new one;
0.6.0 brought protocol v6, the physics stream).

A protocol bump changes, besides the two project files: `ProtocolClient.ProtocolVersion` (bridge),
`PROTOCOL_VERSION` (`server/src/protocol/types.ts`), the `const` in `protocol/schema/common.schema.json`,
`"v"` in every `protocol/examples/*.json`, the server's version in `server/package.json`, and the
expectations in `server/test/22-version-gating.test.ts` and `server/test/fake/fakeBridge.ts`.

## 2. Release

1. Open the repository on GitHub → **Actions** → **Release** (left side) → **Run workflow** →
   choose `main` → **Run workflow**.
2. Wait about 5–10 minutes. Green check = done.
3. The new release appears under **Releases** with `Crosscheck-win-Setup.exe` attached.

If the run fails with "Version … is already released", you forgot step 1: bump the version, push,
and run again. Never reuse a version number.

## 3. Check

- On a PC with the previous version installed: start Crosscheck. After a short while the start
  screen shows "Update ready – restart to install".
- On a PC without Crosscheck: download `Crosscheck-win-Setup.exe` from the latest release and run it.
  Windows SmartScreen may warn because the program is not signed: **More info** → **Run anyway**.

## Trying a build on your own PC

`powershell -File bridge\package.ps1` builds the installer into `dist\releases\` without publishing
anything. It needs `vpk` once: `dotnet tool install -g vpk --version <version>`, where the version
is the `Velopack` version in `bridge/Crosscheck.Bridge/Crosscheck.Bridge.csproj`.
