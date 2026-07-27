# Changelog

## 0.14.7 — 2026-07-27

- Improved VATSIM callsign matching for MSFS traffic with conservative kinematic fallback matching.
- Added stronger ambiguity checks and multi-observation confirmation to reduce incorrect callsign assignments.
- Added a short-range fallback for contacts where SimConnect does not provide complete motion data.
- Added regression tests for offset, ambiguous, and incomplete-motion VATSIM matches.

## 0.14.6 — 2026-07-26

- Switched debug report uploads to direct Vercel Blob client multipart uploads.

## 0.14.5 — 2026-07-23

- Improved VATSIM callsign matching with timestamp-aware historical samples and position interpolation.
- Added monitor-local borderless fullscreen mode for multi-monitor setups.
- Added configurable global hotkeys for Windows joystick/HOTAS buttons alongside XInput controllers.
- Added `Ctrl+Shift+F` as the default fullscreen hotkey.
