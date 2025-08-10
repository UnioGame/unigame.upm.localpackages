# UPM Local Packages Tool

**UPM Local Packages Tool** is a Unity Editor extension that makes it easy to work with local UPM packages located outside the `Packages/` folder.  
It allows you to scan, install, and uninstall local packages directly from the Unity Editor without manually editing `manifest.json`.

![UPM Local Packages Tool window](https://i.ibb.co/Gfc72QK0/window.png)

## Installation

```json
 "dependencies": {
    "unigame.upm.localpackages": "https://github.com/UnioGame/unigame.upm.localpackages.git",
  }
```

## Features

- **Scan folders** for local packages (`package.json` files).
- **Display list of found packages** with:
  - Name (`name`) and Display Name
  - Version
  - Installation status (installed / not installed)
  - Installation type (local `file:` or from registry)
  - Relative path for `manifest.json` (relative to the `Packages/` folder)
- **Filtering** — show only not-installed packages.
- **Batch install** selected packages.
- **Install/Uninstall** package directly from details view.
- **Open package folder** in file explorer.
- **Settings**:
  - List of folders to scan (relative to project root).
  - Add/remove scan paths directly from the tool.

## Differences from Unity's built-in Package Manager

- Shows only local packages from specified folders.
- Automatically writes **relative paths from `Packages/`** into `manifest.json` instead of absolute machine paths — making the project portable across different machines.
- One-click uninstall for local packages from `manifest.json`.
- Handy "Open Folder" button.


## Usage

1. Open the window - **UniGame > Tools > UPM Local Packages**

![menu](https://i.ibb.co/bcrGx4X/menu.png)


2. In the **Settings** tab — add folders to scan for local packages (relative to the project root).
3. Click **Scan** — the tool will search for all `package.json` files in specified folders.
4. In the **Details** tab:
- Select a package from the left list.
- Use the buttons:
  - **Open Folder** — open the package folder in file explorer.
  - **Install** — add the package to `manifest.json`.
  - **Uninstall** — remove the package from `manifest.json` (local `file:` packages only).
1. You can select multiple not-installed packages with checkboxes and click **Update Packages** to install them in bulk.


## License
MIT — free to use and modify.
