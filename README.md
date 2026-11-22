# ClaimCRAFTs

**ClaimCRAFTs v0.3.1** is a free, open-source Windows companion for tracking active public crafting projects in **BitCraft Online** claims. Search for a claim, watch its crafting activity, and see who is crafting—all from a compact desktop overlay.

ClaimCRAFTs is an independent community project. It is not affiliated with or endorsed by the developers of BitCraft Online.

## Features

- **Any claim:** Search for BitCraft claims by name and remember your selection locally.
- **Live crafting:** Direct connection to ResuBaka's public REST API and MessagePack WebSocket, without your own server or API key.
- **Live switch:** Pause/resume automatic updates; manual refresh works even when Live is off.
- **Craft overview:** Public, active, non-passive, unfinished projects grouped by profession and skill, with progress and quantities.
- **Membership indicators:** Green for members with storage permission (or the claim owner), orange for members without storage permission, and red for non-members.
- **Adjustable overlay:** Dark, resizable window with always-on-top, opacity and text shade controls. Preferences are stored locally.
- **Efficient updates:** Applies progress updates without reloading everything, batches notifications, and limits REST refreshes to at least five seconds apart.

## Download and run

**ClaimCRAFTs v0.3.1 is now available!** Download the latest Windows release from the [Releases page](https://github.com/bris701/ClaimCRAFTs/releases/latest). Extract the ZIP archive and run `ClaimCRAFTs.exe`. No separate .NET installation is required.

The planned Windows build is **self-contained**: it includes the .NET runtime and does not require installing .NET separately just to run it. Windows may display an unfamiliar-publisher warning for unsigned community applications; you can instead build the program yourself using the source instructions below.

## Build from source (Windows)

You can build ClaimCRAFTs yourself, even if you've never used Git.

### Requirements

- Windows 10/11, 64-bit
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (**SDK**, not just Runtime)
- Internet connection to restore the NuGet package and to use the app

### Option A: Download source without Git

1. Open this repository's main page and click **Code** → **Download ZIP**.
2. Extract the ZIP to a normal folder, such as `Documents\ClaimCRAFTs`.
3. Open that folder (the folder containing `build.ps1`).
4. Open PowerShell in that folder. On Windows 11, right-click empty space and select **Open in Terminal** (then choose PowerShell if needed).
5. Run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
   ```

6. After a successful build, open `publish\win-x64\ClaimCRAFTs.exe`.

### Option B: Clone using Git

If you have [Git](https://git-scm.com/) installed, you can use:

```powershell
git clone https://github.com/bris701/ClaimCRAFTs.git
cd ClaimCRAFTs
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The build script publishes a **self-contained, single-file Windows x64 executable**. It uses the .NET 10 SDK, and NuGet restores the `MessagePack` 3.1.12 dependency automatically. You do not need Visual Studio to build it. To run the app from source while developing, use `run.ps1` instead.

> **Note:** The application has been built and tested on Windows, including claims with 60+ active crafts. The GitHub Actions workflow also compiles the source on Windows when changes are pushed; this is separate from the self-contained release build.

## Using ClaimCRAFTs

1. Open **Settings** using the gear icon or selected claim name.
2. Search for a claim and choose a match.
3. Return to the overview to monitor its public crafting projects.
4. Use the **Live** switch to enable or pause automatic updates, and the circular refresh icon for a manual refresh.

Only public, active, non-passive, unfinished crafts appear. The badge colors indicate claim membership and storage privileges. Appearance settings are saved automatically.

## Data source and thanks

**A BIG THANK YOU to ResuBaka** for providing API support and valuable feedback during development!

- **BitCraft Hub:** https://craft.resubaka.dev
- **BitCraft Hub source code:** https://github.com/ResuBaka/bitcraft-hub

ClaimCRAFTs talks directly to `https://craft-api.resubaka.dev` and the public MessagePack WebSocket at `wss://craft-api.resubaka.dev/websocket?encoding=MessagePack`. Internet access is required, and live data depends on this community-operated service being available.

## Privacy

ClaimCRAFTs has no user accounts, advertising, or application telemetry. It does not send data to a ClaimCRAFTs-operated server. Requests for claim and crafting data go directly to ResuBaka's service. Locally saved preferences are stored in `%APPDATA%\ClaimCRAFTs\settings.json`; no API keys are embedded or required. If startup fails, a diagnostic log can be saved at `%LOCALAPPDATA%\ClaimCRAFTs\crash.log` and is not uploaded automatically.

## Troubleshooting

- **Build says `dotnet` was not found:** Install the .NET 10 **SDK**, then reopen PowerShell.
- **The executable won't launch:** Look for `%LOCALAPPDATA%\ClaimCRAFTs\crash.log` and check the newest error.
- **No crafting data:** Check your internet connection, try manual refresh, and verify that the selected claim has active public crafts.

For a bug report, include the app version, the steps that reproduce the problem, and the error text (after removing any personal information).

## License

Copyright (C) 2026 **bris701**. ClaimCRAFTs is licensed under the **GNU General Public License, version 3 only** (`GPL-3.0-only`). See [LICENSE](LICENSE) for the full terms.

Third-party libraries retain their respective licenses. In particular, [MessagePack for C#](https://github.com/MessagePack-CSharp/MessagePack-CSharp) is used under the MIT License.
