# Validating OneArr against a real Windows library

This procedure checks that an existing Windows media library is recognised without changing any media file.
Nothing here enables automatic grabbing, deletes media, moves files or retires Radarr/Sonarr.

**Status of these tools.** The scripts were exercised end to end in a Linux sandbox against a scratch OneArr
configuration, with generated media files and a seeded catalog. They have **not** been run against a real Windows
installation or a real library. Run them on your PC and record what you see in `PROGRESS.md`.

## Before you start

- PowerShell 7 on the Windows PC: `winget install Microsoft.PowerShell`.
- OneArr running from the build you intend to test, and its admin password.
- The library folders on local or network drives that the OneArr account can read.

## 1. Back up the installation (required)

1. Stop OneArr from the tray (**Stop OneArr**). The backup refuses to run while OneArr is running.
2. Run:

   ```powershell
   .\backup-onearr.ps1 -Destination D:\OneArr-Backups
   ```

   It copies `%LOCALAPPDATA%\OneArr\config` (database with its WAL files, `secret.key`, settings) into a new timestamped
   folder, verifies every copy by SHA-256, and writes `manifest.txt`. It deletes and edits nothing.
3. Keep that folder until the new version has been checked.

## 2. Upgrading

There is no published release containing the library reconciliation work. Installing a build from this branch would
need a release, and a release needs explicit authorisation. Until one exists, keep the installed version and use
the steps below as a read-only check of your library. Do not replace the installed application without a verified backup.

## 3. Add library folders

In OneArr, open **Settings → Library** and add each folder, for example `D:\Media\Movies` (Movies) and `D:\Media\TV` (Series).
The status line shows whether each folder is reachable.

## 4. Path mappings and root folders

Radarr and Sonarr store paths as they see them, often Docker paths such as `/data/media/movies`.
Map each one to the folder on this PC. Several mappings are separated by `;`:

```powershell
.\validate-library.ps1 -Mapping "movie|/data/media/movies|D:\Media\Movies;series|/data/media/tv|D:\Media\TV"
```

Catalog entries whose root folder is a Docker path can be moved to the Windows folder with:

```powershell
.\validate-library.ps1 -RootFrom /data/media/movies -RootTo D:\Media\Movies -Kind movie
```

Both change OneArr's records only. You are asked to type `MOVE` first.

## 5. Preview (no changes)

```powershell
.\validate-library.ps1
```

Enter the OneArr admin password when asked. The script first checks that automatic grabbing is **off** and stops if it is on.
It then prints, and saves to `%LOCALAPPDATA%\OneArr\validation\<time>\`:

- catalog items checked, files discovered, folders read completely or partly
- confirmed matches, ambiguous matches, unmatched files, inaccessible or undecidable files
- items that would become Available, and items that would be marked Missing

Check the ambiguous and unmatched lists before you continue. An incomplete scan stops here.

## 6. Apply (explicit confirmation)

Nothing is written until you type the words the script asks for:

- `MISSING` if items that had a file would be marked missing (they can then be grabbed again);
- `ACKNOWLEDGE` if there are unmatched files (they are not catalog items);
- `APPLY` to write the associations to OneArr's database.

Any other answer changes nothing.

## 7. Verify

- The output reports items now Available, items now Missing, and the wanted list before and after.
- Open five or six titles in OneArr and compare them with the folders on disk.
- Confirm **automatic grabbing is still off** (Settings). This procedure never turns it on.
- Compare the saved `catalog-before.json` and `catalog-after.json` in the output folder.

Media files are never written. To check, hash the library before and after, for example
`Get-ChildItem D:\Media -Recurse -File | Get-FileHash`, and compare the two lists.

## Rollback

- To undo associations: stop OneArr, then copy the backup from step 1 back over `%LOCALAPPDATA%\OneArr\config`.
  Keep the current config folder first, in case you need it again.
- Root folders and mappings can be changed or removed in **Settings → Library** and the Movies/Series pages.
- No media file is changed by any step, so there is nothing to restore on the library itself.
