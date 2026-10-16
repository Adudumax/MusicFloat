<p align="center">
  <img src="docs/assets/musicfloat-banner.png" alt="MusicFloat banner">
</p>

<h1 align="center">MusicFloat</h1>

<p align="center">
  A lightweight Windows music overlay with SMTC support, customizable track information, shortcuts, and gamepad controls.
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: GPL-3.0-only" src="https://img.shields.io/badge/license-GPL--3.0--only-blue"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4">
</p>

<p align="center">
  English | <a href="README.zh-CN.md">简体中文</a>
</p>

MusicFloat is a local Windows desktop app for players that expose metadata through Windows System Media Transport Controls (SMTC). It shows a compact floating overlay, lets you control playback, and stays out of the game process: no injection, no DirectX hooks, no driver overlay, and no process patching.

The project namespace remains `AppleMusicOverlay` for compatibility with the existing codebase; default builds and official release packages both produce `MusicFloat.exe`.

## Preview

MusicFloat provides a dark desktop control panel with pages for current playback, overlay settings, shortcuts, and common options. The floating overlay displays cover art, title, artist, and the current Favorite state for Apple Music or NetEase CloudMusic.

## Features

- Reads title, artist, playback state, source, and cover art from Windows SMTC.
- Shows a floating overlay when the track changes or when triggered manually.
- Lets users disable automatic track-change popups while keeping manual overlay shortcuts available.
- Supports configurable overlay visibility, duration, size, position, shadow, title and artist visibility, and track information font.
- Supports keyboard and gamepad shortcuts for previous track, next track, play/pause, and showing the current track overlay.
- Routes the same MusicFloat keyboard or gamepad Favorite action by source: Apple Music only adds a Favorite, while NetEase CloudMusic receives its own global Favorite shortcut and repeated actions toggle Like on or off.
- The CloudMusic favorite link at the bottom of MusicFloat's Shortcuts page uses separate modifier and main-key selectors. It must match CloudMusic's global Like shortcut and is used only as a forwarding target, not registered by MusicFloat.
- Detects an already-favorited Apple Music song without removing it or toggling its state.
- Displays Favorite state for Apple Music and NetEase CloudMusic in the overlay.
- When CloudMusic's own global Favorite shortcut or Favorite button is used, CloudMusic performs the real playlist operation and MusicFloat plays the same star motion after confirming the local state. User-customized CloudMusic shortcuts are supported.
- MusicFloat does not use CloudMusic's potentially delayed or low-resolution SMTC thumbnail. It safely resolves the current track from the local playing list, displays only official-CDN artwork measured at 400px or larger, and prefetches the next four queue entries. This accepts legacy CloudMusic albums whose official artwork is capped at 430px while continuing to reject clearly low-resolution thumbnails. On track changes, the overlay waits for verified high-resolution artwork for the current song so the cover, title, artist, and Favorite state appear together without reusing the previous or a low-resolution cover.
- Provides capture source selection when multiple SMTC media sessions are available and defaults to Automatic on first launch. When the selected source closes, the runtime overlay stops showing stale track information regardless of persistent mode.
- Runs locally with a tray icon and optional close-to-tray behavior.
- Uses system-installed fonts only. MusicFloat does not bundle, download, copy, or redistribute Spotify Mix, Apple SF Pro, or other proprietary fonts.

## Download

Download the latest portable Windows x64 build from the [GitHub Releases](https://github.com/Adudumax/MusicFloat/releases/latest) page.

The v1.2.4 portable Windows x64 package includes the .NET runtime. No installer or separate .NET Desktop Runtime installation is required.

## Quick Start

1. Download the latest `MusicFloat-vX.Y.Z-win-x64.zip` release asset.
2. Extract the ZIP to a folder you control.
3. Exit any older MusicFloat process, then run the new `MusicFloat.exe`.
4. Start playback in a supported SMTC media player.
5. Use the control panel to refresh sources, test the overlay, configure overlay behavior, and bind shortcuts.

## Keyboard and Gamepad Controls

MusicFloat supports configurable keyboard and gamepad shortcuts for:

- Previous track
- Next track
- Play / pause
- Show current track overlay
- Favorite the current song in the Apple Music Windows app

Gamepad support depends on devices exposed through Windows gamepad APIs. Shortcut conflict detection, replacement, and automatic saving are handled in the app.

## Build from Source

Requirements:

- Windows 10 version 1809 or newer, or Windows 11
- .NET 8 SDK

Build and test:

```powershell
dotnet restore AppleMusicOverlay.sln
dotnet build AppleMusicOverlay.sln
dotnet test tests/AppleMusicOverlay.Tests/AppleMusicOverlay.Tests.csproj
```

Run from source:

```powershell
dotnet run --project src/AppleMusicOverlay/AppleMusicOverlay.csproj
```

Create a framework-dependent, single-file Windows x64 build named `MusicFloat.exe`:

```powershell
dotnet publish src/AppleMusicOverlay/AppleMusicOverlay.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

## Known Limitations

- MusicFloat depends on Windows SMTC. If Windows does not expose a media session for a player, MusicFloat cannot read or control it.
- Metadata quality depends on the player. Some players may provide missing, delayed, or generic title, artist, or cover information.
- The overlay is a normal Windows desktop overlay. It does not inject into games and may be hidden by exclusive fullscreen modes.
- Optional track information fonts only appear when already installed on the user's system.
- Apple Music Favorite relies on the app's Windows UI Automation accessibility tree because SMTC does not expose Favorite operations.
- Apple Music must remain running and signed in for Favorite state detection and Favorite actions to work.
- NetEase CloudMusic must remain running and signed in with SMTC and global shortcuts enabled. MusicFloat's CloudMusic favorite link must match the shortcut configured in CloudMusic; the default is `Ctrl+Alt+L`. CloudMusic performs the real Like or Unlike operation, then MusicFloat confirms it from the local Favorite playlist state.

## License

MusicFloat is licensed under `GPL-3.0-only`. See [LICENSE](LICENSE).
