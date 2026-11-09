# xingyao-perf

Tiny tray app for one laptop: MECHREVO Xingyao 14, board `XINGYAO Series-P916F-PTL`, Core Ultra X7 358H, BIOS 1.01. It switches performance modes and shows monitoring values on the taskbar. Keep it small: one exe built by `build.ps1` with the `csc.exe` in Windows (C# 5 only). The user writes in Chinese.

Protocol details and test results are in `docs/protocol.md`.

## Safety

- Writing to firmware or the EC can harm the machine. Only send commands already verified in `docs/protocol.md`, and tell the user before anything new.
- Fan writes are documented but have never been tested here.
- Claude Code runs without admin rights. Anything needing admin must be run by the user in their own administrator window; sending keystrokes to that window is blocked by Windows.

## Status (2026-11-09)

Done and confirmed by the user on the taskbar: three modes, monitoring bars, embedded overlay that survives opening Start.

Not yet confirmed by the user: reboot auto-start and mode restore, fullscreen hide, Explorer restart recovery.

## Next step: try running without the official background

Use `scripts/vendor-service.ps1` (`-Action status|disable|restore`). The user runs `disable`, then check:

1. Mode switching and all monitoring values still work.
2. After a reboot: app auto-starts, chosen mode is kept, and temperature, fans and GPU values still read.
3. After sleep and wake: the chosen mode is kept.
4. Fn keys: mode key, brightness, volume, keyboard backlight, touchpad toggle.
5. Which official features are lost, such as battery charge protection.

If anything needed is lost, run `restore`. Do not suggest uninstalling the official software until these pass. An uninstall might also remove `inpoutx64`, which fan reading needs, so check that first.
