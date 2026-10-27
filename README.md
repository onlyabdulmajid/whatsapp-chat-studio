# 💬 Chat Studio Portable — Multi-Platform Chat Archive Viewer

A high-performance, zero-dependency, single-file portable Windows application (`WhatsAppChatStudio.exe`) and standalone browser viewer (`whatsapp-chat-viewer.html`) for exploring exported chat archives with instant photo/sticker rendering, infinite-looping GIFs & stickers, date-divided media galleries, and authentic multi-app visual themes.

---

## ✨ Key Features

### 🌍 Multi-Platform Chat Export Compatibility
Automatically detects and parses exports from major messaging platforms:
- **🟢 WhatsApp** — `.zip` archives and `.txt` logs (both iOS `[d/m/y, h:m:s] Sender: msg` and Android `d/m/y, h:m - Sender: msg` formats, plus `<attached: ...>` and `(file attached)` media).
- **🔵 Telegram Desktop** — JSON exports (`result.json`) and HTML exports (`messages.html`, including photos, animated `.webp` stickers, voice notes, and video files).
- **🟣 Discord** — `DiscordChatExporter` `.json` and `.txt` logs as well as official Discord Data Package `messages.json` / `.csv` exports.
- **🩷 Instagram & Facebook Messenger** — Meta "Download Your Information" `message_1.json` / `.html` exports (including automatic UTF-8 Mojibake decoding and chronological sorting).
- **💬 LINE, KakaoTalk, Signal & Generic CSV/JSON** — Tab-separated LINE logs, bracketed KakaoTalk exports, and standard CSV/JSON message logs.

### 🎨 Authentic Multi-App Themes (Dark & Light)
Switch themes at any time from the top bar (or let the app auto-select the matching theme on import):
- **🟢 WhatsApp** (Dark Emerald `#0b141a` / `#005c4b` & Classic Warm Beige/Mint Light `#efeae2` / `#d9fdd3`)
- **🔵 Telegram** (Night Blue `#0e1621` / `#2b5278` & Classic Sage Light `#d6e4c6` / `#effdde`)
- **🟣 Discord** (Onyx Dark `#313338` / Blurple `#5865f2` & Ash Light `#f2f3f5`)
- **🍎 iMessage** (Apple OLED Dark `#000000` & Clean Light `#f2f2f7` with `#007aff` pill bubbles)
- **🩷 Instagram / Messenger** (Dark Slate & Light with signature Purple-Pink-Rose gradient outgoing bubbles)

### ⚡ Engineered for Giant Multi-GB Archives
- **Zero-Extract ZIP Streaming + 768 MB RAM Hot-Cache**: Streams multi-gigabyte `.zip` archives directly from disk via a concurrent `ZipArchive` reader pool while pre-caching photos, stickers, and GIFs into a hard-capped 768 MB in-memory dictionary (`_ramMediaCache`) for `< 0.2ms` lookup.
- **4-Stage Real-Time Import Progress Bar**: Live telemetry for ZIP Central Directory indexing, multi-core RAM pre-caching, message parsing (`300,000+ lines/sec`), and GPU texture pre-decoding.
- **120fps Virtual Sliding-Window DOM + Timeline Scrubber**: Smoothly scrub across 100,000+ messages with constant DOM node count.
- **Date-Divided Chronological Media Bar & Theater Lightbox**:
  - Browse **All Media**, **Photos**, **Stickers**, **Voice & Docs**, and **Stats** grouped by date in chronological order.
  - Full-width widescreen video & GIF cards in the right media bar and `84vw × 74vh` Theater Lightbox scaling so low-resolution videos and GIFs never appear tiny.
  - Automatic binary RIFF `WEBP` (`ANIM`) and `GIF89a` (`NETSCAPE2.0`) loop-count patching + viewable-area `IntersectionObserver` so every sticker, GIF, and video clip loops infinitely while in view.

---

## 🚀 Quick Start

### Option 1: Portable Windows Executable (Recommended)
1. Download and run **`WhatsAppChatStudio.exe`** (~560 KB single-file portable executable, no installation required).
2. Drag and drop any `.zip` chat archive, unzipped export folder, or `.txt` / `.json` / `.html` log + media files directly onto the window.

### Option 2: Build from Source (Zero External Build Tools Required)
Uses the built-in Windows .NET Framework 4.x C# compiler (`csc.exe`):
```powershell
cd wa-studio-src
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /platform:x64 `
  /out:..\WhatsAppChatStudio.exe `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll `
  /r:Microsoft.Web.WebView2.Core.dll `
  /res:Microsoft.Web.WebView2.Core.dll,Microsoft.Web.WebView2.Core.dll `
  /res:WebView2Loader.dll,WebView2Loader.dll `
  /res:tailwindcss.min.js,tailwindcss.min.js `
  /res:app.html,app.html `
  Program.cs
```
