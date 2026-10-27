# 💬 ChatVault Studio — Portable Chat Archive Viewer

A high-performance, zero-dependency, single-file portable Windows application (`ChatVaultStudio.exe` / `WhatsAppChatStudio.exe`) and browser viewer for exploring exported chat archives with instant photo/sticker rendering, infinite-looping media, date-divided galleries, curated typography, and recognizable app themes.

---

## ✨ What's New in v2.1.0

- ⚙️ **Dedicated Studio Settings & Appearance Modal (`Ctrl+,`)**: Declutters the main interface by moving theme cards, font styling, density controls, perspective selectors, and benchmark triggers into an elegant floating modal with backdrop blur.
- 🔤 **Curated Typography Engine**: Choose between 6 high-legibility font families:
  - **Plus Jakarta Sans** — Modern geometric sans-serif (Default)
  - **Inter** — Hyper-clean tech and UI standard
  - **Outfit** — Friendly round geometric
  - **Lexend** — Specially designed for reading fluency
  - **System Native** — Segoe UI (Windows) / Apple System UI
  - **JetBrains Mono** — Developer code terminal monospace
- 📏 **Dynamic Font Size & Bubble Spacing Density**:
  - Live message text slider (`13px – 17px`) with crisp 12px minimum floor for timestamps and badges.
  - Bubble spacing density switcher: **Compact** (`5px × 8px`), **Normal** (`7px × 10px`), or **Spacious** (`10px × 14px`).
- 🎨 **Decluttered Header & Trademark-Free Platform Themes**:
  - 3× horizontal breathing room for chat titles and subtitles without clipping.
  - Recognizable, non-actionable visual themes:
    - **WaChat Emerald** (`#005c4b` / `#10b981` clean minimal)
    - **TelePaper Sky** (`#2b5278` / `#38bdf8` paper-plane style)
    - **CordGuild Blurple** (`#5865f2` / `#818cf8` gaming chat feed)
    - **iBubble Blue** (`#0a84ff` glossy pill bubbles)
    - **InstaGrad Pink** (Purple-to-pink gradient social DM)
  - Quick Dark/Light mode toggle with instant theme accent harmonization.
- 👁️ **Perspective Switcher ("View as")**: Switch between any participant in the conversation so their outgoing messages render in the primary accent bubble on the right.
- 🧩 **Dense Packed Media Grid**: Automatic `grid-flow-row-dense` layout packs small photos and stickers tightly around widescreen video/GIF cards.

---

## 🌍 Universal Chat Archive Compatibility

Automatically detects and parses exports from major messaging platforms:
- **🟢 WaChat Archives** — `.zip` archives and `.txt` logs (both iOS `[d/m/y, h:m:s] Sender: msg` and Android `d/m/y, h:m - Sender: msg` formats, plus `<attached: ...>` and `(file attached)` media).
- **🔵 TelePaper Exports** — JSON exports (`result.json`) and HTML exports (`messages.html`, including photos, animated `.webp` stickers, voice notes, and video files).
- **🟣 CordGuild Packages** — Data exporter `.json` and `.txt` logs as well as official data package `.csv` exports.
- **🩷 InstaGrad & SocialDM** — JSON / HTML exports (including automatic Latin-1/UTF-8 decoding and chronological sorting).
- **💬 TabTalk, SecureNote & Generic Logs** — Tab-separated LINE logs, bracketed Kakao logs, Signal logs, and standard CSV/JSON exports.

---

## ⚡ Engineered for Giant Multi-GB Archives

- **Zero-Extract ZIP Streaming + 768 MB RAM Hot-Cache**: Streams multi-gigabyte `.zip` archives directly from disk via a concurrent `ZipArchive` reader pool while pre-caching photos, stickers, and GIFs into a hard-capped 768 MB in-memory dictionary for `< 0.2ms` lookup.
- **4-Stage Real-Time Import Progress Bar**: Live telemetry for ZIP Central Directory indexing, multi-core RAM pre-caching, message parsing (`300,000+ lines/sec`), and GPU texture pre-decoding.
- **120fps Virtual Sliding-Window DOM + Timeline Scrubber**: Smoothly scrub across 100,000+ messages with constant DOM node count.
- **Date-Divided Chronological Media Bar & Theater Lightbox**:
  - Browse **All Media**, **Photos**, **Stickers**, **Voice & Docs**, and **Stats** grouped by date in chronological order.
  - Full-width widescreen video & GIF cards in the right media bar and `84vw × 74vh` Theater Lightbox scaling so low-resolution videos and GIFs never appear tiny.
  - Automatic binary RIFF `WEBP` (`ANIM`) and `GIF89a` (`NETSCAPE2.0`) loop-count patching + viewable-area `IntersectionObserver` so every sticker, GIF, and video clip loops infinitely while in view.

---

## 🚀 Quick Start

### Option 1: Portable Windows Executable (Recommended)
1. Download and run **`ChatVaultStudio.exe`** (~1.4 MB single-file portable executable, zero installation required).
2. Drag and drop any `.zip` chat archive, unzipped export folder, or `.txt` / `.json` / `.html` log + media files directly onto the window.

### Option 2: Build from Source (Zero External Build Tools Required)
Uses the built-in Windows .NET Framework 4.x C# compiler (`csc.exe`):
```powershell
cd wa-studio-src
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /platform:x64 `
  /out:..\ChatVaultStudio.exe `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll `
  /r:Microsoft.Web.WebView2.Core.dll `
  /res:Microsoft.Web.WebView2.Core.dll,Microsoft.Web.WebView2.Core.dll `
  /res:WebView2Loader.dll,WebView2Loader.dll `
  /res:tailwindcss.min.js,tailwindcss.min.js `
  /res:app.html,app.html `
  Program.cs
```
