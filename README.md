# Cyberpunk Modlist Setup

A small Windows tool that gets your PC ready for a Cyberpunk 2077 modlist install. Pick your list, set your folders, and it runs the tedious setup steps for you: runtimes, a couple of Windows tweaks, the mod-manager download, and a clean-up of any old mod files. Whatever it can't safely do itself, it walks you through with links.

It's built for two lists:

- Chrome & Blood (MO2 / Wabbajack)
- Welcome to Night City (Wabbajack, or a Vortex collection)

## What it does

Pick a list, then how you're installing it (Mod Organizer 2 through Wabbajack, or Vortex through a collection). From there the screen reads top to bottom:

1. **Set your folders** - where the modlist installs and where its mod archives download.
2. **Run the automated steps** - hit "Run all", or run any one on its own:
   - Visual C++ Redistributables, .NET 8 Desktop Runtime, DirectX runtime
   - Enable Windows long paths
   - Set Steam to update the game only on launch
   - Download Wabbajack / install Vortex
   - Clean the game folder back to vanilla (plus the Cyberpunk AppData folders), moved to a reversible backup
3. **Clear the checks** - it reads your system and game and flags anything that needs attention, with a button to get you there:
   - Game installed, on the build the list targets, on the release branch
   - Phantom Liberty and REDmod present
   - Free space, not in a cloud-synced folder, writable and outside Program Files, NTFS for Vortex hardlinks
   - Vortex deployment method and the Cyberpunk extension (read live from Vortex)
4. **Do your part** - the steps only you can do, in the manager's own window. A "Manual guide" button lists every step with links, and a "Health report" button copies a plain status summary you can paste to whoever's helping you.

If you're switching from Vortex to MO2 it spots that and tells you how to clear Vortex out first, and it'll point you at your old downloads so you don't re-download everything.

## Where things go

The tool downloads its own installers (the runtimes, the mod manager) to a private cache under your local AppData, so they never clutter your modlist's downloads folder. Everything it fetches comes straight from Microsoft, the official Wabbajack GitHub, and the official Vortex GitHub - nothing is bundled, and every downloaded exe is signature-checked before it runs.

Nothing is uploaded. No account, no telemetry. It all runs on your PC.

## Reversible

The clean step moves files to a timestamped backup instead of deleting them. "Undo changes" puts back everything the tool touched - the game folder, the AppData folders, and the Steam update setting.

## Running it

A single exe, no install. It needs administrator rights (the runtime installers do), so Windows asks once when it starts.

If Windows SmartScreen shows "Windows protected your PC", click "More info" then "Run anyway". That's normal for a small unsigned tool.

## Building

Needs CMake, the Visual Studio 2022 C++ toolchain, and vcpkg.

    cmake -B build -S . -DCMAKE_TOOLCHAIN_FILE=<path-to-vcpkg>/scripts/buildsystems/vcpkg.cmake
    cmake --build build --config Release --target package

The packaged tool lands in `dist/CyberpunkModlistSetup/`.
