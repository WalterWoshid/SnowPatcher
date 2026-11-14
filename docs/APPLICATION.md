# Application behavior

## Installation

Windows setup installs for the current user under `LocalAppData/Programs/SnowPatcher`.
Start menu access is on by default; desktop shortcuts and startup at sign-in are optional.
The final page offers to launch the app. No administrator access or separate .NET install is needed.

Setup and uninstall ask you to close SnowPatcher first, including its tray process.
Uninstall removes the program and its shortcuts. Mods, backups, preferences and game files are kept.
The portable ZIP uses the same per-user library and update flow.

## Preferences and background checks

**Options > Preferences** separates update checks from automatic installation.
Checks and closing to the tray are on by default. Automatic installation, system startup
and starting minimized are off. Game updates notify you when mods need reapplying.

Background mode keeps checking after the window is closed. Use the tray menu to
open SnowPatcher, change preferences or exit. **File > Exit** always quits. A second
launch opens the existing window. Startup registration is for the current user.

Game files are checked for changes once a minute; application and mod.io updates
are checked every 30 minutes while SnowPatcher is running. Notifications are quiet
and repeated checks do not repeat an alert. Completion notifications can be disabled.

Automatic reapplication uses the last applied selection. Manual selection changes,
conflicts and interrupted transactions require review. Game writes wait for
SnowRunner to close and the game launcher to finish. Automatic mod updates are checked together
before replacing the working versions; previous ZIPs and game backups are retained.
Queued automatic updates stop if the mod selection or game folder changes.
**Options > Pause automatic actions** pauses the current session without disabling
checks. A file transaction already in progress is allowed to finish.

## SnowPatcher updates

The version button checks GitHub Releases at startup and while the app is open.
When an update is available, click it to download, verify and restart SnowPatcher.
Only application files are replaced. Mod ZIPs, game backups and saves are kept.
Failed installation restores the previous application files.
Background updates postpone restarting if you reopen the app or pause automatic actions.

New mod packages declare their minimum SnowPatcher version. If an update is needed,
**Update & install** keeps the downloaded ZIP, updates SnowPatcher and resumes the
installation after restarting. Compatibility checks and backups still run. If the
game is open or a mod conflicts, installation pauses with an explanation and Retry.
Cancel stops the update and automatic installation. Formats 1 and 2 remain supported.
Version 1.0.0 needs one manual application update to receive this flow.

## Mod library

Add local ZIPs through **Add mods...** or drop them onto the window. SnowPatcher
validates them and keeps its own copies, so the original downloads can be deleted.
Existing external references are copied on startup when their saved hashes match.

A matching package ID updates the existing row and preserves its enabled state.
The previous verified ZIP is retained. Click the version number to choose an
older copy, then Apply. Rebuilt packages can share a version number; the menu
distinguishes those copies by their hashes. Legacy ZIPs have no package ID and
are matched by content only.

Adding, updating, disabling or removing a row changes the selection. Apply
installs that selection. Library changes are saved before replacing an entry;
failed saves keep the previous selection.

The optional SnowPatcher indicator stays first and cannot be removed. It can be
disabled like other mods.

## Checks and game updates

SnowPatcher detects Steam and Epic Games installations. Epic's `en_us` content folder
is selected automatically when needed, including when choosing the installation with Browse.
If both stores are installed, Browse can select the other copy; the choice is remembered.
Epic discovery reads the launcher's local installation records and requires no account login.

Other stores and manual copies can be selected with Browse. No launcher account or
store receipt is required. Content folders are resolved automatically; the chosen
folder is remembered. Compatibility depends on the files each mod patches.

Build information comes from the selected launcher. A release date is shown only
when Steam's public branch identifies the installed Steam build. Epic shows its installed version.
Manual installations show the executable's file version when available, or Unknown.
Their build identity uses executable metadata that code patches leave unchanged.

Compatibility checks run after selection changes. **Check mods** runs a fresh
check; click a result for the affected operation, resource and conflicting mod.
Disabled mods are excluded. Download availability does not prove compatibility.

After a game update or repair, open SnowPatcher and check the selection again.
Apply can rebuild compatible patches against verified new originals. Changed
required code stops the patch and needs review. Manual changes removed by the launcher
need to be reapplied separately or supplied as SnowPatcher packages.

## Apply, backups and restore

Apply and restore require a closed game and stable installation files.
SnowPatcher verifies and saves starting files, preimages and a backup before
writing. Outputs are staged and checked again before replacement.

Existing manual changes can become the starting files when all patch guards
pass. Once an installation is managed, unexplained external changes block Apply.
Verified launcher originals and recorded historical outputs can be recognized
without treating unknown modifications as safe starting files.

Epic's local BuildPatch manifest supplies original-file checksums. When reference data
is missing or unreadable, existing file checks still apply; unexplained changes to an
already managed installation require refreshed reference data or a recorded backup.
Unfinished Epic installs, validation and resumable updates block writes.

Manual installations use the same patch guards, backups and rollback. Without a
launcher manifest, SnowPatcher cannot prove that externally replaced files are
originals. Recorded starting files and historical outputs remain recognizable;
unknown changes to managed files require restoring those starting files or a backup.
Recognized launcher updates still block writes while in progress.

Epic installation handling is checked with launcher-layout fixtures and real Epic
manifest files. In-game behavior on an Epic copy of SnowRunner still needs confirmation;
the Steam build number in a mod package is not evidence of Epic compatibility.

Disabling all mods restores the saved starting files. Reapplying an unchanged
selection leaves game files untouched. Saves and launch options are not changed.

**Backups...** lists snapshots for the installed build. Use the pencil or F2 to
edit a name, Enter/checkmark to save and Escape/X to cancel. A blank name restores
the dated description. Renaming changes metadata only. Restore keeps a backup of
the current archive first; older saved patch outputs remain recognizable.

## Recovery

Apply records a recovery journal before replacing files. If a write fails, only
files still matching that transaction are rolled back. **Recover** completes an
interrupted operation after the game is closed. Changed builds or external edits
stop recovery and retain its copies.

Starting files, history and recovery objects are checksum-verified under
`SnowPatcher/Patches` in local application data. Backups and retained ZIPs are not
automatically deleted.

## In-game indicator

The indicator shows the applied mod count as floating main-menu text. Its count
includes itself. Use SnowPatcher's compatibility check to verify the selection;
the indicator is not a live check of other mods.

See [package APIs](PACKAGES.md#reader-api) and [mod.io setup](MODIO.md) for
development and download configuration.
