# How to use Smash Couch 58.4.0

## What this download contains

[Download the launcher ZIP](https://github.com/eatkinson234/metal-backend/raw/refs/heads/main/smash-couch/downloads/Smash-Couch-58.4.0-macOS-arm64.zip).

This is **the Smash Couch controller-lobby launcher only**. It contains no Smash Bros. game, emulator bundle, keys, firmware, saves or shader cache. It runs locally without an AI service or paid subscription.

**It is a snapshot for an existing compatible setup, not a standalone emulator installer.** The app expects a separately installed, approved native Metal r21 runtime and matching local configuration. A stock Ryujinx installation or a game file by itself does not satisfy its identity gate. The private setup, runtime binaries and game are not included. On a new Mac it may open the lobby but refuse to start the game. Keep the gate intact; do not change its expected hashes to make an unrelated runtime pass.

The binary is Apple Silicon (arm64), with macOS 14.0 as its declared minimum. The complete setup has been validated only on the original development Mac; other Macs, OS versions and displays need their own checks.

## Download and open

1. Download the ZIP and double-click it in Finder to extract `Smash Couch.app`.
2. If you already use Smash Couch, close the game and lobby normally before replacing anything. Keep the working app as a backup. Do not replace or delete your local runtime, save, configuration or cache folders.
3. Keep the extracted app in a convenient folder, such as `Desktop/Games`. This original Mac already has the same 58.4.0 app installed there; downloading another copy is unnecessary for normal play. Run only one lobby at a time.
4. Open `Smash Couch.app`. It uses the existing compatible local setup; moving the launcher does not install that setup.

The ZIP's SHA-256 is recorded in [SHA256SUMS](downloads/SHA256SUMS), with every included file listed in [the archive manifest](downloads/Smash-Couch-58.4.0-ARCHIVE.json). You can check a downloaded copy with:

```sh
shasum -a 256 "$HOME/Downloads/Smash-Couch-58.4.0-macOS-arm64.zip"
```

The app is **ad hoc signed, not Developer ID signed or notarized**. Its code signature was checked before packaging and again after extraction; that does not mean Apple reviewed it. A downloaded copy can be blocked by macOS. If you choose to trust this exact build, follow [Apple's instructions for opening an app from an unknown developer](https://support.apple.com/guide/mac-help/open-a-mac-app-from-an-unknown-developer-mh40616/mac) in System Settings → Privacy & Security. Do not turn off Gatekeeper globally or remove security settings for unrelated apps.

## Connect and join

1. Turn on your controller. If it is not paired, open System Settings → Bluetooth and pair it there. Smash Couch's Bluetooth button opens that pane; it does not pair for you.
2. Click back on the Smash Couch window after pairing. Press and release **any face button** or **Menu** to join. Xbox **A** is an easy choice.
3. Check the player card and animated button diamond to confirm which controller has joined which player. Players can join together; the lobby assigns the available logical slots. Do not infer permanent physical identity from the controller's name or connection order.
4. Release every button so the card becomes ready. Wait at least a second after the last join, then press and release **Menu again** to launch. You can also click the launch button. The first Menu press joins; it should not also start the game.

There are up to eight logical slots. That is not a claim of eight physically connected controllers passing a hardware test. If a game mode displays its own controller-count dialog, use the mouse to close it and report the mode rather than changing emulator settings while it runs.

## Xbox controls

The launcher uses **Nintendo's physical positions**, not matching Xbox letters. These are Smash's usual defaults; a custom control profile inside Smash can change the actions.

| Xbox control | Switch input / usual action |
| --- | --- |
| B (right) | A: confirm in menus / normal attack |
| A (bottom) | B: back in menus / special |
| X (left) | Y: jump |
| Y (top) | X: jump |
| Left stick | Move |
| LT / RT | ZL / ZR: shield with the default game controls |
| Menu | Plus: start/pause in the game |

The old duplicate-attack preset is gone. To make Xbox X attack or choose another scheme, use **Smash → Options → Controls**, then select that named profile for your player before fighting. When Smash opens a name-entry box, type the name with the Mac keyboard. The launcher cannot answer that keyboard dialog with controller presses.

For a ground roll, hold shield and quickly flick the left stick sideways from neutral. Custom in-game bindings can change this. Real shield/combo responsiveness and input-to-screen delay still need physical testing on this new build; do not treat the automated replay tests as that verification.

## Fullscreen, reconnect and quit

- The game launches fullscreen. Use **Control–Command–F** or **Fn/Globe–F** to enter or leave native fullscreen. The lobby supports those shortcuts too.
- If a controller disconnects, reconnect it and deliberately press/release a join button to reclaim a held slot. The lobby or game should be in front for the join. Check the player card; with multiple held slots, the lowest held slot is assigned first, so reconnect one player at a time. Other slots should remain in place. Real multi-controller reconnection on this version is still unverified.
- **View alone in the lobby** leaves the player's slot. Close the game normally with **Command–Q** and allow it to finish; the lobby returns afterward. Then quit the lobby if desired.
- If there is a gate/runtime error, this download has not installed the required local setup. Keep the working installation and report the message. Do not bypass the gate or overwrite saves with older data.

## Expectations and rollback

Warm loading on the original Mac was about 34–38 seconds in the recent recorded checks. A cold graphics cache or new character/effect can take longer or stutter the first time. Do not clear caches on every launch. Up to eight CPU fighters in a test are not eight controller connections.

Current runtime r21 uses the new pacing. The shorter two-frame queue and depth-snapshot memory fixes are **not installed**. An unresolved rare memory surge stopped one isolated test; see [STATUS.md](STATUS.md). This download is not a universal no-stutter/no-crash guarantee.

To undo replacing only the launcher, quit it and restore the launcher backup **paired with its matching runtime**. On the original Mac, the full r21 → r16 rollback is the existing local `work/overnight-correctness-r1/r21-undo.sh`; it restores 58.3.0/r16 and legacy pacing without overwriting game saves. The machine-specific rollback tools and backups are not part of the ZIP. Cache rollback is a separate operation described in the original project's morning note; do not delete current caches or saves to roll back this launcher.
