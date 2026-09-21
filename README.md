# PS7 Studio

A Windows desktop editor for PowerShell 7, built with WinUI 3 and .NET.
Developed by [@nicolux2006](https://github.com/nicolux2006).
This is an independent project, not an official Microsoft product.

## Features

- Tabbed script editing, completion, parameter suggestions and debugging.
- Run scripts directly from unsaved editor tabs.
- An integrated ConPTY terminal with local xterm.js rendering. The editor, terminal and debugger share a PowerShell session.
- Multiple application instances and automatic restoration of editor tabs, including unsaved content.
- English and German interface, configurable editor fonts and light/dark appearance.

## Run the application

Extract the complete application package and start `PS7Studio.exe`. Keep its accompanying files and folders together.

**Microsoft Edge WebView2 Evergreen Runtime is required.** Install it from the [Microsoft download page](https://developer.microsoft.com/microsoft-edge/webview2/). The application displays a message and closes if the runtime is unavailable; it does not install the runtime itself. For offline installation, transfer the Evergreen Standalone Installer to the target computer.

PowerShell, .NET and the Windows App SDK are included in the application package. No separate installation of these components is needed. Local editing and execution work offline once WebView2 is installed; commands that access online services still require a connection.

## Build from source

Use Windows x64 with the **.NET 10 SDK** and **Windows SDK 10.0.26100**. A compatible Visual Studio installation can be used to open `PS7Studio.slnx`.

From a PowerShell terminal in the repository root:

```powershell
.\acquire-runtime.ps1
.\acquire-terminal-runtime.ps1
.\build.ps1
```

The acquisition scripts download pinned dependencies and verify their SHA-256 hashes. The first build also restores NuGet packages, so initial setup requires internet access. Subsequent builds reuse the local caches.

The application is published to `artifacts/app/PS7Studio.exe`. To choose a different output folder:

```powershell
.\build.ps1 -Configuration Release -OutputDirectory artifacts/my-build
```

Use a fresh output folder for distribution. Distribute the whole folder, including its dependency manifest and third-party notices. Put packaged binaries in GitHub Releases rather than committing them to the source repository.

## Source layout

| Project | Purpose |
| --- | --- |
| `PS7Studio.App` | WinUI interface, editor and embedded terminal view |
| `PS7Studio.Core` | Documents, settings, session recovery and PowerShell session integration |
| `PS7Studio.PowerShell` | Private PowerShell and Editor Services runtime bundling |
| `PS7Studio.Terminal` | Windows ConPTY process, terminal input/output and resizing |
| `PS7Studio.Protocol` | Shared protocol message types |
| `PS7Studio.Localization` | English/German resources and language bindings |

`build-icon.ps1` regenerates the Windows icon from `src/PS7Studio.App/Assets/Studio.png`.

Downloaded runtimes, local caches, IDE state and generated build outputs are excluded by `.gitignore`. They can be recreated using the setup and build commands above.

## Dependencies

Runtime acquisition currently pins PowerShell 7.6.6, PowerShell Editor Services 4.7.0, xterm.js 6.0.0 and addon-fit 0.11.0. NuGet versions are specified in the project files. WebView2 uses the separately installed Evergreen Runtime.

Third-party license and notice files are retained alongside vendored assets and included in application packages. Preserve these files when redistributing the application or its dependencies.
