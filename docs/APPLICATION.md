# Application behavior

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

SnowPatcher detects Steam installations and shows the installed build. A date is
shown only when Steam's public branch identifies that same build.

Compatibility checks run after selection changes. **Check mods** runs a fresh
check; click a result for the affected operation, resource and conflicting mod.
Disabled mods are excluded. Download availability does not prove compatibility.

After a Steam update or repair, open SnowPatcher and check the selection again.
Apply can rebuild compatible patches against verified new originals. Changed
required code stops the patch and needs review. Manual changes removed by Steam
need to be reapplied separately or supplied as SnowPatcher packages.

## Apply, backups and restore

Apply and restore require a closed game and a stable Steam installation.
SnowPatcher verifies and saves starting files, preimages and a backup before
writing. Outputs are staged and checked again before replacement.

Existing manual changes can become the starting files when all patch guards
pass. Once an installation is managed, unexplained external changes block Apply.
Verified Steam originals and recorded historical outputs can be recognized
without treating unknown modifications as safe starting files.

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
