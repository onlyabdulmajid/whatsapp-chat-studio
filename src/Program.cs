using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace ChatVaultStudio
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static string NativeFolder;

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 1. Setup embedded assembly resolver & extract WebView2Loader.dll before touching WebView2 types
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatVaultStudio"
            );
            NativeFolder = Path.Combine(appData, "native_v1");
            Directory.CreateDirectory(NativeFolder);

            string loaderPath = Path.Combine(NativeFolder, "WebView2Loader.dll");
            ExtractEmbeddedResourceToDisk("WebView2Loader.dll", loaderPath);

            AppDomain.CurrentDomain.AssemblyResolve += OnResolveAssembly;

            RunApplication(args);
        }

        private static void RunApplication(string[] args)
        {
            CoreWebView2Environment.SetLoaderDllFolderPath(NativeFolder);
            Application.Run(new StudioForm(args));
        }

        private static Assembly OnResolveAssembly(object sender, ResolveEventArgs args)
        {
            if (args.Name != null && args.Name.StartsWith("Microsoft.Web.WebView2.Core", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = ReadEmbeddedResourceBytes("Microsoft.Web.WebView2.Core.dll");
                if (bytes != null)
                {
                    return Assembly.Load(bytes);
                }
            }
            return null;
        }

        public static byte[] ReadEmbeddedResourceBytes(string resourceName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            Stream stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                string[] names = asm.GetManifestResourceNames();
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i].EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        stream = asm.GetManifestResourceStream(names[i]);
                        break;
                    }
                }
            }
            if (stream == null) return null;
            using (stream)
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static void ExtractEmbeddedResourceToDisk(string resourceName, string targetPath)
        {
            try
            {
                byte[] bytes = ReadEmbeddedResourceBytes(resourceName);
                if (bytes == null) return;
                if (File.Exists(targetPath))
                {
                    FileInfo fi = new FileInfo(targetPath);
                    if (fi.Length == bytes.Length) return;
                }
                File.WriteAllBytes(targetPath, bytes);
            }
            catch { }
        }
    }

    internal sealed class MediaZipEntryInfo
    {
        public string FullName;
        public long Length;
    }

    internal sealed class ParticipantStat
    {
        public string Name;
        public int Count;
        public int Words;
        public int MediaCount;
    }

    internal sealed class ParsedMessage
    {
        public int Id;
        public int DateIndex;
        public string TimeStr;
        public int SenderIndex;
        public int Flags; // 1=System, 2=Edited, 4=Deleted, 8=JumboEmoji, 16=AttOmitted, 32=AttLinked
        public string Text;
        public string AttType;
        public string AttFile;
        public long AttSize;
    }

    public sealed class StudioForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private CoreWebView2Environment _env;
        private CoreWebView2Controller _controller;
        private TcpListener _server;
        private int _serverPort;
        private byte[] _appHtmlBytes;
        private byte[] _tailwindJsBytes;

        // High-Speed Multi-Tier Media Engine State
        private int _sessionId = 1;
        private string _activeZipPath = null;
        private ConcurrentBag<ZipArchive> _zipPool = new ConcurrentBag<ZipArchive>();
        private ConcurrentDictionary<string, MediaZipEntryInfo> _zipMediaIndex = new ConcurrentDictionary<string, MediaZipEntryInfo>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, string> _diskMediaIndex = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, byte[]> _ramMediaCache = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private long _ramCacheBytesTotal = 0;
        private const long MaxRamCacheBytes = 768L * 1024L * 1024L; // 768 MB Hot-RAM Media Cache

        private volatile byte[] _currentChatBundleBytes = Encoding.UTF8.GetBytes("{}");
        private readonly string[] _startupArgs;

        public StudioForm(string[] args)
        {
            _startupArgs = args;
            Text = "ChatVault Studio — Portable Chat Archive Viewer";
            Size = new Size(1340, 860);
            MinimumSize = new Size(860, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(11, 20, 26);
            Icon = CreateStudioAppIcon();

            _appHtmlBytes = Program.ReadEmbeddedResourceBytes("app.html") ?? Encoding.UTF8.GetBytes("<h1>Missing app.html</h1>");
            _tailwindJsBytes = Program.ReadEmbeddedResourceBytes("tailwindcss.min.js") ?? new byte[0];

            StartLocalMediaServer();

            Load += OnFormLoad;
            Resize += OnFormResize;
            FormClosing += OnFormClosing;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int useDark = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(Handle, 19, ref useDark, sizeof(int));
                }
            }
            catch { }
        }

        private static Icon CreateStudioAppIcon()
        {
            Bitmap bmp = new Bitmap(64, 64);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (LinearGradientBrush bgBrush = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(16, 185, 129), Color.FromArgb(5, 150, 105), 45f))
                {
                    g.FillEllipse(bgBrush, 4, 4, 56, 56);
                }
                using (Pen borderPen = new Pen(Color.FromArgb(110, 255, 255, 255), 2.5f))
                {
                    g.DrawEllipse(borderPen, 5, 5, 54, 54);
                }
                using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(whiteBrush, 16, 18, 32, 26);
                    Point[] tail = new Point[] { new Point(18, 38), new Point(13, 49), new Point(28, 42) };
                    g.FillPolygon(whiteBrush, tail);
                }
                using (Font f = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (SolidBrush greenText = new SolidBrush(Color.FromArgb(6, 95, 70)))
                {
                    g.DrawString("CV", f, greenText, 19f, 22f);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private async void OnFormLoad(object sender, EventArgs e)
        {
            try
            {
                string userDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChatVaultStudio",
                    "WebView2Data"
                );
                Directory.CreateDirectory(userDataDir);

                _env = await CoreWebView2Environment.CreateAsync(null, userDataDir, null);
                _controller = await _env.CreateCoreWebView2ControllerAsync(Handle);
                _controller.DefaultBackgroundColor = Color.FromArgb(255, 11, 20, 26);
                _controller.Bounds = ClientRectangle;
                _controller.AllowExternalDrop = true;
                _controller.IsVisible = true;

                CoreWebView2 wv = _controller.CoreWebView2;
                wv.Settings.IsStatusBarEnabled = false;
                wv.Settings.AreDefaultContextMenusEnabled = true;
                wv.WebMessageReceived += OnWebMessageReceived;

                wv.Navigate("http://127.0.0.1:" + _serverPort + "/");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to initialize WebView2 runtime:\n" + ex.Message,
                    "ChatVault Studio",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private void OnFormResize(object sender, EventArgs e)
        {
            if (_controller != null)
            {
                _controller.Bounds = ClientRectangle;
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try { if (_server != null) _server.Stop(); } catch { }
            ClearZipPool();
        }

        private void ClearZipPool()
        {
            ZipArchive za;
            while (_zipPool.TryTake(out za))
            {
                try { za.Dispose(); } catch { }
            }
        }

        /**********************************************************************
         * 1. HIGH-CONCURRENCY LOOPBACK MEDIA & BUNDLE SERVER
         **********************************************************************/
        private void StartLocalMediaServer()
        {
            _server = new TcpListener(IPAddress.Loopback, 0);
            _server.Start(128);
            _serverPort = ((IPEndPoint)_server.LocalEndpoint).Port;

            Task.Run(() =>
            {
                while (true)
                {
                    try
                    {
                        TcpClient client = _server.AcceptTcpClient();
                        client.NoDelay = true;
                        Task.Run(() => HandleHttpClient(client));
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }

        private void HandleHttpClient(TcpClient client)
        {
            using (client)
            using (NetworkStream net = client.GetStream())
            {
                try
                {
                    byte[] headerBuf = new byte[4096];
                    int read = net.Read(headerBuf, 0, headerBuf.Length);
                    if (read <= 0) return;

                    string reqText = Encoding.ASCII.GetString(headerBuf, 0, read);
                    string[] lines = reqText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    if (lines.Length == 0) return;

                    string[] firstLine = lines[0].Split(' ');
                    if (firstLine.Length < 2) return;

                    string rawPath = firstLine[1];
                    int qIdx = rawPath.IndexOf('?');
                    string path = qIdx >= 0 ? rawPath.Substring(0, qIdx) : rawPath;

                    // Parse optional Range header for video/audio streaming
                    string rangeHeader = null;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        {
                            rangeHeader = lines[i].Substring(6).Trim();
                            break;
                        }
                    }

                    if (path == "/" || path == "/index.html")
                    {
                        WriteHttpResponse(net, "200 OK", "text/html; charset=utf-8", _appHtmlBytes, false);
                        return;
                    }

                    if (path == "/tailwind.js")
                    {
                        WriteHttpResponse(net, "200 OK", "application/javascript; charset=utf-8", _tailwindJsBytes, true);
                        return;
                    }

                    if (path == "/api/chat-bundle")
                    {
                        byte[] bundle = _currentChatBundleBytes;
                        WriteHttpResponse(net, "200 OK", "application/json; charset=utf-8", bundle, false);
                        return;
                    }

                    if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
                    {
                        string encodedKey = path.Substring(7);
                        string mediaKey = Uri.UnescapeDataString(encodedKey).Trim().ToLowerInvariant();
                        ServeMediaRequest(net, mediaKey, rangeHeader);
                        return;
                    }

                    WriteHttpResponse(net, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("Not found"), false);
                }
                catch { }
            }
        }

        private void ServeMediaRequest(NetworkStream net, string key, string rangeHeader)
        {
            // Tier 1: Lock-free In-Memory RAM Cache (< 0.1ms)
            byte[] cachedBytes;
            if (_ramMediaCache.TryGetValue(key, out cachedBytes))
            {
                string mime = DetectMimeType(key, cachedBytes);
                WriteByteBufferWithRangeSupport(net, mime, cachedBytes, rangeHeader);
                return;
            }

            // Tier 2: Disk File
            string diskPath;
            if (_diskMediaIndex.TryGetValue(key, out diskPath) && File.Exists(diskPath))
            {
                FileInfo fi = new FileInfo(diskPath);
                if (fi.Length <= 10 * 1024 * 1024 && Interlocked.Read(ref _ramCacheBytesTotal) < MaxRamCacheBytes)
                {
                    byte[] fileBytes = File.ReadAllBytes(diskPath);
                    EnsureInfiniteLoopMedia(fileBytes);
                    if (_ramMediaCache.TryAdd(key, fileBytes))
                    {
                        Interlocked.Add(ref _ramCacheBytesTotal, fileBytes.Length);
                    }
                    string mime = DetectMimeType(key, fileBytes);
                    WriteByteBufferWithRangeSupport(net, mime, fileBytes, rangeHeader);
                    return;
                }

                // Stream large file directly from disk with HTTP Range support
                StreamDiskFileWithRangeSupport(net, DetectMimeType(key, null), diskPath, fi.Length, rangeHeader);
                return;
            }

            // Tier 3: Concurrent ZipArchive Reader Pool
            MediaZipEntryInfo entryInfo;
            if (_zipMediaIndex.TryGetValue(key, out entryInfo) && !string.IsNullOrEmpty(_activeZipPath))
            {
                ZipArchive za = BorrowZipArchive();
                if (za != null)
                {
                    try
                    {
                        ZipArchiveEntry entry = za.GetEntry(entryInfo.FullName);
                        if (entry != null)
                        {
                            byte[] entryBytes;
                            using (Stream es = entry.Open())
                            using (MemoryStream ms = new MemoryStream((int)Math.Min(entryInfo.Length > 0 ? entryInfo.Length : 65536, 64 * 1024 * 1024)))
                            {
                                es.CopyTo(ms);
                                entryBytes = ms.ToArray();
                            }

                            EnsureInfiniteLoopMedia(entryBytes);

                            if (entryBytes.Length <= 12 * 1024 * 1024 && Interlocked.Read(ref _ramCacheBytesTotal) < MaxRamCacheBytes)
                            {
                                if (_ramMediaCache.TryAdd(key, entryBytes))
                                {
                                    Interlocked.Add(ref _ramCacheBytesTotal, entryBytes.Length);
                                }
                            }

                            string mime = DetectMimeType(key, entryBytes);
                            WriteByteBufferWithRangeSupport(net, mime, entryBytes, rangeHeader);
                            return;
                        }
                    }
                    finally
                    {
                        _zipPool.Add(za);
                    }
                }
            }

            WriteHttpResponse(net, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("Media not found"), false);
        }

        private ZipArchive BorrowZipArchive()
        {
            ZipArchive za;
            if (_zipPool.TryTake(out za)) return za;
            try
            {
                FileStream fs = new FileStream(_activeZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                return new ZipArchive(fs, ZipArchiveMode.Read, false);
            }
            catch
            {
                return null;
            }
        }

        private static void WriteHttpResponse(NetworkStream net, string status, string contentType, byte[] body, bool immutableCache)
        {
            string cacheHeader = immutableCache
                ? "Cache-Control: public, max-age=31536000, immutable\r\n"
                : "Cache-Control: no-store\r\n";

            string header =
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                cacheHeader +
                "Connection: close\r\n\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            net.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
            {
                net.Write(body, 0, body.Length);
            }
        }

        private static void WriteByteBufferWithRangeSupport(NetworkStream net, string contentType, byte[] data, string rangeHeader)
        {
            long total = data.Length;
            long start = 0;
            long end = total - 1;
            bool isPartial = false;

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && total > 0)
            {
                string[] parts = rangeHeader.Substring(6).Split('-');
                long parsedStart;
                if (parts.Length >= 1 && long.TryParse(parts[0], out parsedStart) && parsedStart >= 0 && parsedStart < total)
                {
                    start = parsedStart;
                    long parsedEnd;
                    if (parts.Length >= 2 && long.TryParse(parts[1], out parsedEnd) && parsedEnd >= start && parsedEnd < total)
                    {
                        end = parsedEnd;
                    }
                    isPartial = true;
                }
            }

            long contentLen = (end - start) + 1;
            if (!isPartial)
            {
                string header =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: " + contentType + "\r\n" +
                    "Content-Length: " + total + "\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Cache-Control: public, max-age=31536000, immutable\r\n" +
                    "Access-Control-Allow-Origin: *\r\n" +
                    "Connection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(header);
                net.Write(hb, 0, hb.Length);
                if (total > 0) net.Write(data, 0, (int)total);
            }
            else
            {
                string header =
                    "HTTP/1.1 206 Partial Content\r\n" +
                    "Content-Type: " + contentType + "\r\n" +
                    "Content-Length: " + contentLen + "\r\n" +
                    "Content-Range: bytes " + start + "-" + end + "/" + total + "\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Cache-Control: public, max-age=31536000, immutable\r\n" +
                    "Access-Control-Allow-Origin: *\r\n" +
                    "Connection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(header);
                net.Write(hb, 0, hb.Length);
                net.Write(data, (int)start, (int)contentLen);
            }
        }

        private static void StreamDiskFileWithRangeSupport(NetworkStream net, string contentType, string filePath, long total, string rangeHeader)
        {
            long start = 0;
            long end = total - 1;
            bool isPartial = false;

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && total > 0)
            {
                string[] parts = rangeHeader.Substring(6).Split('-');
                long parsedStart;
                if (parts.Length >= 1 && long.TryParse(parts[0], out parsedStart) && parsedStart >= 0 && parsedStart < total)
                {
                    start = parsedStart;
                    long parsedEnd;
                    if (parts.Length >= 2 && long.TryParse(parts[1], out parsedEnd) && parsedEnd >= start && parsedEnd < total)
                    {
                        end = parsedEnd;
                    }
                    isPartial = true;
                }
            }

            long contentLen = (end - start) + 1;
            string statusLine = isPartial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n";
            string rangeLine = isPartial ? ("Content-Range: bytes " + start + "-" + end + "/" + total + "\r\n") : "";

            string header =
                statusLine +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + contentLen + "\r\n" +
                rangeLine +
                "Accept-Ranges: bytes\r\n" +
                "Cache-Control: public, max-age=31536000, immutable\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Connection: close\r\n\r\n";

            byte[] hb = Encoding.ASCII.GetBytes(header);
            net.Write(hb, 0, hb.Length);

            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536))
            {
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[65536];
                long remaining = contentLen;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int r = fs.Read(buf, 0, toRead);
                    if (r <= 0) break;
                    net.Write(buf, 0, r);
                    remaining -= r;
                }
            }
        }

        private static string DetectMimeType(string fileName, byte[] data)
        {
            if (data != null && data.Length >= 12)
            {
                // SVG (<svg)
                if (data[0] == 0x3C && (data[1] == 0x73 || data[1] == 0x3F || data[1] == 0x21))
                    return "image/svg+xml";
                // PNG
                if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                    return "image/png";
                // JPEG
                if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                    return "image/jpeg";
                // GIF
                if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
                    return "image/gif";
                // RIFF (WEBP or WAVE)
                if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46)
                {
                    if (data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
                        return "image/webp";
                    if (data[8] == 0x57 && data[9] == 0x41 && data[10] == 0x56 && data[11] == 0x45)
                        return "audio/wav";
                }
                // OggS
                if (data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
                    return "audio/ogg";
            }

            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            switch (ext)
            {
                case ".svg": return "image/svg+xml";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".mp4":
                case ".m4v": return "video/mp4";
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".opus":
                case ".ogg": return "audio/ogg";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".m4a": return "audio/mp4";
                case ".pdf": return "application/pdf";
                case ".vcf": return "text/vcard";
                default: return "application/octet-stream";
            }
        }

        /**********************************************************************
         * 2. WEBVIEW2 MESSAGE BRIDGE & NATIVE DIALOGS
         **********************************************************************/
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string msg = null;
                try { msg = e.TryGetWebMessageAsString(); } catch { }
                if (string.IsNullOrEmpty(msg)) msg = e.WebMessageAsJson;

                if (msg.Contains("\"app_ready\""))
                {
                    if (_startupArgs != null && _startupArgs.Length >= 2 && _startupArgs[0] == "--verify-giant")
                    {
                        Task.Run(() => BuildAndLoadRichDemoChat(true));
                    }
                    else if (_startupArgs != null && _startupArgs.Length > 0 && _startupArgs[0] != "--verify" && (File.Exists(_startupArgs[0]) || Directory.Exists(_startupArgs[0])))
                    {
                        ImportPathsAsync(_startupArgs);
                    }
                    else
                    {
                        Task.Run(() => BuildAndLoadRichDemoChat(false));
                    }
                    return;
                }

                if (msg.Contains("\"render_complete\""))
                {
                    if (_startupArgs != null && _startupArgs.Length >= 2 && (_startupArgs[0] == "--verify" || _startupArgs[0] == "--verify-giant" || _startupArgs[0] == "--verify-settings"))
                    {
                        string outShot = _startupArgs[1];
                        CaptureAndExitAsync(outShot);
                    }
                    return;
                }

                if (msg.Contains("\"load_demo\""))
                {
                    Task.Run(() => BuildAndLoadRichDemoChat(false));
                    return;
                }

                if (msg.Contains("\"load_giant_demo\""))
                {
                    Task.Run(() => BuildAndLoadRichDemoChat(true));
                    return;
                }

                if (msg.Contains("\"open_zip\""))
                {
                    using (OpenFileDialog ofd = new OpenFileDialog())
                    {
                        ofd.Title = "Select Exported Chat .ZIP Archive";
                        ofd.Filter = "Chat Export ZIP Archives (*.zip)|*.zip|All Files (*.*)|*.*";
                        if (ofd.ShowDialog(this) == DialogResult.OK)
                        {
                            ImportPathsAsync(new string[] { ofd.FileName });
                        }
                    }
                    return;
                }

                if (msg.Contains("\"open_folder\""))
                {
                    using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                    {
                        fbd.Description = "Select Unzipped Chat Export Folder (log file + media)";
                        fbd.ShowNewFolderButton = false;
                        if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(fbd.SelectedPath))
                        {
                            ImportPathsAsync(new string[] { fbd.SelectedPath });
                        }
                    }
                    return;
                }

                if (msg.Contains("\"open_files\""))
                {
                    using (OpenFileDialog ofd = new OpenFileDialog())
                    {
                        ofd.Title = "Select Chat Log (.txt, .json, .html, .csv) and/or Media Files";
                        ofd.Filter = "All Supported Chat Exports & Media (*.*)|*.*|Chat Logs (*.txt;*.json;*.html;*.csv)|*.txt;*.json;*.html;*.csv";
                        ofd.Multiselect = true;
                        if (ofd.ShowDialog(this) == DialogResult.OK && ofd.FileNames.Length > 0)
                        {
                            ImportPathsAsync(ofd.FileNames);
                        }
                    }
                    return;
                }

                if (msg.Contains("\"drop_files\""))
                {
                    List<string> droppedPaths = new List<string>();
                    if (e.AdditionalObjects != null)
                    {
                        for (int i = 0; i < e.AdditionalObjects.Count; i++)
                        {
                            CoreWebView2File wf = e.AdditionalObjects[i] as CoreWebView2File;
                            if (wf != null && !string.IsNullOrEmpty(wf.Path))
                            {
                                droppedPaths.Add(wf.Path);
                            }
                        }
                    }
                    if (droppedPaths.Count > 0)
                    {
                        ImportPathsAsync(droppedPaths.ToArray());
                    }
                    return;
                }

                if (msg.Contains("\"parse_text\""))
                {
                    int idx = msg.IndexOf("\"text\":", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        string rawJsonText = ExtractJsonStringField(msg, "text");
                        if (!string.IsNullOrEmpty(rawJsonText))
                        {
                            Task.Run(() => ImportRawText("Pasted Chat", rawJsonText, false));
                        }
                    }
                    return;
                }
            }
            catch { }
        }

        private async void CaptureAndExitAsync(string outShot)
        {
            try
            {
                if (_startupArgs != null && _startupArgs.Length > 0 && _startupArgs[0] == "--verify-settings")
                {
                    if (_controller != null && _controller.CoreWebView2 != null)
                    {
                        await _controller.CoreWebView2.ExecuteScriptAsync("var btn = document.getElementById('openSettingsBtn'); if (btn) btn.click();");
                        await Task.Delay(300);
                    }
                }
                await Task.Delay(350);
                if (_controller != null && _controller.CoreWebView2 != null)
                {
                    using (FileStream fs = new FileStream(outShot, FileMode.Create, FileAccess.Write))
                    {
                        await _controller.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                    }
                }
            }
            catch { }
            Close();
        }

        private void SendWebMessageJson(string json)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SendWebMessageJson(json)));
                return;
            }
            if (_controller != null && _controller.CoreWebView2 != null)
            {
                try { _controller.CoreWebView2.PostWebMessageAsJson(json); } catch { }
            }
        }

        private void SendProgress(string title, string phase, int percent, int step, string detail, string telemetry)
        {
            string json = "{" +
                "\"type\":\"progress\"," +
                "\"title\":\"" + EscapeJson(title) + "\"," +
                "\"phase\":\"" + EscapeJson(phase) + "\"," +
                "\"percent\":" + percent + "," +
                "\"step\":" + step + "," +
                "\"detail\":\"" + EscapeJson(detail) + "\"," +
                "\"telemetry\":\"" + EscapeJson(telemetry) + "\"" +
                "}";
            SendWebMessageJson(json);
        }

        /**********************************************************************
         * 3. MULTI-THREADED NATIVE IMPORT PIPEtab-log (ZIP / FOLDER / FILES)
         **********************************************************************/
        private void ResetMediaState()
        {
            Interlocked.Increment(ref _sessionId);
            _activeZipPath = null;
            ClearZipPool();
            _zipMediaIndex.Clear();
            _diskMediaIndex.Clear();
            _ramMediaCache.Clear();
            Interlocked.Exchange(ref _ramCacheBytesTotal, 0);
        }

        private void ImportPathsAsync(string[] paths)
        {
            Task.Run(() =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    // Case 1: Single .ZIP Archive
                    if (paths.Length == 1 && File.Exists(paths[0]) && paths[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        ImportZipArchive(paths[0], sw);
                        return;
                    }

                    // Case 2: Single Folder
                    if (paths.Length == 1 && Directory.Exists(paths[0]))
                    {
                        string[] allFiles = Directory.GetFiles(paths[0], "*.*", SearchOption.AllDirectories);
                        ImportFileList(Path.GetFileName(paths[0]), allFiles, true, sw);
                        return;
                    }

                    // Case 3: Multiple files or mix of folders/files
                    List<string> expanded = new List<string>();
                    for (int i = 0; i < paths.Length; i++)
                    {
                        string p = paths[i];
                        if (File.Exists(p))
                        {
                            if (p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && paths.Length == 1)
                            {
                                ImportZipArchive(p, sw);
                                return;
                            }
                            expanded.Add(p);
                        }
                        else if (Directory.Exists(p))
                        {
                            expanded.AddRange(Directory.GetFiles(p, "*.*", SearchOption.AllDirectories));
                        }
                    }

                    if (expanded.Count > 0)
                    {
                        ImportFileList("Imported Chat", expanded.ToArray(), false, sw);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Import error: " + ex.Message, "ChatVault Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        private void ImportZipArchive(string zipPath, Stopwatch sw)
        {
            ResetMediaState();
            _activeZipPath = zipPath;
            string rawChatName = Path.GetFileNameWithoutExtension(zipPath);
            string chatTitle = CleanChatTitle(rawChatName);

            FileInfo zipFi = new FileInfo(zipPath);
            double zipMB = zipFi.Length / (1024.0 * 1024.0);

            SendProgress(
                chatTitle,
                "Stage 1/4: Scanning ZIP Central Directory...",
                8,
                1,
                Path.GetFileName(zipPath),
                zipMB.ToString("0.0") + " MB archive"
            );

            string chatTxtContent = null;
            List<MediaZipEntryInfo> preloadCandidates = new List<MediaZipEntryInfo>();

            using (FileStream fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Read, false))
            {
                int totalEntries = za.Entries.Count;
                string bestTxtEntryName = null;
                long bestTxtLen = -1;

                for (int i = 0; i < totalEntries; i++)
                {
                    ZipArchiveEntry entry = za.Entries[i];
                    string fullName = entry.FullName;
                    if (fullName.EndsWith("/") || fullName.Contains("__MACOSX/") || Path.GetFileName(fullName).StartsWith("._"))
                        continue;

                    string fileName = Path.GetFileName(fullName);
                    if (string.IsNullOrEmpty(fileName)) continue;

                    if (IsChatLogFile(fileName))
                    {
                        long score = ScoreChatLogCandidate(fileName, entry.Length);
                        if (score > bestTxtLen)
                        {
                            bestTxtLen = score;
                            bestTxtEntryName = fullName;
                        }
                    }
                    else
                    {
                        string key = NormalizeKey(fileName);
                        MediaZipEntryInfo info = new MediaZipEntryInfo { FullName = fullName, Length = entry.Length };
                        _zipMediaIndex[key] = info;

                        // Candidate for instant RAM hot-cache (stickers, GIFs, photos, short videos, voice notes <= 8MB)
                        string ext = Path.GetExtension(key);
                        if (entry.Length > 0 && entry.Length <= 8 * 1024 * 1024 &&
                            (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".gif" || ext == ".mp4" || ext == ".webm" || ext == ".svg" || ext == ".opus" || ext == ".ogg" || ext == ".mp3" || ext == ".wav"))
                        {
                            preloadCandidates.Add(info);
                        }
                    }
                }

                if (bestTxtEntryName != null)
                {
                    ZipArchiveEntry txtEntry = za.GetEntry(bestTxtEntryName);
                    if (txtEntry != null)
                    {
                        using (Stream ts = txtEntry.Open())
                        using (StreamReader sr = new StreamReader(ts, Encoding.UTF8, true))
                        {
                            chatTxtContent = sr.ReadToEnd();
                        }
                    }
                }
            }

            // Pre-populate ZipArchive Reader Pool for concurrent threads
            int poolCount = Math.Min(12, Math.Max(4, Environment.ProcessorCount));
            for (int i = 0; i < poolCount; i++)
            {
                try
                {
                    FileStream pfs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    _zipPool.Add(new ZipArchive(pfs, ZipArchiveMode.Read, false));
                }
                catch { }
            }

            // Stage 2: Parallel Multi-Core RAM Pre-Warming (reverse order so newest photos/stickers are cached first!)
            preloadCandidates.Reverse();
            int totalCandidates = preloadCandidates.Count;
            if (totalCandidates > 0)
            {
                int cachedCount = 0;
                Parallel.ForEach(
                    preloadCandidates,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    (candidate, loopState) =>
                    {
                        if (Interlocked.Read(ref _ramCacheBytesTotal) >= MaxRamCacheBytes)
                        {
                            loopState.Stop();
                            return;
                        }

                        ZipArchive reader = BorrowZipArchive();
                        if (reader == null) return;
                        try
                        {
                            ZipArchiveEntry e = reader.GetEntry(candidate.FullName);
                            if (e != null)
                            {
                                byte[] bytes = new byte[e.Length];
                                using (Stream s = e.Open())
                                {
                                    int offset = 0;
                                    while (offset < bytes.Length)
                                    {
                                        int r = s.Read(bytes, offset, bytes.Length - offset);
                                        if (r <= 0) break;
                                        offset += r;
                                    }
                                }
                                string key = NormalizeKey(Path.GetFileName(candidate.FullName));
                                EnsureInfiniteLoopMedia(bytes);
                                if (_ramMediaCache.TryAdd(key, bytes))
                                {
                                    Interlocked.Add(ref _ramCacheBytesTotal, bytes.Length);
                                }
                            }
                        }
                        catch { }
                        finally
                        {
                            _zipPool.Add(reader);
                        }

                        int done = Interlocked.Increment(ref cachedCount);
                        if (done % 15 == 0 || done == totalCandidates)
                        {
                            int pct = 20 + (int)Math.Min(45, (done * 45L) / Math.Max(1, totalCandidates));
                            double mb = Interlocked.Read(ref _ramCacheBytesTotal) / (1024.0 * 1024.0);
                            SendProgress(
                                chatTitle,
                                "Stage 2/4: Pre-caching photos & stickers into RAM...",
                                pct,
                                2,
                                "Loaded " + done.ToString("N0") + " / " + totalCandidates.ToString("N0") + " media files into instant memory",
                                mb.ToString("0.0") + " MB cached"
                            );
                        }
                    }
                );
            }

            if (chatTxtContent != null)
            {
                ParseAndFinalizeChat(chatTitle, chatTxtContent, sw);
            }
        }

        private void ImportFileList(string folderOrDefaultTitle, string[] filePaths, bool forceReset, Stopwatch sw)
        {
            string txtPath = null;
            long bestTxtSize = -1;
            List<string> mediaPaths = new List<string>();

            for (int i = 0; i < filePaths.Length; i++)
            {
                string p = filePaths[i];
                string fn = Path.GetFileName(p);
                if (IsChatLogFile(fn))
                {
                    FileInfo fi = new FileInfo(p);
                    long score = ScoreChatLogCandidate(fn, fi.Length);
                    if (score > bestTxtSize)
                    {
                        bestTxtSize = score;
                        txtPath = p;
                    }
                }
                else
                {
                    mediaPaths.Add(p);
                }
            }

            bool hasNewChatTxt = (txtPath != null);
            if (hasNewChatTxt || forceReset)
            {
                ResetMediaState();
            }

            string chatTitle = hasNewChatTxt
                ? CleanChatTitle(Path.GetFileNameWithoutExtension(txtPath))
                : folderOrDefaultTitle;

            SendProgress(
                chatTitle,
                "Stage 1/4: Indexing media files...",
                12,
                1,
                "Found " + mediaPaths.Count.ToString("N0") + " media files",
                sw.ElapsedMilliseconds + " ms"
            );

            for (int i = 0; i < mediaPaths.Count; i++)
            {
                string mp = mediaPaths[i];
                string key = NormalizeKey(Path.GetFileName(mp));
                _diskMediaIndex[key] = mp;
            }

            // Pre-warm photos & stickers into RAM in parallel (newest first)
            mediaPaths.Reverse();
            int totalMedia = mediaPaths.Count;
            int doneMedia = 0;

            if (totalMedia > 0)
            {
                Parallel.ForEach(
                    mediaPaths,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    (mp, loopState) =>
                    {
                        if (Interlocked.Read(ref _ramCacheBytesTotal) >= MaxRamCacheBytes)
                        {
                            loopState.Stop();
                            return;
                        }
                        try
                        {
                            string key = NormalizeKey(Path.GetFileName(mp));
                            string ext = Path.GetExtension(key);
                            if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".gif" || ext == ".mp4" || ext == ".webm" || ext == ".svg" || ext == ".opus" || ext == ".ogg" || ext == ".mp3" || ext == ".wav")
                            {
                                FileInfo fi = new FileInfo(mp);
                                if (fi.Length > 0 && fi.Length <= 8 * 1024 * 1024)
                                {
                                    byte[] bytes = File.ReadAllBytes(mp);
                                    EnsureInfiniteLoopMedia(bytes);
                                    if (_ramMediaCache.TryAdd(key, bytes))
                                    {
                                        Interlocked.Add(ref _ramCacheBytesTotal, bytes.Length);
                                    }
                                }
                            }
                        }
                        catch { }

                        int d = Interlocked.Increment(ref doneMedia);
                        if (d % 20 == 0 || d == totalMedia)
                        {
                            int pct = 20 + (int)Math.Min(45, (d * 45L) / Math.Max(1, totalMedia));
                            double mb = Interlocked.Read(ref _ramCacheBytesTotal) / (1024.0 * 1024.0);
                            SendProgress(
                                chatTitle,
                                "Stage 2/4: Pre-caching photos & stickers into RAM...",
                                pct,
                                2,
                                "Cached " + d.ToString("N0") + " / " + totalMedia.ToString("N0") + " files",
                                mb.ToString("0.0") + " MB in RAM"
                            );
                        }
                    }
                );
            }

            if (hasNewChatTxt)
            {
                string txtContent = File.ReadAllText(txtPath, Encoding.UTF8);
                ParseAndFinalizeChat(chatTitle, txtContent, sw);
            }
            else
            {
                // Only additional media files were dropped onto an existing chat!
                // Re-serialize or notify frontend that media links are updated
                double mb = Interlocked.Read(ref _ramCacheBytesTotal) / (1024.0 * 1024.0);
                int totalIndexed = _ramMediaCache.Count + _diskMediaIndex.Count + _zipMediaIndex.Count;
                SendWebMessageJson("{\"type\":\"media_updated\",\"mediaFilesCount\":" + totalIndexed + ",\"ramCacheMB\":" + mb.ToString("0.0", CultureInfo.InvariantCulture) + "}");
            }
        }

        private void ImportRawText(string chatTitle, string rawText, bool clearMedia)
        {
            Stopwatch sw = Stopwatch.StartNew();
            if (clearMedia) ResetMediaState();
            ParseAndFinalizeChat(chatTitle, rawText, sw);
        }

        /**********************************************************************
         * 4. UNIVERSAL MULTI-PLATFORM NATIVE C# PARSER
         * Supports phone-messenger, paper-plane (JSON/HTML), guild-chat (JSON/TXT/CSV),
         * social DM (social JSON/HTML), tab-log, bracket-log, secure-messenger & CSV
         **********************************************************************/
        private static bool IsChatLogFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string lower = fileName.ToLowerInvariant();
            return lower.EndsWith(".txt") || lower.EndsWith(".json") || lower.EndsWith(".html") || lower.EndsWith(".htm") || lower.EndsWith(".csv");
        }

        private static long ScoreChatLogCandidate(string fileName, long byteLength)
        {
            if (byteLength <= 0) return -1;
            string lower = fileName.ToLowerInvariant();
            // High priority for canonical export filenames across platforms
            if (lower == "result.json" || lower == "_chat.txt" || lower == "message_1.json" || lower == "messages.html" || lower == "messages.json")
                return byteLength + 1000000000L;
            if (lower.StartsWith("whatsapp chat") || lower.StartsWith("message_") || lower.StartsWith("messages"))
                return byteLength + 500000000L;
            return byteLength;
        }

        private static readonly Regex HeaderRegex = new Regex(
            @"^[\u200e\u200f\ufeff]*\[?(\d{1,4}[\/.\-]\d{1,2}[\/.\-]\d{1,4})[,\s]+(\d{1,2}:\d{2}(?::\d{2})?(?:[\s\u202f\u00a0]*[AaPp]\.?[Mm]\.?)?)\]?(?:\s*[-–—]\s*|\s+)(.*)$",
            RegexOptions.Compiled
        );

        // guild-chat TXT export: [24-Oct-25 02:32 PM] SenderName
        private static readonly Regex GuildTxtHeaderRegex = new Regex(
            @"^\[(\d{1,4}[\/.\-][A-Za-z0-9]{1,4}[\/.\-]\d{1,4})\s+(\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AaPp][Mm])?)\]\s+([^\r\n]+)$",
            RegexOptions.Compiled
        );

        // tab-log date header: 2025/10/24(Fri) or 2025.10.24 Friday
        private static readonly Regex StickyDateRegex = new Regex(
            @"^(\d{4}[\/.\-]\d{1,2}[\/.\-]\d{1,2})(?:\s*\([^)]+\)|\s+[A-Za-z]+)?$",
            RegexOptions.Compiled
        );

        // tab-log message row: 14:32\tSender\tMessage
        private static readonly Regex TabMsgRegex = new Regex(
            @"^(\d{1,2}:\d{2}(?:\s*[AaPp][Mm])?)\t([^\t]+)\t(.*)$",
            RegexOptions.Compiled
        );

        // bracket-log message row: [Sender] [2:32 PM] Message
        private static readonly Regex BracketMsgRegex = new Regex(
            @"^\[([^\]]+)\]\s*\[([^\]]*\d{1,2}:\d{2}[^\]]*)\]\s*(.*)$",
            RegexOptions.Compiled
        );

        private static readonly Regex IosAttachRegex = new Regex(
            @"<attached:\s*([^>]+)>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex AndroidAttachRegex = new Regex(
            @"^([^\s/\\]+?\.(?:jpg|jpeg|png|webp|gif|svg|mp4|mov|3gp|webm|opus|ogg|mp3|m4a|wav|aac|pdf|doc|docx|xls|xlsx|ppt|pptx|vcf|zip|txt|apk|heic))\s*\([^)]+\)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static int GetOrAddIdx(string val, List<string> list, Dictionary<string, int> map)
        {
            int idx;
            if (!map.TryGetValue(val, out idx))
            {
                idx = list.Count;
                list.Add(val);
                map[val] = idx;
            }
            return idx;
        }

        private static int GetOrAddSender(string sender, List<string> list, Dictionary<string, int> map, List<ParticipantStat> stats)
        {
            if (string.IsNullOrEmpty(sender)) return -1;
            int idx;
            if (!map.TryGetValue(sender, out idx))
            {
                idx = list.Count;
                list.Add(sender);
                map[sender] = idx;
                stats.Add(new ParticipantStat { Name = sender });
            }
            return idx;
        }

        private static string FixMetaMojibake(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            bool hasHighLatin = false;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c >= '\u0080' && c <= '\u00ff') { hasHighLatin = true; break; }
                if (c > '\u00ff') return input; // Already wide Unicode
            }
            if (!hasHighLatin) return input;
            try
            {
                byte[] raw = Encoding.GetEncoding("ISO-8859-1").GetBytes(input);
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                return input;
            }
        }

        private static string FlattenRichText(object textObj)
        {
            if (textObj == null) return "";
            string s = textObj as string;
            if (s != null) return s;
            System.Collections.IEnumerable arr = textObj as System.Collections.IEnumerable;
            if (arr != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (object item in arr)
                {
                    if (item is string)
                    {
                        sb.Append((string)item);
                    }
                    else
                    {
                        IDictionary<string, object> dict = item as IDictionary<string, object>;
                        if (dict != null && dict.ContainsKey("text") && dict["text"] != null)
                        {
                            sb.Append(dict["text"].ToString());
                        }
                    }
                }
                return sb.ToString();
            }
            return textObj.ToString();
        }

        private static void SplitIsoDateTime(string rawIso, out string dateStr, out string timeStr)
        {
            dateStr = "Unknown Date";
            timeStr = "12:00 PM";
            if (string.IsNullOrEmpty(rawIso)) return;
            DateTime dt;
            if (DateTime.TryParse(rawIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            {
                dateStr = dt.ToString("MM/dd/yy", CultureInfo.InvariantCulture);
                timeStr = dt.ToString("h:mm tt", CultureInfo.InvariantCulture);
                return;
            }
            int tIdx = rawIso.IndexOf('T');
            if (tIdx < 0) tIdx = rawIso.IndexOf(' ');
            if (tIdx > 0)
            {
                dateStr = rawIso.Substring(0, tIdx).Trim();
                timeStr = rawIso.Substring(tIdx + 1).Trim();
            }
            else
            {
                dateStr = rawIso.Trim();
            }
        }

        private void ParseAndFinalizeChat(string chatTitle, string rawContent, Stopwatch sw)
        {
            SendProgress(
                chatTitle,
                "Stage 3/4: Parsing multi-platform chat export...",
                68,
                3,
                "Running native multi-format parser...",
                sw.ElapsedMilliseconds + " ms"
            );

            List<string> uniqueDates = new List<string>();
            Dictionary<string, int> dateToIdx = new Dictionary<string, int>(StringComparer.Ordinal);

            List<string> uniqueSenders = new List<string>();
            Dictionary<string, int> senderToIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            List<ParticipantStat> participantStats = new List<ParticipantStat>();

            int[] hourHist = new int[24];
            List<ParsedMessage> messages = new List<ParsedMessage>(16384);
            string detectedPlatform = "emerald";

            string trimmed = (rawContent ?? "").TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

            // 1. JSON EXPORTS (paper-plane desktop result.json, social DM message_1.json, guild-chat JSON, secure-messenger/Generic JSON)
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                try
                {
                    JavaScriptSerializer jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    object root = jss.DeserializeObject(trimmed);
                    IDictionary<string, object> rootDict = root as IDictionary<string, object>;
                    System.Collections.IEnumerable msgEnumerable = null;

                    if (rootDict != null)
                    {
                        if (rootDict.ContainsKey("name") && rootDict["name"] != null)
                            chatTitle = rootDict["name"].ToString();
                        else if (rootDict.ContainsKey("title") && rootDict["title"] != null)
                            chatTitle = FixMetaMojibake(rootDict["title"].ToString());
                        else if (rootDict.ContainsKey("channel") && rootDict["channel"] is IDictionary<string, object>)
                        {
                            IDictionary<string, object> ch = (IDictionary<string, object>)rootDict["channel"];
                            if (ch.ContainsKey("name") && ch["name"] != null) chatTitle = "#" + ch["name"].ToString();
                        }

                        if (rootDict.ContainsKey("messages"))
                            msgEnumerable = rootDict["messages"] as System.Collections.IEnumerable;
                    }
                    else
                    {
                        msgEnumerable = root as System.Collections.IEnumerable;
                    }

                    if (msgEnumerable != null)
                    {
                        List<IDictionary<string, object>> msgDicts = new List<IDictionary<string, object>>();
                        foreach (object item in msgEnumerable)
                        {
                            IDictionary<string, object> d = item as IDictionary<string, object>;
                            if (d != null) msgDicts.Add(d);
                        }

                        // Detect social (social DM) export and reverse to chronological order
                        if (msgDicts.Count > 0 && msgDicts[0].ContainsKey("sender_name") && msgDicts[0].ContainsKey("timestamp_ms"))
                        {
                            detectedPlatform = "gradient";
                            msgDicts.Reverse();
                        }
                        else if (rootDict != null && (rootDict.ContainsKey("guild") || rootDict.ContainsKey("channel")))
                        {
                            detectedPlatform = "guild";
                        }
                        else if (msgDicts.Count > 0 && (msgDicts[0].ContainsKey("Timestamp") && msgDicts[0].ContainsKey("Contents")))
                        {
                            detectedPlatform = "guild";
                            msgDicts.Reverse();
                        }
                        else
                        {
                            detectedPlatform = "sky";
                        }

                        for (int i = 0; i < msgDicts.Count; i++)
                        {
                            IDictionary<string, object> m = msgDicts[i];
                            string dateStr = "Unknown Date";
                            string timeStr = "12:00 PM";
                            string sender = "";
                            string bodyText = "";
                            string expAttFile = null;
                            string expAttType = null;
                            bool isSys = false;

                            // Case A: social (social DM / social DM)
                            if (m.ContainsKey("sender_name") && m.ContainsKey("timestamp_ms"))
                            {
                                sender = FixMetaMojibake(m["sender_name"] != null ? m["sender_name"].ToString() : "User");
                                long tsMs = Convert.ToInt64(m["timestamp_ms"]);
                                DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(tsMs).ToLocalTime();
                                dateStr = dt.ToString("MM/dd/yy", CultureInfo.InvariantCulture);
                                timeStr = dt.ToString("h:mm tt", CultureInfo.InvariantCulture);
                                if (m.ContainsKey("content") && m["content"] != null)
                                    bodyText = FixMetaMojibake(m["content"].ToString());

                                string[] mediaKeys = new string[] { "photos", "videos", "gifs", "audio_files", "files" };
                                for (int mk = 0; mk < mediaKeys.Length; mk++)
                                {
                                    if (m.ContainsKey(mediaKeys[mk]) && m[mediaKeys[mk]] is System.Collections.IEnumerable)
                                    {
                                        foreach (object mo in (System.Collections.IEnumerable)m[mediaKeys[mk]])
                                        {
                                            IDictionary<string, object> md = mo as IDictionary<string, object>;
                                            if (md != null && md.ContainsKey("uri") && md["uri"] != null)
                                            {
                                                expAttFile = Path.GetFileName(md["uri"].ToString());
                                                break;
                                            }
                                        }
                                    }
                                }
                                if (expAttFile == null && m.ContainsKey("sticker") && m["sticker"] is IDictionary<string, object>)
                                {
                                    IDictionary<string, object> sd = (IDictionary<string, object>)m["sticker"];
                                    if (sd.ContainsKey("uri") && sd["uri"] != null)
                                    {
                                        expAttFile = Path.GetFileName(sd["uri"].ToString());
                                        expAttType = "sticker";
                                    }
                                }
                            }
                            // Case B: guild-chat (guild exporter or Data Package)
                            else if (m.ContainsKey("author") || (m.ContainsKey("Timestamp") && m.ContainsKey("Contents")))
                            {
                                if (m.ContainsKey("timestamp") && m["timestamp"] != null)
                                    SplitIsoDateTime(m["timestamp"].ToString(), out dateStr, out timeStr);
                                else if (m.ContainsKey("Timestamp") && m["Timestamp"] != null)
                                    SplitIsoDateTime(m["Timestamp"].ToString(), out dateStr, out timeStr);

                                if (m.ContainsKey("author") && m["author"] is IDictionary<string, object>)
                                {
                                    IDictionary<string, object> au = (IDictionary<string, object>)m["author"];
                                    if (au.ContainsKey("nickname") && au["nickname"] != null && au["nickname"].ToString().Length > 0)
                                        sender = au["nickname"].ToString();
                                    else if (au.ContainsKey("name") && au["name"] != null)
                                        sender = au["name"].ToString();
                                }
                                else
                                {
                                    sender = "You";
                                }

                                if (m.ContainsKey("content") && m["content"] != null)
                                    bodyText = m["content"].ToString();
                                else if (m.ContainsKey("Contents") && m["Contents"] != null)
                                    bodyText = m["Contents"].ToString();

                                if (m.ContainsKey("attachments") && m["attachments"] is System.Collections.IEnumerable)
                                {
                                    foreach (object ao in (System.Collections.IEnumerable)m["attachments"])
                                    {
                                        IDictionary<string, object> ad = ao as IDictionary<string, object>;
                                        if (ad != null)
                                        {
                                            if (ad.ContainsKey("fileName") && ad["fileName"] != null)
                                                expAttFile = Path.GetFileName(ad["fileName"].ToString());
                                            else if (ad.ContainsKey("url") && ad["url"] != null)
                                                expAttFile = Path.GetFileName(ad["url"].ToString().Split('?')[0]);
                                            break;
                                        }
                                    }
                                }
                                else if (m.ContainsKey("Attachments") && m["Attachments"] != null && m["Attachments"].ToString().Length > 0)
                                {
                                    expAttFile = Path.GetFileName(m["Attachments"].ToString().Split('?')[0]);
                                }
                            }
                            // Case C: paper-plane desktop result.json & Generic JSON
                            else
                            {
                                if (m.ContainsKey("date") && m["date"] != null)
                                    SplitIsoDateTime(m["date"].ToString(), out dateStr, out timeStr);
                                else if (m.ContainsKey("timestamp") && m["timestamp"] != null)
                                    SplitIsoDateTime(m["timestamp"].ToString(), out dateStr, out timeStr);

                                string mType = m.ContainsKey("type") && m["type"] != null ? m["type"].ToString() : "message";
                                if (mType == "service") isSys = true;

                                if (m.ContainsKey("from") && m["from"] != null)
                                    sender = m["from"].ToString();
                                else if (m.ContainsKey("actor") && m["actor"] != null)
                                    sender = m["actor"].ToString();
                                else if (m.ContainsKey("sender") && m["sender"] != null)
                                    sender = m["sender"].ToString();

                                if (m.ContainsKey("text"))
                                    bodyText = FlattenRichText(m["text"]);
                                else if (m.ContainsKey("body") && m["body"] != null)
                                    bodyText = m["body"].ToString();

                                if (m.ContainsKey("photo") && m["photo"] != null)
                                {
                                    expAttFile = Path.GetFileName(m["photo"].ToString());
                                    expAttType = "photo";
                                }
                                else if (m.ContainsKey("file") && m["file"] != null)
                                {
                                    expAttFile = Path.GetFileName(m["file"].ToString());
                                    string mt = m.ContainsKey("media_type") && m["media_type"] != null ? m["media_type"].ToString() : "";
                                    if (mt == "sticker") expAttType = "sticker";
                                    else if (mt == "animation" || mt == "video_file") expAttType = "video";
                                    else if (mt == "voice_message" || mt == "audio_file") expAttType = "audio";
                                }

                                if (isSys && string.IsNullOrEmpty(bodyText) && m.ContainsKey("action") && m["action"] != null)
                                {
                                    bodyText = (sender + " " + m["action"].ToString().Replace('_', ' ')).Trim();
                                }
                            }

                            int dIdx = GetOrAddIdx(dateStr, uniqueDates, dateToIdx);
                            int hr = ExtractHourFromTime(timeStr);
                            if (hr >= 0 && hr < 24) hourHist[hr]++;
                            int sIdx = isSys ? -1 : GetOrAddSender(sender, uniqueSenders, senderToIdx, participantStats);
                            FinalizeParsedMessage(messages, dIdx, timeStr, sIdx, isSys, bodyText, participantStats, expAttFile, expAttType);
                        }
                    }
                }
                catch { }
            }
            // 2. HTML EXPORTS (paper-plane desktop messages.html, guild-chat HTML, social HTML)
            else if (trimmed.StartsWith("<") && (trimmed.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                detectedPlatform = trimmed.IndexOf("tgme_", StringComparison.OrdinalIgnoreCase) >= 0 || trimmed.IndexOf("pull_right date", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "sky"
                    : "gradient";

                Regex htmlMsgRegex = new Regex(
                    @"<div\s+class=""message\s+default[^""]*""[^>]*>([\s\S]*?)(?=<div\s+class=""message\s+|<div\s+class=""page_body|$)",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase
                );
                Regex fromRegex = new Regex(@"<div\s+class=""from_name""[^>]*>([\s\S]*?)</div>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                Regex dateTitleRegex = new Regex(@"title=""(\d{2}\.\d{2}\.\d{4}\s+\d{2}:\d{2}:\d{2})""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                Regex textDivRegex = new Regex(@"<div\s+class=""text""[^>]*>([\s\S]*?)</div>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                Regex hrefMediaRegex = new Regex(@"href=""([^""]+\.(?:jpg|jpeg|png|webp|gif|mp4|mov|webm|ogg|opus|mp3|wav|pdf))""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

                MatchCollection matches = htmlMsgRegex.Matches(trimmed);
                string lastSenderName = "User";
                for (int i = 0; i < matches.Count; i++)
                {
                    string block = matches[i].Groups[1].Value;
                    Match fm = fromRegex.Match(block);
                    if (fm.Success)
                    {
                        lastSenderName = Regex.Replace(fm.Groups[1].Value, "<[^>]+>", "").Trim();
                    }

                    string dateStr = "10/24/26";
                    string timeStr = "12:00 PM";
                    Match dm = dateTitleRegex.Match(block);
                    if (dm.Success)
                    {
                        SplitIsoDateTime(dm.Groups[1].Value, out dateStr, out timeStr);
                    }

                    string bodyText = "";
                    Match tm = textDivRegex.Match(block);
                    if (tm.Success)
                    {
                        bodyText = Regex.Replace(tm.Groups[1].Value.Replace("<br>", "\n").Replace("<br/>", "\n"), "<[^>]+>", "").Trim();
                        bodyText = WebUtility.HtmlDecode(bodyText);
                    }

                    string expAttFile = null;
                    Match mm = hrefMediaRegex.Match(block);
                    if (mm.Success)
                    {
                        expAttFile = Path.GetFileName(mm.Groups[1].Value);
                    }

                    int dIdx = GetOrAddIdx(dateStr, uniqueDates, dateToIdx);
                    int hr = ExtractHourFromTime(timeStr);
                    if (hr >= 0 && hr < 24) hourHist[hr]++;
                    int sIdx = GetOrAddSender(lastSenderName, uniqueSenders, senderToIdx, participantStats);
                    FinalizeParsedMessage(messages, dIdx, timeStr, sIdx, false, bodyText, participantStats, expAttFile, null);
                }
            }

            // 3. TEXT & CSV EXPORTS (phone-messenger, guild-chat TXT, tab-log, bracket-log, secure-messenger, CSV)
            if (messages.Count == 0)
            {
                string[] lines = rawContent.Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                int totalLines = lines.Length;

                int curDateIdx = 0;
                string curTimeStr = "";
                int curSenderIdx = -1;
                bool curIsSystem = false;
                StringBuilder curBody = null;
                bool hasCurrent = false;
                string activeStickyDate = "10/24/26";

                for (int i = 0; i < totalLines; i++)
                {
                    string line = lines[i];

                    // Check tab-log sticky date header: 2025/10/24(Fri)
                    Match lineDateM = StickyDateRegex.Match(line.Trim());
                    if (lineDateM.Success)
                    {
                        activeStickyDate = lineDateM.Groups[1].Value;
                        continue;
                    }

                    // A. phone-messenger iOS & Android Header
                    Match m = HeaderRegex.Match(line);
                    if (m.Success)
                    {
                        if (hasCurrent)
                        {
                            FinalizeParsedMessage(messages, curDateIdx, curTimeStr, curSenderIdx, curIsSystem, curBody.ToString(), participantStats, null, null);
                        }

                        string dateStr = CleanDirectional(m.Groups[1].Value).Trim();
                        string timeStr = CleanDirectional(m.Groups[2].Value).Replace('\u202f', ' ').Replace('\u00a0', ' ').Trim();
                        string remainder = m.Groups[3].Value;

                        int dIdx = GetOrAddIdx(dateStr, uniqueDates, dateToIdx);
                        int hour = ExtractHourFromTime(timeStr);
                        if (hour >= 0 && hour < 24) hourHist[hour]++;

                        int colonIdx = remainder.IndexOf(": ", StringComparison.Ordinal);
                        int sIdx = -1;
                        string bodyText = remainder;
                        bool isSys = false;

                        if (colonIdx > 0 && colonIdx < 60)
                        {
                            string candidateSender = CleanDirectional(remainder.Substring(0, colonIdx)).Trim();
                            if (candidateSender.Length > 0 && !candidateSender.Contains("//"))
                            {
                                sIdx = GetOrAddSender(candidateSender, uniqueSenders, senderToIdx, participantStats);
                                bodyText = remainder.Substring(colonIdx + 2);
                            }
                            else
                            {
                                isSys = true;
                            }
                        }
                        else
                        {
                            isSys = true;
                        }

                        curDateIdx = dIdx;
                        curTimeStr = timeStr;
                        curSenderIdx = sIdx;
                        curIsSystem = isSys;
                        curBody = new StringBuilder(bodyText);
                        hasCurrent = true;
                        continue;
                    }

                    // B. guild-chat TXT Header: [24-Oct-25 02:32 PM] SenderName
                    Match dm = GuildTxtHeaderRegex.Match(line);
                    if (dm.Success)
                    {
                        detectedPlatform = "guild";
                        if (hasCurrent)
                        {
                            FinalizeParsedMessage(messages, curDateIdx, curTimeStr, curSenderIdx, curIsSystem, curBody.ToString(), participantStats, null, null);
                        }
                        string dateStr = dm.Groups[1].Value.Trim();
                        string timeStr = dm.Groups[2].Value.Trim();
                        string senderName = dm.Groups[3].Value.Trim();
                        curDateIdx = GetOrAddIdx(dateStr, uniqueDates, dateToIdx);
                        int hour = ExtractHourFromTime(timeStr);
                        if (hour >= 0 && hour < 24) hourHist[hour]++;
                        curTimeStr = timeStr;
                        curSenderIdx = GetOrAddSender(senderName, uniqueSenders, senderToIdx, participantStats);
                        curIsSystem = false;
                        curBody = new StringBuilder();
                        hasCurrent = true;
                        continue;
                    }

                    // C. tab-log Tab-Separated Message: 14:32\tSender\tMessage
                    Match lm = TabMsgRegex.Match(line);
                    if (lm.Success)
                    {
                        detectedPlatform = "sky";
                        if (hasCurrent)
                        {
                            FinalizeParsedMessage(messages, curDateIdx, curTimeStr, curSenderIdx, curIsSystem, curBody.ToString(), participantStats, null, null);
                        }
                        curDateIdx = GetOrAddIdx(activeStickyDate, uniqueDates, dateToIdx);
                        curTimeStr = lm.Groups[1].Value.Trim();
                        int hour = ExtractHourFromTime(curTimeStr);
                        if (hour >= 0 && hour < 24) hourHist[hour]++;
                        curSenderIdx = GetOrAddSender(lm.Groups[2].Value.Trim(), uniqueSenders, senderToIdx, participantStats);
                        curIsSystem = false;
                        curBody = new StringBuilder(lm.Groups[3].Value);
                        hasCurrent = true;
                        continue;
                    }

                    // D. bracket-log Message: [Sender] [2:32 PM] Message
                    Match km = BracketMsgRegex.Match(line);
                    if (km.Success)
                    {
                        if (hasCurrent)
                        {
                            FinalizeParsedMessage(messages, curDateIdx, curTimeStr, curSenderIdx, curIsSystem, curBody.ToString(), participantStats, null, null);
                        }
                        curDateIdx = GetOrAddIdx(activeStickyDate, uniqueDates, dateToIdx);
                        curSenderIdx = GetOrAddSender(km.Groups[1].Value.Trim(), uniqueSenders, senderToIdx, participantStats);
                        curTimeStr = km.Groups[2].Value.Trim();
                        int hour = ExtractHourFromTime(curTimeStr);
                        if (hour >= 0 && hour < 24) hourHist[hour]++;
                        curIsSystem = false;
                        curBody = new StringBuilder(km.Groups[3].Value);
                        hasCurrent = true;
                        continue;
                    }

                    if (hasCurrent)
                    {
                        if (curBody.Length > 0) curBody.Append('\n');
                        curBody.Append(line);
                    }

                    if (i > 0 && i % 8000 == 0)
                    {
                        int pct = 68 + (int)Math.Min(22, (i * 22L) / Math.Max(1, totalLines));
                        SendProgress(
                            chatTitle,
                            "Stage 3/4: Parsing message log...",
                            pct,
                            3,
                            "Parsed " + messages.Count.ToString("N0") + " messages (" + i.ToString("N0") + " / " + totalLines.ToString("N0") + " lines)",
                            sw.ElapsedMilliseconds + " ms"
                        );
                    }
                }

                if (hasCurrent)
                {
                    FinalizeParsedMessage(messages, curDateIdx, curTimeStr, curSenderIdx, curIsSystem, curBody.ToString(), participantStats, null, null);
                }
            }

            SendProgress(
                chatTitle,
                "Stage 3/4: Building compact GPU-ready bundle...",
                91,
                3,
                "Serializing " + messages.Count.ToString("N0") + " messages...",
                sw.ElapsedMilliseconds + " ms"
            );

            // Sort participants by message count descending for stats
            List<ParticipantStat> sortedStats = new List<ParticipantStat>(participantStats);
            sortedStats.Sort((a, b) => b.Count.CompareTo(a.Count));
            string defaultMe = sortedStats.Count > 0 ? sortedStats[0].Name : "";

            int mediaCountTotal = Math.Max(_ramMediaCache.Count, Math.Max(_diskMediaIndex.Count, _zipMediaIndex.Count));
            double ramMB = Interlocked.Read(ref _ramCacheBytesTotal) / (1024.0 * 1024.0);

            _currentChatBundleBytes = SerializeChatBundleJson(
                _sessionId,
                chatTitle,
                detectedPlatform,
                defaultMe,
                mediaCountTotal,
                ramMB,
                uniqueDates,
                uniqueSenders,
                sortedStats,
                hourHist,
                messages
            );

            string readyJson = "{" +
                "\"type\":\"chat_ready\"," +
                "\"sessionId\":" + _sessionId + "," +
                "\"platform\":\"" + EscapeJson(detectedPlatform) + "\"," +
                "\"chatName\":\"" + EscapeJson(chatTitle) + "\"," +
                "\"messageCount\":" + messages.Count +
                "}";
            SendWebMessageJson(readyJson);
        }

        private void FinalizeParsedMessage(
            List<ParsedMessage> list,
            int dateIdx,
            string timeStr,
            int senderIdx,
            bool isSystem,
            string rawBody,
            List<ParticipantStat> stats,
            string explicitAttFile,
            string explicitAttType)
        {
            string body = rawBody ?? "";
            string cleaned = CleanDirectional(body).Trim();

            int flags = isSystem ? 1 : 0;

            if (cleaned.IndexOf("<This message was edited>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                flags |= 2;
                body = Regex.Replace(body, @"<This message was edited>", "", RegexOptions.IgnoreCase).Trim();
                cleaned = CleanDirectional(body).Trim();
            }

            if (string.Equals(cleaned, "This message was deleted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleaned, "This message was deleted.", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleaned, "You deleted this message", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleaned, "You deleted this message.", StringComparison.OrdinalIgnoreCase))
            {
                flags |= 4;
            }

            string attType = "";
            string attFile = "";
            long attSize = 0;
            string text = body.Trim();

            if (!string.IsNullOrEmpty(explicitAttFile))
            {
                attFile = explicitAttFile.Trim();
                attType = !string.IsNullOrEmpty(explicitAttType) ? explicitAttType : ClassifyMediaType(attFile);
            }
            else
            {
                // Detect attachment from message body
                Match iosM = IosAttachRegex.Match(cleaned);
                if (iosM.Success)
                {
                    attFile = iosM.Groups[1].Value.Trim();
                    attType = ClassifyMediaType(attFile);
                    text = cleaned.Replace(iosM.Value, "").Trim();
                }
                else
                {
                    int nlIdx = cleaned.IndexOf('\n');
                    string firstLine = (nlIdx >= 0 ? cleaned.Substring(0, nlIdx) : cleaned).Trim();
                    string restLines = nlIdx >= 0 ? cleaned.Substring(nlIdx + 1).Trim() : "";

                    Match andM = AndroidAttachRegex.Match(firstLine);
                    if (andM.Success)
                    {
                        attFile = andM.Groups[1].Value.Trim();
                        attType = ClassifyMediaType(attFile);
                        text = restLines;
                    }
                    else if (IsOmittedPlaceholder(firstLine))
                    {
                        flags |= 16;
                        attFile = firstLine;
                        attType = ClassifyOmittedType(firstLine);
                        text = restLines;
                    }
                    else
                    {
                        string normFirst = NormalizeKey(firstLine);
                        if (!string.IsNullOrEmpty(normFirst) && (_ramMediaCache.ContainsKey(normFirst) || _diskMediaIndex.ContainsKey(normFirst) || _zipMediaIndex.ContainsKey(normFirst)))
                        {
                            attFile = firstLine;
                            attType = ClassifyMediaType(attFile);
                            text = restLines;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(attFile) && (flags & 16) == 0)
            {
                string key = NormalizeKey(attFile);
                byte[] ramBytes;
                MediaZipEntryInfo zipInfo;
                string diskPath;

                if (_ramMediaCache.TryGetValue(key, out ramBytes))
                {
                    flags |= 32;
                    attSize = ramBytes.Length;
                }
                else if (_zipMediaIndex.TryGetValue(key, out zipInfo))
                {
                    flags |= 32;
                    attSize = zipInfo.Length;
                }
                else if (_diskMediaIndex.TryGetValue(key, out diskPath))
                {
                    flags |= 32;
                    try { attSize = new FileInfo(diskPath).Length; } catch { }
                }
            }

            if (string.IsNullOrEmpty(attType) && !isSystem && IsShortEmojiOnly(text))
            {
                flags |= 8;
            }

            if (!isSystem && senderIdx >= 0 && senderIdx < stats.Count)
            {
                ParticipantStat st = stats[senderIdx];
                st.Count++;
                if (!string.IsNullOrEmpty(text))
                {
                    st.Words += CountWordsFast(text);
                }
                if (!string.IsNullOrEmpty(attType) && (flags & 16) == 0)
                {
                    st.MediaCount++;
                }
            }

            list.Add(new ParsedMessage
            {
                Id = list.Count,
                DateIndex = dateIdx,
                TimeStr = timeStr,
                SenderIndex = senderIdx,
                Flags = flags,
                Text = text,
                AttType = attType,
                AttFile = attFile,
                AttSize = attSize
            });
        }

        private static byte[] SerializeChatBundleJson(
            int sessionId,
            string chatName,
            string platform,
            string defaultMe,
            int mediaFilesCount,
            double ramCacheMB,
            List<string> dates,
            List<string> senders,
            List<ParticipantStat> participants,
            int[] hourHist,
            List<ParsedMessage> messages)
        {
            StringBuilder sb = new StringBuilder(messages.Count * 96 + 4096);
            sb.Append('{');
            sb.Append("\"sessionId\":").Append(sessionId).Append(',');
            sb.Append("\"platform\":\"").Append(EscapeJson(platform ?? "emerald")).Append("\",");
            sb.Append("\"chatName\":\"").Append(EscapeJson(chatName)).Append("\",");
            sb.Append("\"defaultMe\":\"").Append(EscapeJson(defaultMe)).Append("\",");
            sb.Append("\"mediaFilesCount\":").Append(mediaFilesCount).Append(',');
            sb.Append("\"ramCacheMB\":").Append(ramCacheMB.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');

            sb.Append("\"dates\":[");
            for (int i = 0; i < dates.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(dates[i])).Append('"');
            }
            sb.Append("],");

            sb.Append("\"senders\":[");
            for (int i = 0; i < senders.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(senders[i])).Append('"');
            }
            sb.Append("],");

            sb.Append("\"hourHistogram\":[");
            for (int i = 0; i < 24; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(hourHist[i]);
            }
            sb.Append("],");

            sb.Append("\"participants\":[");
            for (int i = 0; i < participants.Count; i++)
            {
                if (i > 0) sb.Append(',');
                ParticipantStat p = participants[i];
                sb.Append("{\"name\":\"").Append(EscapeJson(p.Name))
                  .Append("\",\"count\":").Append(p.Count)
                  .Append(",\"words\":").Append(p.Words)
                  .Append(",\"mediaCount\":").Append(p.MediaCount).Append('}');
            }
            sb.Append("],");

            sb.Append("\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                ParsedMessage m = messages[i];
                sb.Append('[')
                  .Append(m.Id).Append(',')
                  .Append(m.DateIndex).Append(',')
                  .Append('"').Append(EscapeJson(m.TimeStr)).Append("\",")
                  .Append(m.SenderIndex).Append(',')
                  .Append(m.Flags).Append(',')
                  .Append('"').Append(EscapeJson(m.Text)).Append("\",")
                  .Append('"').Append(EscapeJson(m.AttType)).Append("\",")
                  .Append('"').Append(EscapeJson(m.AttFile)).Append("\",")
                  .Append(m.AttSize)
                  .Append(']');
            }
            sb.Append("]}");

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /**********************************************************************
         * 5. BUILT-IN RICH DEMO & 25,000-MESSAGE GIANT ARCHIVE GENERATOR
         **********************************************************************/
        private void BuildAndLoadRichDemoChat(bool giant25kMode)
        {
            Stopwatch sw = Stopwatch.StartNew();
            ResetMediaState();

            string title = giant25kMode
                ? "Dolomites Crew 2023–2026 (25,000-Msg Giant Archive)"
                : "Weekend Mountain Trip 🏔️";

            SendProgress(
                title,
                "Stage 1/4: Generating high-speed demo media assets...",
                15,
                1,
                "Synthesizing crisp SVG photos, transparent stickers & WAV voice notes...",
                sw.ElapsedMilliseconds + " ms"
            );

            // Generate 6 distinct scenic photos, 4 transparent stickers, playable WAV voice note, PDF & vCard in RAM
            RegisterDemoBytes("IMG-20261025-WA0001.jpg", Encoding.UTF8.GetBytes(BuildScenicPhotoSvg("#0f172a", "#1e3a8a", "#f97316", "📍 Dolomites Basecamp • 06:42 AM", "#fde047")));
            RegisterDemoBytes("00000014-PHOTO-2026-10-25-09-18-02.jpg", Encoding.UTF8.GetBytes(BuildEspressoPhotoSvg()));
            RegisterDemoBytes("IMG-20261025-WA0005.jpg", Encoding.UTF8.GetBytes(BuildScenicPhotoSvg("#022c22", "#065f46", "#10b981", "🌲 Seceda Ridge Trail • 2,518m", "#a7f3d0")));
            RegisterDemoBytes("IMG-20261025-WA0008.jpg", Encoding.UTF8.GetBytes(BuildScenicPhotoSvg("#1e1b4b", "#312e81", "#ec4899", "✨ Alpe di Siusi Sunset", "#fbcfe8")));
            RegisterDemoBytes("IMG-20261025-WA0012.jpg", Encoding.UTF8.GetBytes(BuildScenicPhotoSvg("#090d16", "#1e293b", "#38bdf8", "🌌 Milky Way over Rifugio Locatelli", "#e0f2fe")));
            RegisterDemoBytes("IMG-20261025-WA0015.jpg", Encoding.UTF8.GetBytes(BuildScenicPhotoSvg("#27272a", "#3f3f46", "#f59e0b", "🔥 Evening Campfire &amp; Guitar", "#fef08a")));

            RegisterDemoBytes("STK-20261025-WA0002.webp", Encoding.UTF8.GetBytes(BuildCatStickerSvg("LET'S GOOO!", "#fbbf24", "#10b981")));
            RegisterDemoBytes("00000019-STICKER-2026-10-25-09-21-10.webp", Encoding.UTF8.GetBytes(BuildFrogStickerSvg("ON MY WAY 🎒", "#22c55e", "#3b82f6")));
            RegisterDemoBytes("STK-20261025-WA0009.webp", Encoding.UTF8.GetBytes(BuildCatStickerSvg("100% READY 🔥", "#f97316", "#8b5cf6")));
            RegisterDemoBytes("STK-20261025-WA0011.webp", Encoding.UTF8.GetBytes(BuildFrogStickerSvg("SO GOOD ☕", "#14b8a6", "#ec4899")));

            // Infinite-looping demo GIFs / Motion clips for testing right media bar & chat loop
            RegisterDemoBytes("VID-20261025-WA0004-GIF.gif", Encoding.UTF8.GetBytes(BuildAnimatedSummitGifSvg("🚁 Seceda Ridge Drone Flyover", "#022c22", "#10b981", "#34d399")));
            RegisterDemoBytes("VID-20261025-WA0016-GIF.gif", Encoding.UTF8.GetBytes(BuildAnimatedSummitGifSvg("🔥 Campfire Sparks &amp; Aurora", "#1e1b4b", "#ec4899", "#f43f5e")));

            RegisterDemoBytes("PTT-20261025-WA0003.opus", BuildPlayableWavVoiceNote());
            RegisterDemoBytes("Trail-Map-Itinerary-2026.pdf", Encoding.UTF8.GetBytes("%PDF-1.4 Sample Trail Itinerary PDF"));
            RegisterDemoBytes("Alpine-Shuttle-Driver.vcf", Encoding.UTF8.GetBytes("BEGIN:VCARD\nFN:Marco Rossi\nTEL:+39 0471 555123\nEND:VCARD"));

            double ramMB = Interlocked.Read(ref _ramCacheBytesTotal) / (1024.0 * 1024.0);
            SendProgress(
                title,
                "Stage 2/4: Pre-caching photos & stickers into RAM...",
                52,
                2,
                "All photos, stickers, GIFs & voice notes locked in RAM hot-cache",
                ramMB.ToString("0.0") + " MB in RAM"
            );

            if (!giant25kMode)
            {
                string sampleChatTxt = string.Join("\n", new string[]
                {
                    "10/23/26, 7:10 PM - This archive is stored locally on this computer. Nothing is uploaded anywhere.",
                    "10/23/26, 7:12 PM - Maya created group \"Weekend Mountain Trip 🏔️\"",
                    "10/23/26, 7:15 PM - Maya: Trail-Map-Itinerary-2026.pdf (file attached)\nHere is the offline GPX & refuge map in case cell reception drops at the pass!",
                    "10/23/26, 7:18 PM - Alex: STK-20261025-WA0002.webp (file attached)",
                    "10/23/26, 8:40 PM - Jordan: IMG-20261025-WA0015.jpg (file attached)\nPre-trip campfire gear check in the backyard 🔥",
                    "10/23/26, 8:45 PM - Maya: VID-20261025-WA0016-GIF.gif (file attached)\nCampfire timelapse GIF looping non-stop! ✨",
                    "10/24/26, 6:44 AM - Alex: IMG-20261025-WA0001.jpg (file attached)\nLook at the alpenglow from the basecamp parking lot right now!! *Unbelievable* view.",
                    "10/24/26, 6:45 AM - Maya: 😱🔥",
                    "10/24/26, 6:46 AM - Jordan: 00000019-STICKER-2026-10-25-09-21-10.webp (file attached)",
                    "10/24/26, 6:48 AM - Maya: PTT-20261025-WA0003.opus (file attached)",
                    "10/24/26, 9:18 AM - Alex: <attached: 00000014-PHOTO-2026-10-25-09-18-02.jpg>\nSummit espresso checkpoint achieved ☕⛰️ <This message was edited>",
                    "10/24/26, 9:22 AM - Maya: STK-20261025-WA0011.webp (file attached)",
                    "10/25/26, 11:40 AM - Jordan: IMG-20261025-WA0005.jpg (file attached)\nRidge line is completely clear today!",
                    "10/25/26, 12:05 PM - Alex: VID-20261025-WA0004-GIF.gif (file attached)\nQuick drone flyover GIF across the Seceda ridge! 🚁⛰️",
                    "10/25/26, 2:15 PM - Maya: STK-20261025-WA0009.webp (file attached)",
                    "10/25/26, 5:42 PM - Alex: IMG-20261025-WA0008.jpg (file attached)\nGolden hour hitting the peaks just before we head down.",
                    "10/25/26, 6:10 PM - Jordan: Alpine-Shuttle-Driver.vcf (file attached)\nSaved Marco's number here for our return shuttle from Cortina.",
                    "10/25/26, 9:20 PM - Maya: IMG-20261025-WA0012.jpg (file attached)\nLong exposure shot from the refuge deck tonight 🌌",
                    "10/25/26, 9:24 PM - Maya: This message was deleted",
                    "10/25/26, 9:25 PM - Alex: Open *⚙️ Settings → Tools* to run the 25K Giant Test, or drop your own multi-GB chat export `.zip` onto the window to see how fast this native app runs! 🚀"
                });

                ParseAndFinalizeChat(title, sampleChatTxt, sw);
                return;
            }

            // Build 25,000-message giant multi-year archive log
            StringBuilder giantSb = new StringBuilder(25000 * 85);
            giantSb.AppendLine("01/01/24, 9:00 AM - This archive is stored locally on this computer. Nothing is uploaded anywhere.");
            giantSb.AppendLine("01/01/24, 9:01 AM - Maya created group \"Dolomites Crew 2023–2026\"");

            string[] senders = new string[] { "Alex", "Maya", "Jordan", "Elena", "Liam" };
            string[] photos = new string[] {
                "IMG-20261025-WA0001.jpg",
                "00000014-PHOTO-2026-10-25-09-18-02.jpg",
                "IMG-20261025-WA0005.jpg",
                "IMG-20261025-WA0008.jpg",
                "IMG-20261025-WA0012.jpg",
                "IMG-20261025-WA0015.jpg"
            };
            string[] stickers = new string[] {
                "STK-20261025-WA0002.webp",
                "00000019-STICKER-2026-10-25-09-21-10.webp",
                "STK-20261025-WA0009.webp",
                "STK-20261025-WA0011.webp"
            };
            string[] gifs = new string[] {
                "VID-20261025-WA0004-GIF.gif",
                "VID-20261025-WA0016-GIF.gif"
            };
            string[] sampleTexts = new string[] {
                "Anyone up for an early morning trail run tomorrow? 🏃‍♂️⛰️",
                "Just checked the weather radar — clear skies all weekend!",
                "Don't forget to pack headlamps and extra layers for the pass.",
                "That espresso at the refuge was *honestly* the best coffee I've had all year ☕",
                " Uploading the full resolution RAW shots to the shared drive tonight!",
                "Wait until you see the panorama from 2,500m!",
                "Who has the GPX track for the Seceda loop?",
                "🔥🏔️✨",
                "We made it to the summit in 2 hours 15 minutes — new personal best!",
                "Let's book the mountain hut for next month before it fills up."
            };

            DateTime dt = new DateTime(2024, 1, 1, 9, 5, 0);
            for (int i = 0; i < 25000; i++)
            {
                dt = dt.AddMinutes((i % 7) + 1);
                string dStr = dt.ToString("MM/dd/yy", CultureInfo.InvariantCulture);
                string tStr = dt.ToString("h:mm tt", CultureInfo.InvariantCulture);
                string sName = senders[i % senders.Length];

                if (i % 45 == 0)
                {
                    string ph = photos[(i / 45) % photos.Length];
                    giantSb.Append(dStr).Append(", ").Append(tStr).Append(" - ").Append(sName).Append(": ").Append(ph).Append(" (file attached)\n")
                           .Append("Checkpoint photo #").Append((i / 45) + 1).Append(" from the ridge! *Zero lag* instant photo loading.\n");
                }
                else if (i % 65 == 0)
                {
                    string st = stickers[(i / 65) % stickers.Length];
                    giantSb.Append(dStr).Append(", ").Append(tStr).Append(" - ").Append(sName).Append(": ").Append(st).Append(" (file attached)\n");
                }
                else if (i % 110 == 0)
                {
                    string gf = gifs[(i / 110) % gifs.Length];
                    giantSb.Append(dStr).Append(", ").Append(tStr).Append(" - ").Append(sName).Append(": ").Append(gf).Append(" (file attached)\n")
                           .Append("Looping ridge clip #").Append((i / 110) + 1).Append(" 🎬✨\n");
                }
                else if (i % 180 == 0)
                {
                    giantSb.Append(dStr).Append(", ").Append(tStr).Append(" - ").Append(sName).Append(": PTT-20261025-WA0003.opus (file attached)\n");
                }
                else
                {
                    string msgTxt = sampleTexts[i % sampleTexts.Length];
                    giantSb.Append(dStr).Append(", ").Append(tStr).Append(" - ").Append(sName).Append(": ").Append(msgTxt).Append(" [msg #").Append(i + 1).Append("]\n");
                }
            }

            ParseAndFinalizeChat(title, giantSb.ToString(), sw);
        }

        private void RegisterDemoBytes(string fileName, byte[] bytes)
        {
            string key = NormalizeKey(fileName);
            if (_ramMediaCache.TryAdd(key, bytes))
            {
                Interlocked.Add(ref _ramCacheBytesTotal, bytes.Length);
            }
        }

        private static string BuildScenicPhotoSvg(string c1, string c2, string c3, string label, string sunColor)
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 800 500\">" +
                   "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">" +
                   "<stop offset=\"0%\" stop-color=\"" + c1 + "\"/><stop offset=\"55%\" stop-color=\"" + c2 + "\"/><stop offset=\"100%\" stop-color=\"" + c3 + "\"/>" +
                   "</linearGradient></defs>" +
                   "<rect width=\"800\" height=\"500\" fill=\"url(#g)\"/>" +
                   "<circle cx=\"590\" cy=\"190\" r=\"56\" fill=\"" + sunColor + "\" opacity=\"0.9\"/>" +
                   "<polygon points=\"0,500 230,180 470,500\" fill=\"#1e293b\"/>" +
                   "<polygon points=\"230,180 300,275 180,275\" fill=\"#f8fafc\" opacity=\"0.85\"/>" +
                   "<polygon points=\"260,500 520,140 790,500\" fill=\"#0f172a\"/>" +
                   "<polygon points=\"520,140 600,260 455,260\" fill=\"#f8fafc\" opacity=\"0.9\"/>" +
                   "<rect y=\"425\" width=\"800\" height=\"75\" fill=\"#000000\" opacity=\"0.45\"/>" +
                   "<text x=\"30\" y=\"470\" fill=\"#ffffff\" font-family=\"sans-serif\" font-size=\"22\" font-weight=\"bold\">" + label + "</text></svg>";
        }

        private static string BuildEspressoPhotoSvg()
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 700 480\">" +
                   "<rect width=\"700\" height=\"480\" fill=\"#18181b\"/>" +
                   "<circle cx=\"350\" cy=\"235\" r=\"150\" fill=\"#d4d4d8\"/>" +
                   "<circle cx=\"350\" cy=\"235\" r=\"118\" fill=\"#78350f\"/>" +
                   "<circle cx=\"350\" cy=\"235\" r=\"92\" fill=\"#b45309\"/>" +
                   "<path d=\"M350 190 C320 160, 290 205, 350 260 C410 205, 380 160, 350 190 Z\" fill=\"#fef3c7\"/>" +
                   "<rect x=\"475\" y=\"215\" width=\"52\" height=\"38\" rx=\"14\" fill=\"#d4d4d8\"/>" +
                   "<text x=\"30\" y=\"442\" fill=\"#fbbf24\" font-family=\"sans-serif\" font-size=\"21\" font-weight=\"bold\">☕ Morning Summit Espresso</text></svg>";
        }

        private static string BuildAnimatedSummitGifSvg(string title, string bg1, string bg2, string accent)
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 800 450\">" +
                   "<defs><linearGradient id=\"bg\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">" +
                   "<stop offset=\"0%\" stop-color=\"" + bg1 + "\"/><stop offset=\"100%\" stop-color=\"" + bg2 + "\"/>" +
                   "</linearGradient></defs>" +
                   "<rect width=\"800\" height=\"450\" fill=\"url(#bg)\"/>" +
                   "<circle cx=\"630\" cy=\"115\" r=\"42\" fill=\"" + accent + "\" opacity=\"0.85\">" +
                   "<animate attributeName=\"r\" values=\"38;52;38\" dur=\"1.8s\" repeatCount=\"indefinite\"/>" +
                   "<animate attributeName=\"opacity\" values=\"0.65;1;0.65\" dur=\"1.8s\" repeatCount=\"indefinite\"/>" +
                   "</circle>" +
                   "<g>" +
                   "<animateTransform attributeName=\"transform\" type=\"translate\" values=\"-30,0; 30,0; -30,0\" dur=\"3.2s\" repeatCount=\"indefinite\"/>" +
                   "<polygon points=\"-40,450 220,150 480,450\" fill=\"#0f172a\" opacity=\"0.85\"/>" +
                   "<polygon points=\"240,450 530,110 840,450\" fill=\"#1e293b\" opacity=\"0.92\"/>" +
                   "<polygon points=\"530,110 610,220 460,220\" fill=\"#f8fafc\" opacity=\"0.9\"/>" +
                   "</g>" +
                   "<g>" +
                   "<animateTransform attributeName=\"transform\" type=\"translate\" values=\"0,0; 220,-18; 0,0\" dur=\"2.6s\" repeatCount=\"indefinite\"/>" +
                   "<circle cx=\"260\" cy=\"145\" r=\"8\" fill=\"#fde047\"/>" +
                   "<rect x=\"235\" y=\"138\" width=\"50\" height=\"10\" rx=\"5\" fill=\"#ffffff\"/>" +
                   "</g>" +
                   "<text x=\"400\" y=\"355\" text-anchor=\"middle\" fill=\"#ffffff\" opacity=\"0.9\" font-family=\"sans-serif\" font-size=\"22\" font-weight=\"bold\">" + title + "</text></svg>";
        }

        private static string BuildCatStickerSvg(string caption, string bodyColor, string badgeColor)
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 240 240\">" +
                   "<g>" +
                   "<animateTransform attributeName=\"transform\" type=\"translate\" values=\"0,0; 0,-9; 0,0\" dur=\"1.1s\" repeatCount=\"indefinite\"/>" +
                   "<circle cx=\"120\" cy=\"128\" r=\"82\" fill=\"" + bodyColor + "\" stroke=\"#ffffff\" stroke-width=\"10\"/>" +
                   "<polygon points=\"55,85 75,30 110,65\" fill=\"" + bodyColor + "\" stroke=\"#ffffff\" stroke-width=\"8\" stroke-linejoin=\"round\"/>" +
                   "<polygon points=\"185,85 165,30 130,65\" fill=\"" + bodyColor + "\" stroke=\"#ffffff\" stroke-width=\"8\" stroke-linejoin=\"round\"/>" +
                   "<g>" +
                   "<animateTransform attributeName=\"transform\" type=\"translate\" values=\"0,0; 0,-3; 0,0\" dur=\"0.55s\" repeatCount=\"indefinite\"/>" +
                   "<rect x=\"64\" y=\"102\" width=\"48\" height=\"28\" rx=\"6\" fill=\"#0f172a\"/>" +
                   "<rect x=\"128\" y=\"102\" width=\"48\" height=\"28\" rx=\"6\" fill=\"#0f172a\"/>" +
                   "<line x1=\"112\" y1=\"112\" x2=\"128\" y2=\"112\" stroke=\"#0f172a\" stroke-width=\"6\"/>" +
                   "</g>" +
                   "<path d=\"M98 152 Q120 172 142 152\" fill=\"none\" stroke=\"#0f172a\" stroke-width=\"6\" stroke-linecap=\"round\"/>" +
                   "</g>" +
                   "<rect x=\"36\" y=\"182\" width=\"168\" height=\"38\" rx=\"19\" fill=\"" + badgeColor + "\" stroke=\"#ffffff\" stroke-width=\"5\">" +
                   "<animate attributeName=\"rx\" values=\"19;15;19\" dur=\"1.1s\" repeatCount=\"indefinite\"/>" +
                   "</rect>" +
                   "<text x=\"120\" y=\"207\" text-anchor=\"middle\" fill=\"#ffffff\" font-family=\"sans-serif\" font-weight=\"900\" font-size=\"17\">" + caption + "</text></svg>";
        }

        private static string BuildFrogStickerSvg(string caption, string bodyColor, string badgeColor)
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 240 240\">" +
                   "<g>" +
                   "<animateTransform attributeName=\"transform\" type=\"translate\" values=\"0,0; 0,-8; 0,0\" dur=\"1.25s\" repeatCount=\"indefinite\"/>" +
                   "<circle cx=\"82\" cy=\"82\" r=\"28\" fill=\"" + bodyColor + "\" stroke=\"#ffffff\" stroke-width=\"8\"/>" +
                   "<circle cx=\"158\" cy=\"82\" r=\"28\" fill=\"" + bodyColor + "\" stroke=\"#ffffff\" stroke-width=\"8\"/>" +
                   "<ellipse cx=\"120\" cy=\"135\" rx=\"78\" ry=\"62\" fill=\"" + bodyColor + "\" stroke=\"#ffffff\" stroke-width=\"10\"/>" +
                   "<circle cx=\"82\" cy=\"80\" r=\"11\" fill=\"#0f172a\">" +
                   "<animate attributeName=\"r\" values=\"11;3;11\" dur=\"2.2s\" repeatCount=\"indefinite\"/>" +
                   "</circle>" +
                   "<circle cx=\"158\" cy=\"80\" r=\"11\" fill=\"#0f172a\">" +
                   "<animate attributeName=\"r\" values=\"11;3;11\" dur=\"2.2s\" repeatCount=\"indefinite\"/>" +
                   "</circle>" +
                   "<path d=\"M95 142 Q120 160 145 142\" fill=\"none\" stroke=\"#0f172a\" stroke-width=\"6\" stroke-linecap=\"round\"/>" +
                   "</g>" +
                   "<rect x=\"42\" y=\"180\" width=\"156\" height=\"36\" rx=\"18\" fill=\"" + badgeColor + "\" stroke=\"#ffffff\" stroke-width=\"5\"/>" +
                   "<text x=\"120\" y=\"204\" text-anchor=\"middle\" fill=\"#ffffff\" font-family=\"sans-serif\" font-weight=\"900\" font-size=\"16\">" + caption + "</text></svg>";
        }

        private static byte[] BuildPlayableWavVoiceNote()
        {
            int sampleRate = 22050;
            int numSamples = sampleRate * 3;
            byte[] buf = new byte[44 + numSamples * 2];
            using (MemoryStream ms = new MemoryStream(buf))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + numSamples * 2);
                bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)1);
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(numSamples * 2);

                double[] freqs = new double[] { 261.63, 329.63, 392.00, 523.25 };
                for (int i = 0; i < numSamples; i++)
                {
                    double t = (double)i / sampleRate;
                    int noteIdx = Math.Min(3, (int)(t / 0.75));
                    double localT = (t % 0.75) / 0.75;
                    double env = Math.Sin(Math.PI * localT);
                    short pcm = (short)(Math.Sin(2.0 * Math.PI * freqs[noteIdx] * t) * 0.35 * env * 32767);
                    bw.Write(pcm);
                }
            }
            return buf;
        }

        /**********************************************************************
         * 6. HELPER UTILITIES
         **********************************************************************/
        private static string CleanDirectional(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\u200e' || c == '\u200f' || (c >= '\u202a' && c <= '\u202e') || c == '\ufeff')
                    continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string NormalizeKey(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            string cleaned = CleanDirectional(fileName).Trim();
            int slash = Math.Max(cleaned.LastIndexOf('/'), cleaned.LastIndexOf('\\'));
            if (slash >= 0) cleaned = cleaned.Substring(slash + 1);
            return cleaned.ToLowerInvariant();
        }

        private static string CleanChatTitle(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Chat Archive";
            string s = Regex.Replace(raw, @"^WhatsApp Chat with\s+", "", RegexOptions.IgnoreCase).Trim();
            if (string.Equals(s, "_chat", StringComparison.OrdinalIgnoreCase)) return "Chat Archive";
            return s;
        }

        private static string ClassifyMediaType(string fileName)
        {
            string lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".webp") || lower.StartsWith("stk-") || lower.Contains("-sticker-") || lower.Contains("sticker"))
                return "sticker";
            if (lower.EndsWith(".mp4") || lower.EndsWith(".mov") || lower.EndsWith(".webm") || lower.EndsWith(".3gp") || lower.EndsWith(".gif") || lower.StartsWith("vid-") || lower.Contains("-video-") || lower.Contains("-gif-"))
                return "video";
            if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".png") || lower.EndsWith(".bmp") || lower.EndsWith(".svg") || lower.EndsWith(".heic") || lower.StartsWith("img-") || lower.Contains("-photo-"))
                return "photo";
            if (lower.EndsWith(".opus") || lower.EndsWith(".ogg") || lower.EndsWith(".mp3") || lower.EndsWith(".m4a") || lower.EndsWith(".wav") || lower.EndsWith(".aac") || lower.StartsWith("ptt-") || lower.StartsWith("aud-") || lower.Contains("-audio-"))
                return "audio";
            if (lower.EndsWith(".vcf"))
                return "vcard";
            return "document";
        }

        // Guarantees that animated WebP stickers and GIFs always loop infinitely (LoopCount = 0)
        private static void EnsureInfiniteLoopMedia(byte[] data)
        {
            if (data == null || data.Length < 30) return;

            // 1. Animated WebP ("RIFF" .... "WEBP"): patch ANIM chunk LoopCount (uint16 at chunk payload +4) to 0
            if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
                data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            {
                int offset = 12;
                while (offset + 8 <= data.Length)
                {
                    int chunkSize = data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24);
                    if (chunkSize < 0) break;

                    if (data[offset] == (byte)'A' && data[offset + 1] == (byte)'N' &&
                        data[offset + 2] == (byte)'I' && data[offset + 3] == (byte)'M' &&
                        chunkSize >= 6 && offset + 14 <= data.Length)
                    {
                        // LoopCount == 0 in WebP ANIM chunk specification means infinite loop
                        data[offset + 12] = 0;
                        data[offset + 13] = 0;
                        break;
                    }

                    int paddedSize = (chunkSize + 1) & ~1;
                    if (paddedSize < 0 || offset + 8 + paddedSize <= offset) break;
                    offset += 8 + paddedSize;
                }
                return;
            }

            // 2. Animated GIF ("GIF89a" / "GIF87a"): patch NETSCAPE2.0 / ANIMEXTS1.0 loop count to 0
            if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8')
            {
                int limit = Math.Min(data.Length - 19, 4096);
                for (int i = 6; i < limit; i++)
                {
                    if (data[i] == 0x21 && data[i + 1] == 0xFF && data[i + 2] == 0x0B)
                    {
                        // Check sub-block [0x03, 0x01, lo, hi, 0x00] at i + 14
                        if (data[i + 14] == 0x03 && data[i + 15] == 0x01)
                        {
                            data[i + 16] = 0;
                            data[i + 17] = 0;
                            break;
                        }
                    }
                }
            }
        }

        private static bool IsOmittedPlaceholder(string line)
        {
            return string.Equals(line, "<Media omitted>", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(line, "image omitted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(line, "video omitted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(line, "sticker omitted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(line, "audio omitted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(line, "GIF omitted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(line, "document omitted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(line, "Contact card omitted", StringComparison.OrdinalIgnoreCase);
        }

        private static string ClassifyOmittedType(string line)
        {
            string lower = line.ToLowerInvariant();
            if (lower.Contains("sticker")) return "sticker";
            if (lower.Contains("audio")) return "audio";
            if (lower.Contains("video") || lower.Contains("gif")) return "video";
            if (lower.Contains("document") || lower.Contains("contact")) return "document";
            return "photo";
        }

        private static int ExtractHourFromTime(string timeStr)
        {
            if (string.IsNullOrEmpty(timeStr)) return 12;
            int colon = timeStr.IndexOf(':');
            if (colon <= 0) return 12;
            int hr;
            if (!int.TryParse(timeStr.Substring(0, colon).Trim(), out hr)) return 12;
            bool isPm = timeStr.IndexOf("PM", StringComparison.OrdinalIgnoreCase) >= 0 || timeStr.IndexOf("p.m", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isAm = timeStr.IndexOf("AM", StringComparison.OrdinalIgnoreCase) >= 0 || timeStr.IndexOf("a.m", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isPm && hr < 12) hr += 12;
            if (isAm && hr == 12) hr = 0;
            return (hr >= 0 && hr < 24) ? hr : 12;
        }

        private static int CountWordsFast(string s)
        {
            int count = 0;
            bool inWord = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsWhiteSpace(s[i]))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    count++;
                }
            }
            return count;
        }

        private static bool IsShortEmojiOnly(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string trimmed = s.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 10) return false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (char.IsLetterOrDigit(c) || (c >= 33 && c <= 126)) return false;
            }
            return true;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string ExtractJsonStringField(string json, string fieldName)
        {
            string pattern = "\"" + fieldName + "\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[++i];
                    if (next == 'n') sb.Append('\n');
                    else if (next == 'r') sb.Append('\r');
                    else if (next == 't') sb.Append('\t');
                    else sb.Append(next);
                }
                else if (c == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
