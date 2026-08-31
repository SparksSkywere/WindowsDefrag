# Windows Defrag

Disk analysis and defragmentation utility — cluster map, layout methods, SSD/NVMe detection, and TRIM.

## Requirements

- Windows 10/11
- Administrator privileges to move files, TRIM, and run check/fix
- For building: [.NET 8 SDK](https://dotnet.microsoft.com/download)

## Install (release packages)

Build installers:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

The installer creates **Start Menu** and **Desktop** shortcuts under Skywere Industries (both on by default; you can uncheck them).

### GUI

1. Launch **Windows Defrag**
2. **Analyse** a volume to count fragments and paint the cluster map
3. Defrag HDDs with STEALTH / SPACE / COMPLETE, or **TRIM** on SSD/NVMe
4. Double-click a cluster block to inspect files; HTML reports are written after analysis

### CLI

```text
WindowsDefrag.exe --list --no-ui
WindowsDefrag.exe --analyze C --no-ui
WindowsDefrag.exe --defrag C --method SPACE --no-ui
WindowsDefrag.exe --trim C --no-ui
WindowsDefrag.exe --help
```

## License
This repository is licensed under the MIT License as provided in the LICENSE file included with the project.
