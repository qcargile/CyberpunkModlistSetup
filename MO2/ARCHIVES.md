# Archives Load Order

A native archive-order tab for Cyberpunk 2077 in MO2 2.5.2 on Windows x64. It uses Cyberpunk Conflict Studio's engine and relationship colors.

## Install

1. Close MO2.
2. Extract `ChromeBlood-Archives-1.0.zip` into the folder containing `ModOrganizer.exe`.
3. Check that `plugins/chrome_blood_archives` and `Tools/Conflict Studio/Mo2Bridge/ConflictStudio.Mo2Bridge.exe` exist under that folder.
4. Open MO2 with Cyberpunk 2077 selected. Archives Load Order appears before Data.

Use the bundled MO2 Python support. Keep the Cyberpunk support plugin's `enforce_archive_load_order` option off; it would compete with the profile order written here. Other MO2 versions are not supported by this native-tab integration.

## Use

Higher legacy archives win. Select with Ctrl or Shift, drag to reorder, then choose Save order. The context menu also moves a selection to the top or bottom, selects its owning mod and copies archive names. Selecting an archive selects its mod on the left; selecting a mod selects its archives.

Colors describe the selected archives' resource relationships: green wins, red loses, yellow is mixed, cyan is identical and purple is unknown. Scrollbar markers show where related archives sit. Conflict indicators have tooltips. These show resource precedence, not a verdict that mods are compatible.

REDmod archives are visible but read-only. This tab does not change REDmod order or MO2's left-side mod priorities. The included Conflict Studio companion provides detailed resource and code analysis. It is optional to run.

To use the companion, add `Tools/Conflict Studio/ConflictStudio.exe` as an MO2 executable. Set Start in to the folder containing `ModOrganizer.ini`, use `--manager mo2 --profile current` as the arguments, and add `ConflictStudio.exe` to MO2's executable blacklist so it reads the real files. Keep the companion's folder and licenses together. It is built from the same source as the bridge and supports the profile-order format below.

## Profiles, saves and updates

The existing effective `archive/pc/mod/modlist.txt` supplies the initial order. Save writes `profiles/<profile>/archive-order.txt` in that same text format. MO2 maps it to the game's `archive/pc/mod/modlist.txt` when launching through MO2. The original curated mod file stays intact. The different profile filename prevents Wabbajack from treating archive entries as enabled mod names.

Save verifies current inputs and the result, retains backups and refuses unsafe or stale writes. Undo restores the previous saved file within the current session. New archives appear automatically after the saved entries. Once a profile order exists, disabled archives retain their recorded positions through subsequent saves. The first profile save starts with the active archives.

Mod and profile changes refresh the visible tab; a hidden tab waits until opened. Unsaved drafts survive refresh and profile switching within the session. They do not survive exit. Normal X, Alt-F4 and File Exit warn about drafts. Save before using MO2's native restart or instance-switch commands; those bypass that warning. Active helper work still drains safely.

Mods can be renamed or removed while Archives is open. If file monitoring stops because a location becomes unavailable, the tab asks you to restore access and Refresh. Save still checks the current files before writing.

Earlier `profiles/<profile>/archive-order/modlist.txt` files remain a fallback. The current format is shared with the included Conflict Studio companion. Older standalone Studio builds may not read it; an already-open companion view needs Refresh after external changes, and stale saves are rejected.

Disabling or removing the plugin keeps saved files but stops mapping its profile order. Re-enable it to restore that mapping. To uninstall, close MO2 and remove `plugins/chrome_blood_archives`. Remove `Tools/Conflict Studio/Mo2Bridge` only if no other installed tool uses it. Retain profile orders and backups if wanted.

## Package with Wabbajack

Include the plugin folder and the complete bridge folder. Exclude Python `__pycache__` folders. Wabbajack includes the selected profiles' `archive-order.txt` files normally. For each compiled profile, ignore `profiles/<profile>/archive-order`, which contains retained legacy files and runtime backups.
