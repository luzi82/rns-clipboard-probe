# rns-clipboard-probe

A Rabbit and Steel mod by **luzi82**. Press **F8** to copy your current build and available choices to the clipboard, then paste the text into an AI chat for advice.

The mod copies text locally. It does not contact an AI service or send game data over the network.

## Install

1. Set up [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) for Rabbit and Steel.
2. Install and enable [RNSReloaded](https://github.com/NotNite/RNSReloaded) and its required dependencies. This mod was built against RNSReloaded 1.1.23.
3. Close the game. Extract `rns-clipboard-probe-2.2.0.zip` into Reloaded-II's `Mods` directory. The resulting folder should be `Mods/RNSClipboardProbe/`, containing `ModConfig.json` and `RNSClipboardProbe.dll`.
4. Enable **RNS Clipboard Probe** and launch the game through Reloaded-II.
5. Open a shop or loot selection, press **F8**, and paste the clipboard into your AI chat.

The release build has **F9 disabled**, **no TCP listener**, and **no screenshot/dump implementation**. Restart the game after updating the DLL; a running game continues to use its loaded version.

## Clipboard output

- HP / maximum HP, gold, level, rabbit class, difficulty, and current region ordinal and name.
- Four abilities with separate base and adjusted damage, cooldown, global cooldown, capacity and remaining uses.
- All carried items and the currently open shop or loot choices, including prices.
- Shop gem upgrades with the upgraded ability's description and base stats.
- Both faces of transforming items, grouped as one choice with the current slot form identified.
- Emphasized special terms such as *CHARGES* and *ASTRA*, with related status explanations.

Names and descriptions include the game's original English text alongside the active language. English mode prints English once. Each ability has a complete active-language block followed by an English block, each with both stat groups. Description placeholders use base values; adjusted values are listed separately.

## Current limits

- The snapshot covers the **first player**. Multiplayer participant selection is not implemented.
- The Codices' form counter is shown with an unverified-meaning label until its countdown behavior is confirmed. It is not asserted to be a remaining-use count.
- Shop upgrades show base preview values; they do not predict every interaction after purchase.
- This release has offline capture-replay and binary checks. The public build still needs an in-game check after disabling diagnostics.

The mod reads the installed game's local `Data/*.csv` files. Game files, translations, screenshots, saved runs and RNSReloaded binaries are not included in the downloads.

## Build from source

Use a .NET SDK capable of targeting .NET 9 on Windows. From this repository's root, provide the path to the installed RNSReloaded interfaces DLL:

```powershell
dotnet build RNSClipboardProbe.csproj -c Release -p:RNSReloadedInterfacesDll="C:/path/to/Mods/RNSReloaded/RNSReloaded.Interfaces.dll"
```

Alternatively, build against a separate checkout of RNSReloaded (including `RNSReloaded.Interfaces/`):

```powershell
dotnet build RNSClipboardProbe.csproj -c Release -p:RNSReloadedRepo="C:/path/to/RNSReloaded"
```

Release builds disable diagnostics by default, including when built without the packaging script. To check the resulting DLL:

```powershell
dotnet run --project tests/ReleaseChecks -c Release -- bin/Release/net9.0-windows/RNSClipboardProbe.dll
```

These public tests use synthetic data and require no game files. They check the clipboard output and inspect the compiled assembly to verify that F9, the TCP listener and screenshots are absent.

## Package

```powershell
./scripts/package.ps1 -RNSReloadedInterfacesDll "C:/path/to/Mods/RNSReloaded/RNSReloaded.Interfaces.dll"
```

The script builds with diagnostics explicitly disabled, runs the release checks, and writes the install ZIP, matching source ZIP and `SHA256SUMS.txt` under `dist/`. Copy the install ZIP and source ZIP to a GitHub Release together.

Developer-only F9/TCP usage and the opt-in diagnostic build are documented in [docs/diagnostics.md](docs/diagnostics.md).

## License and credits

Copyright (C) 2026 luzi82. Licensed under **AGPL-3.0-or-later**; see [LICENSE](LICENSE).

Built on RNSReloaded by NotNite and contributors, and Reloaded-II by Sewer56 and contributors. Rabbit and Steel and its game content belong to their respective owners. This is an unofficial mod.
