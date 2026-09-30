# Changelog

## 1.0.2

- Internal clean-up: the code now follows the Risk of Rain 2 modding community's conventions and no longer catches errors.
- If an entry's "New!" check fails (a broken entry from another mod, for example), the button now stops with the error in the log, instead of skipping that entry.
- Names the game can't save are listed in the log on every press, not only the first time.

## 1.0.1

- Your profile is saved as soon as you press the button, so the marks survive a crash. Before, they were only saved when you closed the logbook, finished a run or quit the game.
- Survivors you haven't unlocked, or whose DLC you don't own, keep their "New!" markers, skills and skins included, so these show once you get the survivor.
- An entry whose "New!" check fails (a broken entry from another mod, for example) no longer stops the button: it is skipped and everything else is marked.
- Entries whose internal names the game can't save (such as names with spaces) are left as they are, instead of showing "New!" again after a restart.
- Risk of Options shows the mod's icon.
- Added a link to the source code: https://github.com/T-Jayay/MarkAllSeen

## 1.0.0

- Initial release: "Mark all as seen" button in the Risk of Options menu.
