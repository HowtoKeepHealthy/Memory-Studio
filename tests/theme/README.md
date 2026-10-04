# WPF theme integration check

Run `./scripts/test-theme.ps1` after the native DLL, NASM and DemoTarget have been built. The script copies the app into a unique `artifacts/theme/run-*` directory and does not use or modify the production `app/obj` or `app/bin` outputs.

The fixture scans real self-process memory to populate the main window, opens tool windows offscreen, then switches dark → paper → dark. It checks window colours, the actual generated text of primary buttons and menu headers, enabled/disabled/hover menu states, cached detached menu resources, scrollbar corner colour, font/scale preservation, preference JSON round trips and unchanged personal settings. It also renders PNGs for visual review. No trace session starts and no menu popup appears on the desktop.

The report is `theme-results.txt` in the run directory. The test is an opt-in UI integration check and is not added to the normal build test path.
