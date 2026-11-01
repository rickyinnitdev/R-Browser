# R Browser for Linux (Mint / Ubuntu / Debian)

This is the native Linux port of the **R Browser**, designed for **Linux Mint (GNOME/Cinnamon)** and Debian/Ubuntu-based distributions.

It maintains all three core pillars from [CORE-PRINCIPLES.md](../CORE-PRINCIPLES.md):
1. **Minimalist Chrome**: Clean, dark-glass GTK 4 interface under 64px height. No Electron, no Chromium UI bloatware.
2. **Lowest RAM Usage**: Powered by WebKitGTK 6.0 (~90–140 MB idle private memory). Unused background tabs are automatically suspended, and heap pages are released to the Linux kernel via glibc `malloc_trim(0)`.
3. **Best Ad & Privacy Blocker**: Zero-allocation ABP engine evaluating 120,000+ rules directly in C#, cosmetic element hiding, anti-adblock evasion, and YouTube auto-skipping.

---

## 🛠️ Prerequisites on Linux Mint

Open your terminal on Linux Mint and install the required native libraries and .NET SDK:

```bash
sudo apt update
sudo apt install -y \
    dotnet-sdk-8.0 \
    libgtk-4-1 \
    libwebkitgtk-6.0-4 \
    libglib2.0-0
```

*(Note: If you have .NET 9 installed instead of 8, that works out-of-the-box as well — the project targets both `net9.0` and `net8.0`).*

---

## 🚀 Build & Run

From the project root:

```bash
cd R1.Linux
dotnet build
dotnet run
```

Or open a specific URL:

```bash
dotnet run -- https://duckduckgo.com
```

Or open in Incognito mode:

```bash
dotnet run -- --incognito
```

---

## 📦 Publish a Standalone Binary

To produce a single, self-contained executable that runs on any Linux Mint machine without requiring the .NET SDK:

```bash
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o ./dist-linux
```

The resulting `./dist-linux/RBrowser` binary is ready to run directly.

---

## 🖥️ Desktop Menu Integration (Linux Mint)

To make R Browser appear in your Linux Mint Application Menu (Cinnamon / GNOME):

1. Copy the icon:
```bash
sudo cp ../assets/r1.ico /usr/share/icons/hicolor/256x256/apps/r1.ico
```

2. Create `~/.local/share/applications/r1.desktop`:
```ini
[Desktop Entry]
Name=R Browser
Comment=Ultra-lightweight privacy browser
Exec=/path/to/R1/R1.Linux/dist-linux/RBrowser %U
Icon=/usr/share/icons/hicolor/256x256/apps/r1.ico
Terminal=false
Type=Application
Categories=Network;WebBrowser;
MimeType=text/html;text/xml;application/xhtml+xml;x-scheme-handler/http;x-scheme-handler/https;
```

3. Update the desktop database:
```bash
update-desktop-database ~/.local/share/applications/
```

---

## ⚙️ Data & Configuration

On Linux, R Browser strictly conforms to the **XDG Base Directory Specification**:
- **Settings & Bookmarks**: `~/.local/share/R1/settings.json` and `bookmarks.json`
- **History**: `~/.local/share/R1/history.json`
- **Adblock filter cache**: `~/.local/share/R1/filters.cache.txt`
- **Diagnostics log (optional)**: Set `R1_LOG=1` to write to `~/.local/share/R1/r1.log`
