# DeskDock

DeskDock lets you use one Windows mouse and keyboard on an Android device over USB. It sends input directly to Android; it does not extend or mirror the Windows display.

**Status:** early alpha. The current build has been used with a Samsung Galaxy Tab A7 (SM-T505) running Android 12. Other Android versions and device builds still need testing.

## Features

- Move the pointer through the right edge of the Windows display to enter Android.
- Move left through Android's left edge to return to Windows.
- Visible Android mouse pointer with left, right, and middle clicks.
- Vertical and horizontal mouse wheel input.
- Common keyboard keys and modifier combinations.
- `Ctrl` + `Alt` + `F12` releases the mouse and returns control to Windows.

## Requirements

- Windows 10 or 11.
- .NET 10 Desktop Runtime to run the app; .NET 10 SDK to build it.
- Android SDK Platform-Tools (`adb.exe`). DeskDock does not bundle ADB.
- Android Developer options and USB debugging enabled.
- An Android build that provides the shell `uinput` command and allows the ADB shell to register a virtual mouse.

## Run from source

1. Enable USB debugging on Android and connect the device by USB.
2. Unlock the device and accept the USB debugging authorization prompt.
3. From this folder, run:

   ```powershell
   dotnet run --project DeskDock.csproj
   ```

4. Select the device, choose **Connect over USB**, then choose **Start sharing**.
5. Move the Windows pointer through the right edge of the primary display.

If ADB is not found automatically, install Android SDK Platform-Tools and select `adb.exe` with **Browse**.

## Build a Windows app

```powershell
dotnet publish DeskDock.csproj --configuration Release --output .\publish
```

Run `publish\DeskDock.exe` on a Windows machine with the .NET 10 Desktop Runtime installed.

## Compatibility notes

Android manufacturers vary their shell tools and input permissions. Devices without an accessible `uinput` command cannot use DeskDock's visible virtual mouse path. The tablet listed above is the only device verified so far.

## License

No license is included yet. Choose and add a license before inviting outside contributions or granting others permission to reuse the code. Making a GitHub repository public does not itself grant reuse rights.
