# Chrome & Blood Update Notice

Shows a normal MO2 notification when a newer Chrome & Blood list version is published. It offers **Open Wabbajack**, **Close**, and **Don't show this version again**. A later version can still notify you after dismissing an earlier one.

Close MO2, then extract `ChromeBlood-Update-Notice-1.0.zip` into the folder containing `ModOrganizer.exe`. The plugin belongs at `plugins/chrome_blood_update_notice`. It uses MO2's Python and PyQt6 support; the C&B instance uses MO2 2.5.2.

The plugin checks the installed `Chrome & Blood.compiler_settings` file and the public C&B Wabbajack listing. It requires that file's `Version`, `ModListName` and `MachineUrl` to identify the installed list. It stays inactive for other games or installations without matching C&B metadata. A new Nexus revision with the same list version does not trigger a notice.

One asynchronous check runs on startup or re-enable, with a 12-second deadline and a 256 KiB response limit. There is no polling or sign-in. An unavailable or invalid response produces no notice. Disabling the plugin cancels its check and closes any open notice.

Open Wabbajack uses the C&B listing link. If Windows cannot open it, the plugin opens the Nexus collection page instead. Close MO2 and the game before installing an update. The notification does not perform the update or change installed mods.

Tools > Preview Chrome & Blood update notice opens a preview without changing the installed version or recording a dismissal. To uninstall, close MO2 and remove `plugins/chrome_blood_update_notice`.

For Wabbajack authoring, include that plugin folder and the current compiler settings. Exclude its `__pycache__` folder. The Setup executable is not involved.
