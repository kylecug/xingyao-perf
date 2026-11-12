# xingyao-perf

Tiny tray app for one laptop: MECHREVO Xingyao 14, board `XINGYAO Series-P916F-PTL`, Core Ultra X7 358H, BIOS 1.01. It switches performance modes and shows monitoring values on the taskbar. Keep it small: one exe built by `build.ps1` with the `csc.exe` in Windows (C# 5 only). The user writes in Chinese.

Protocol details and test results are in `docs/protocol.md`.

Version: `src/Version.cs` and `src/app.manifest` (currently 0.9.19). Commit, push to `main` and tag every change without waiting to be asked (the user's standing request); bump both files on each commit and tag it `vX.Y.Z`.

## Safety

- Writing to firmware or the EC can harm the machine. Only send commands already verified in `docs/protocol.md`, and tell the user before anything new.
- Fan writes `D6` (shared PWM) and `D5 10` (auto) are verified here with `scripts/fan-test.ps1` and used by the tray fan menu (10%, 20% and 30-100%, verified 2026-11-12; with hand-back to the EC at 85 C, unreadable temperature, sleep, exit and start); `D7`/`D8` are untested. The quiet mode has a low stage at a fixed 30% between fans stopped and the EC curve.
- Claude Code runs without admin rights. Anything needing admin must be run by the user in their own administrator window; sending keystrokes to that window is blocked by Windows.

## Status (2026-11-09)

Done and confirmed by the user on the taskbar: three modes, monitoring bars, embedded overlay that survives opening Start.

Reboot auto-start and kept mode are confirmed from logs; the user has not yet confirmed the values on the taskbar after reboot. Not yet confirmed by the user: fullscreen hide, Explorer restart recovery.

## Running without the official background

The official background is currently disabled on this machine with `scripts/vendor-service.ps1` (`-Action status|disable|restore`). Mode switching, monitoring, Fn keys, sleep and wake, reboot, and charging all passed; see `docs/protocol.md`. With the background off nothing limited charging, so the only loss was charge protection, now replaced by the charge limit below.

If anything needed is lost, run `restore`. Before suggesting an uninstall of the official software, check whether it would remove `inpoutx64`, which fan reading needs.

## Battery charge limit

`scripts/battery-limit.ps1` (`-Action status|set|restore`, `-Limit 60|80`) sets the EC charge thresholds; the write sequence is now verified on this machine (see `docs/protocol.md`). Set to 80% on 2026-11-09: charging on AC stopped at 79% (Windows' figure) with no discharge, which the user accepts. With the laptop shut down, a power meter showed only a 1-2 second spike then near zero, also after unplugging for tens of minutes, so the EC enforces it while off. After shutdown and restart the thresholds still read `1,80,100`. Still unknown: a full battery drain or a BIOS/EC update; if charging goes past 80% again, rerun `set`.

The tray menu has a charge limit submenu (60%, 80%, off) using the same commands, with read-back and rollback. Only 80% has been written on this machine; 60% (`F2 60`) and off (`F1 10`, `F3 0`, `F2 0`, no enable) are untested here, so tell the user that before they first use them. The script keeps the original values (`0,0,0`) in `%LOCALAPPDATA%\xingyao-perf\battery-original.txt` for `restore`.
