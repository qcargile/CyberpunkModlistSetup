# Chrome & Blood Update Notice

Shows a normal MO2 notification when a newer Chrome & Blood version or a changed Wabbajack package is published. Settings-only updates count when they are compiled and published. It offers **Open Wabbajack**, **Close**, and **Don't show this build again**. Dismissing one build does not hide a later build with the same version number.

Close MO2, then extract `ChromeBlood-Update-Notice-1.0.zip` into the folder containing `ModOrganizer.exe`. The plugin belongs at `plugins/chrome_blood_update_notice`. It uses MO2's Python and PyQt6 support; the C&B instance uses MO2 2.5.2.

The plugin checks the installed `Chrome & Blood.compiler_settings` file and the public C&B Wabbajack listing. It requires that file's `Version`, `ModListName` and `MachineUrl` to identify the installed list. It stays inactive for other games or installations without matching C&B metadata.

On its first successful check, the plugin records the published package hash as its baseline for the installed version. If the published version is already higher than the installed version, it notifies immediately. Otherwise, a different package hash triggers a notice on a later check, even when the version number is unchanged. Installing a different numbered version establishes a new baseline. Changing only Nexus collection metadata does not count as a new Wabbajack package.

Wabbajack's GUI does not record the installed package hash in the MO2 folder. A same-version notice reports a new publication; it cannot confirm whether you have already installed that build. Choose **Don't show this build again** after updating if the notice returns. **Close** reminds you on the next MO2 startup. The first baseline check cannot detect silent updates published before that check. Old version-wide dismissals do not suppress the new build notices.

One asynchronous check runs on startup or re-enable, with a 12-second deadline and a 256 KiB response limit. There is no polling or sign-in. An unavailable or invalid response produces no notice. Disabling the plugin cancels its check and closes any open notice.

Open Wabbajack uses the C&B listing link. If Windows cannot open it, the plugin opens the Nexus collection page instead. Close MO2 and the game before installing an update. The notification does not perform the update or change installed mods.

Tools > Preview Chrome & Blood update notice opens a preview without changing the installed version or recording a dismissal. To uninstall, close MO2 and remove `plugins/chrome_blood_update_notice`.

For Wabbajack authoring, include that plugin folder and the current compiler settings. Exclude its `__pycache__` folder. The Setup executable is not involved.
