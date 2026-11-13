// 照片管家 —— 并集导入 + 可视化去重
//
// 设计目标：
//   1) 导入：内容级并集，绝不产生 xxx_1.jpg
//   2) 去重：先看清楚，再删；删除走回收站
//   3) 界面：一个窗口两页，主操作突出，出错能看懂

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

[assembly: System.Reflection.AssemblyTitle("PhotoManager")]
[assembly: System.Reflection.AssemblyProduct("照片管家")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace PhotoManager
{
    /// <summary>文件日志：出问题时能查。写在 exe 旁边，写不了就退到临时目录。</summary>
    internal static class Log2
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string FilePath
        {
            get
            {
                if (_path != null) return _path;
                try
                {
                    var baseDir = "";
                    try
                    {
                        var loc = typeof(Log2).Assembly.Location;
                        if (!string.IsNullOrEmpty(loc)) baseDir = Path.GetDirectoryName(loc);
                    }
                    catch { }
                    if (string.IsNullOrEmpty(baseDir))
                    {
                        try { baseDir = AppDomain.CurrentDomain.BaseDirectory; } catch { baseDir = ""; }
                    }
                    var p = string.IsNullOrEmpty(baseDir)
                        ? Path.Combine(Path.GetTempPath(), "照片管家.log")
                        : Path.Combine(baseDir, "照片管家.log");
                    File.AppendAllText(p, "");
                    _path = p;
                }
                catch
                {
                    try { _path = Path.Combine(Path.GetTempPath(), "照片管家.log"); }
                    catch { _path = "照片管家.log"; }
                }
                return _path;
            }
        }

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    var p = FilePath;
                    try { if (File.Exists(p) && new FileInfo(p).Length > 4 * 1024 * 1024) File.Delete(p); } catch { }
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Open()
        {
            try
            {
                var p = FilePath;
                if (!File.Exists(p)) File.AppendAllText(p, "");
                System.Diagnostics.Process.Start("notepad.exe", "\"" + p + "\"");
            }
            catch { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log2.Write("未处理异常: " + e.ExceptionObject);
                try
                {
                    MessageBox.Show("程序出错：\n" + e.ExceptionObject + "\n\n日志：" + Log2.FilePath,
                        "照片管家", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };

            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Log2.Write("界面异常: " + e.Exception);
                try
                {
                    MessageBox.Show("操作出错：\n" + e.Exception.Message + "\n\n日志：" + Log2.FilePath,
                        "照片管家", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };

            Log2.Write("==== 启动 ====");
            Application.Run(new MainForm());
        }
    }

    internal enum DateLayout { None = 0, Year = 1, YearMonth = 2 }

    internal enum FileKind { All = 0, Images = 1, Videos = 2 }

    internal sealed class DupGroup
    {
        public long Size;
        public string Hash;
        public int Index;
        public List<string> Files = new List<string>();   // [0] 保留
        public string Keeper { get { return Files.Count > 0 ? Files[0] : ""; } }
    }

    internal sealed class DupRow
    {
        public DupGroup Group;
        public string Path;
        public bool IsKeeper;
    }

    /// <summary>记住上次用的路径和选项。</summary>
    internal sealed class AppSettings
    {
        public string Source = "";
        public string Dest = "";
        public string DedupeDir = "";
        public int LayoutIndex = 0;
        public int KindIndex = 0;
        public bool MoveMode = false;
        public bool IncludeSub = true;
        public bool KeepStructure = true;
        public bool DryRun = false;
        public bool AutoScan = true;
        public bool DedupeSub = true;

        private static string FilePath
        {
            get
            {
                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "照片管家");
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "last.ini");
                }
                catch
                {
                    try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last.ini"); }
                    catch { return Path.Combine(Path.GetTempPath(), "照片管家-last.ini"); }
                }
            }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1);
                    switch (k)
                    {
                        case "source": s.Source = v; break;
                        case "dest": s.Dest = v; break;
                        case "dedupe": s.DedupeDir = v; break;
                        case "layout": int.TryParse(v, out s.LayoutIndex); break;
                        case "kind": int.TryParse(v, out s.KindIndex); break;
                        case "move": s.MoveMode = v == "1"; break;
                        case "includeSub": s.IncludeSub = v == "1"; break;
                        case "keep": s.KeepStructure = v == "1"; break;
                        case "dry": s.DryRun = v == "1"; break;
                        case "autoscan": s.AutoScan = v == "1"; break;
                        case "dsub": s.DedupeSub = v == "1"; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var lines = new[]
                {
                    "source=" + Source, "dest=" + Dest, "dedupe=" + DedupeDir,
                    "layout=" + LayoutIndex,
                    "kind=" + KindIndex,
                    "move=" + (MoveMode ? "1" : "0"),
                    "includeSub=" + (IncludeSub ? "1" : "0"),
                    "keep=" + (KeepStructure ? "1" : "0"),
                    "dry=" + (DryRun ? "1" : "0"),
                    "autoscan=" + (AutoScan ? "1" : "0"),
                    "dsub=" + (DedupeSub ? "1" : "0")
                };
                File.WriteAllLines(FilePath, lines, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class Ops
    {
        public const string ConflictDir = "_同名冲突";

        public static string HashFile(string path, CancellationToken ct)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                var sb = new StringBuilder(64);
                foreach (var b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static bool IsInternal(string path)
        {
            foreach (var seg in path.Split(Path.DirectorySeparatorChar))
                if (seg.StartsWith("_重复文件_", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 遍历文件。自己走目录，遇到没权限/坏掉的子目录就跳过并记日志，
        /// 而不是像 Directory.EnumerateFiles 那样整个操作直接失败。
        /// </summary>
        public static List<string> Scan(string root, bool includeSub, FileKind kind, Action<int> onProgress = null)
        {
            var list = new List<string>();
            if (!Directory.Exists(root))
            {
                Log2.Write("扫描失败，目录不存在: " + root);
                return list;
            }

            var stack = new Stack<string>();
            stack.Push(root);
            int skippedDirs = 0;

            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (Exception ex)
                {
                    skippedDirs++;
                    Log2.Write("跳过无法读取的目录: " + dir + "  " + ex.Message);
                    files = null;
                }
                if (files != null)
                {
                    foreach (var f in files)
                    {
                        try { if (!IsInternal(f) && KindMatch(f, kind)) list.Add(f); } catch { }
                    }
                    if (onProgress != null) onProgress(list.Count);
                }

                if (!includeSub) continue;

                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch (Exception ex)
                {
                    skippedDirs++;
                    Log2.Write("跳过无法读取的子目录列表: " + dir + "  " + ex.Message);
                    continue;
                }
                foreach (var s in subs)
                {
                    try
                    {
                        if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; // 不跟符号链接，防止绕圈
                    }
                    catch { continue; }
                    stack.Push(s);
                }
            }

            if (skippedDirs > 0) Log2.Write("扫描时跳过 " + skippedDirs + " 个目录（无权限/不可读）");
            return list;
        }

        public static string RelativeTo(string root, string path)
        {
            var r = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? path.Substring(r.Length) : Path.GetFileName(path);
        }

        private static int Depth(string path) { return path.Count(c => c == Path.DirectorySeparatorChar); }

        private static bool HasNumericSuffix(string path)
        {
            return Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"_\d+$");
        }

        // ── 文件类型 ────────────────────────────────────────────────────────
        private static readonly HashSet<string> ImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif",
            ".avif", ".jfif", ".ico", ".jxl",
            ".raw", ".arw", ".srf", ".sr2", ".cr2", ".cr3", ".nef", ".nrw", ".dng", ".orf", ".rw2", ".raf", ".pef", ".srw", ".3fr"
        };

        private static readonly HashSet<string> VideoExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".qt", ".avi", ".mkv", ".wmv", ".flv", ".f4v", ".3gp", ".3g2",
            ".mpg", ".mpeg", ".m2v", ".ts", ".mts", ".m2ts", ".webm", ".vob", ".rmvb", ".rm", ".asf", ".ogv", ".divx"
        };

        public static bool IsImage(string path)
        {
            return ImageExt.Contains(Path.GetExtension(path) ?? "");
        }

        public static bool IsVideo(string path)
        {
            return VideoExt.Contains(Path.GetExtension(path) ?? "");
        }

        public static bool KindMatch(string path, FileKind kind)
        {
            if (kind == FileKind.Images) return IsImage(path);
            if (kind == FileKind.Videos) return IsVideo(path);
            return true;
        }

        /// <summary>MP4/MOV 的创建时间（moov → mvhd，秒数以 1904 为起点）。</summary>
        private static bool TryMp4Time(string path, out DateTime result)
        {
            result = DateTime.MinValue;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long pos = 0;
                    long len = fs.Length;
                    var head = new byte[16];
                    int guard = 0;
                    while (pos + 8 <= len && guard++ < 512)
                    {
                        fs.Position = pos;
                        if (fs.Read(head, 0, 8) != 8) break;
                        long size = ((long)head[0] << 24) | ((long)head[1] << 16) | ((long)head[2] << 8) | head[3];
                        string type = Encoding.ASCII.GetString(head, 4, 4);
                        long body = pos + 8;
                        if (size == 1)
                        {
                            if (fs.Read(head, 8, 8) != 8) break;
                            size = 0;
                            for (int i = 8; i < 16; i++) size = (size << 8) | head[i];
                            body = pos + 16;
                        }
                        else if (size == 0) size = len - pos;
                        if (size < 8) break;

                        if (type == "moov") { pos = body; continue; }
                        if (type == "mvhd")
                        {
                            fs.Position = body;
                            int ver = fs.ReadByte();
                            fs.Position += 3; // 跳过 flags
                            if (ver == 1)
                            {
                                var b8 = new byte[8];
                                if (fs.Read(b8, 0, 8) != 8) break;
                                ulong secs = 0;
                                for (int i = 0; i < 8; i++) secs = (secs << 8) | b8[i];
                                result = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((double)secs).ToLocalTime();
                            }
                            else
                            {
                                var b4 = new byte[4];
                                if (fs.Read(b4, 0, 4) != 4) break;
                                uint secs = ((uint)b4[0] << 24) | ((uint)b4[1] << 16) | ((uint)b4[2] << 8) | b4[3];
                                result = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs).ToLocalTime();
                            }
                            return result.Year >= 1990 && result <= DateTime.Now.AddDays(1);
                        }
                        pos += size;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>从文件名猜时间（相机/手机/微信的常见命名）。</summary>
        private static bool TryNameTime(string name, out DateTime result)
        {
            result = DateTime.MinValue;
            var m = Regex.Match(name, @"(20\d{2})(\d{2})(\d{2})[_\-\s]?(\d{2})(\d{2})(\d{2})");
            if (m.Success)
            {
                if (DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value
                        + m.Groups[4].Value + m.Groups[5].Value + m.Groups[6].Value,
                        "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                    return true;
            }
            m = Regex.Match(name, @"(?:wx_camera|mmexport)_(\d{13})");
            if (m.Success)
            {
                long ms;
                if (long.TryParse(m.Groups[1].Value, out ms))
                {
                    try { result = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime; return true; } catch { }
                }
            }
            m = Regex.Match(name, @"(20\d{2})[\-_]?(\d{2})[\-_]?(\d{2})");
            if (m.Success)
            {
                if (DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value,
                        "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                    return result.Year >= 1990;
            }
            return false;
        }

        private static bool TryExifTime(string path, out DateTime result)
        {
            result = DateTime.MinValue;
            try
            {
                using (var img = Image.FromFile(path))
                {
                    int[] ids = new int[] { 36867, 36868, 306 };
                    foreach (var id in ids)
                    {
                        try
                        {
                            var pi = img.GetPropertyItem(id);
                            if (pi == null || pi.Value == null) continue;
                            var s = Encoding.ASCII.GetString(pi.Value).Trim('\0', ' ', '\r', '\n');
                            DateTime dt;
                            if (DateTime.TryParseExact(s, new string[] { "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                            {
                                result = dt;
                                return true;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>统一的时间判断：图片看 EXIF，视频看容器元数据，再退到文件名、文件时间。</summary>
        public static DateTime GuessTime(string path)
        {
            try
            {
                if (IsImage(path))
                {
                    DateTime exif;
                    if (TryExifTime(path, out exif)) return exif;
                }
                else if (IsVideo(path))
                {
                    DateTime mp4;
                    if (TryMp4Time(path, out mp4)) return mp4;
                }
                DateTime byName;
                if (TryNameTime(Path.GetFileName(path), out byName)) return byName;
            }
            catch { }
            try { return File.GetLastWriteTime(path); } catch { return DateTime.Now; }
        }

        public static DateTime ShotTime(string path)
        {
            try
            {
                using (var img = Image.FromFile(path))
                {
                    int[] ids = new int[] { 36867, 36868, 306 };
                    foreach (var id in ids)
                    {
                        try
                        {
                            var pi = img.GetPropertyItem(id);
                            if (pi == null || pi.Value == null) continue;
                            var s = Encoding.ASCII.GetString(pi.Value).Trim('\0', ' ', '\r', '\n');
                            DateTime dt;
                            if (DateTime.TryParseExact(s, new string[] { "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                                return dt;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            try { return File.GetLastWriteTime(path); } catch { return DateTime.Now; }
        }

        private static string DateSubDir(DateTime t, DateLayout layout)
        {
            if (layout == DateLayout.Year) return t.ToString("yyyy");
            if (layout == DateLayout.YearMonth) return Path.Combine(t.ToString("yyyy"), t.ToString("yyyy-MM"));
            return "";
        }

        private static string AvailableConflictPath(string path, HashSet<string> plannedTargets)
        {
            var candidate = path;
            int number = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate) || plannedTargets.Contains(candidate))
            {
                candidate = Path.Combine(Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path) + "_冲突" + number + Path.GetExtension(path));
                number++;
            }
            return candidate;
        }

        /// <summary>最近一次导入的失败明细（给结果弹窗用）。</summary>
        public static readonly List<string> LastErrors = new List<string>();
        /// <summary>因为权限被拒而失败的数量（用来识别受控文件夹访问）。</summary>
        public static int UnauthorizedCount;

        /// <summary>测试目标文件夹能不能写：能写返回 null，不能返回具体错误。</summary>
        public static string TestWrite(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "没有填目标文件夹。";
            if (!Directory.Exists(dir)) return "文件夹不存在：\n" + dir;
            var probe = Path.Combine(dir, "_写入测试_" + Environment.TickCount + ".tmp");
            try
            {
                File.WriteAllText(probe, "ok", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log2.Write("写入测试失败: " + probe + "  " + ex.GetType().Name + " " + ex.Message);
                return "创建文件失败：\n" + probe + "\n\n错误类型：" + ex.GetType().Name + "\n错误信息：" + ex.Message;
            }
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                return "文件能创建，但删不掉：\n" + probe + "\n\n错误：" + ex.Message + "\n（测试文件需要你手动删一下）";
            }
            Log2.Write("写入测试通过: " + dir);
            return null;
        }

        public static int[] UnionImport(string source, string dest, bool includeSub, bool keepStructure,
            FileKind kind, DateLayout layout, bool moveMode, bool dryRun, CancellationToken ct,
            Action<string, int, int> progress, Action<string> log)
        {
            LastErrors.Clear();
            UnauthorizedCount = 0;
            var srcFiles = Scan(source, includeSub, kind);
            var destFiles = Scan(dest, includeSub, FileKind.All);
            var kindText = kind == FileKind.Images ? "仅照片" : kind == FileKind.Videos ? "仅视频" : "全部文件";
            Log2.Write(string.Format("导入开始：源={0}（{1} 个文件） 目标={2}（已有 {3} 个文件） 含子目录={4} 归档={5} 类型={6} 方式={7} 试运行={8}",
                source, srcFiles.Count, dest, destFiles.Count, includeSub, layout, kindText,
                moveMode ? "移动" : "复制", dryRun));
            if (srcFiles.Count == 0)
                log("源文件夹里没有找到符合条件的文件。请确认路径、勾选「包含子文件夹」，或把「文件类型」改成「全部文件」。");

            var bySize = new Dictionary<long, List<string>>();
            foreach (var f in destFiles)
            {
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                List<string> lst;
                if (!bySize.TryGetValue(len, out lst)) { lst = new List<string>(); bySize[len] = lst; }
                lst.Add(f);
            }
            var hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int skipped = 0, copied = 0, conflicts = 0, failed = 0, i = 0;
            foreach (var src in srcFiles)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                progress(Path.GetFileName(src), i, srcFiles.Count);
                try
                {
                    var info = new FileInfo(src);
                    long size = info.Length;
                    var modifiedUtc = info.LastWriteTimeUtc;
                    var createdUtc = info.CreationTimeUtc;
                    string srcHash = null;

                    List<string> candidates;
                    if (bySize.TryGetValue(size, out candidates))
                    {
                        srcHash = HashFile(src, ct);
                        foreach (var cand in candidates)
                        {
                            string h;
                            if (!hashCache.TryGetValue(cand, out h))
                            {
                                try { h = HashFile(cand, ct); } catch { h = null; }
                                if (h != null) hashCache[cand] = h;
                            }
                            if (h == srcHash)
                            {
                                log("跳过（内容已存在）：" + src + "  ==  " + cand);
                                skipped++;
                                goto NextFile;
                            }
                        }
                    }

                    if (srcHash == null) srcHash = HashFile(src, ct);
                    var rel = RelativeTo(source, src);
                    string sub = layout != DateLayout.None
                        ? DateSubDir(GuessTime(src), layout)
                        : (keepStructure ? (Path.GetDirectoryName(rel) ?? "") : "");
                    var target = Path.Combine(Path.Combine(dest, sub), Path.GetFileName(src));

                    if (File.Exists(target) || Directory.Exists(target) || plannedTargets.Contains(target))
                    {
                        if (srcHash == null) srcHash = HashFile(src, ct);
                        string th;
                        if (!hashCache.TryGetValue(target, out th))
                        {
                            try { th = HashFile(target, ct); } catch { th = null; }
                            if (th != null) hashCache[target] = th;
                        }
                        if (th == srcHash)
                        {
                            log("跳过（内容已存在）：" + src + "  ==  " + target);
                            skipped++;
                            goto NextFile;
                        }
                        target = AvailableConflictPath(
                            Path.Combine(Path.Combine(Path.Combine(dest, ConflictDir), sub), Path.GetFileName(src)),
                            plannedTargets);
                        log("同名不同内容，另存到冲突夹：" + src);
                        conflicts++;
                    }

                    if (!dryRun)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        if (moveMode) MoveWithRetry(src, target, ct);
                        else CopyWithRetry(src, target, ct);
                        try
                        {
                            File.SetLastWriteTimeUtc(target, modifiedUtc);
                            if (!moveMode) File.SetCreationTimeUtc(target, createdUtc);
                        }
                        catch { }
                    }
                    log((dryRun ? (moveMode ? "[试运行] 将移动：" : "[试运行] 将复制：") : (moveMode ? "移动：" : "复制："))
                        + src + "  ->  " + target);

                    if (!bySize.ContainsKey(size)) bySize[size] = new List<string>();
                    bySize[size].Add(target);
                    hashCache[target] = srcHash;
                    plannedTargets.Add(target);
                    copied++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    if (ex is UnauthorizedAccessException) UnauthorizedCount++;
                    var detail = string.Format("失败：{0}\n    {1}: {2}", src, ex.GetType().Name, ex.Message);
                    log(detail);
                    Log2.Write(detail);
                    if (LastErrors.Count < 5) LastErrors.Add(Path.GetFileName(src) + " → " + ex.GetType().Name + ": " + ex.Message);
                }
            NextFile: ;
            }

            log(string.Format("—— 导入结束：跳过 {0}，复制 {1}，冲突 {2}，失败 {3}（源共 {4} 个）{5}",
                skipped, copied, conflicts, failed, srcFiles.Count, dryRun ? "（试运行，未写入）" : ""));
            Log2.Write(string.Format("导入结束：跳过 {0}，复制 {1}，冲突 {2}，失败 {3}（源共 {4} 个）", skipped, copied, conflicts, failed, srcFiles.Count));
            return new[] { skipped, copied, conflicts, failed };
        }

        public static List<DupGroup> ScanDuplicates(string root, bool includeSub, FileKind kind, CancellationToken ct,
            Action<string, int, int> progress, Action<string> log)
        {
            var files = Scan(root, includeSub, kind);
            var bySize = new Dictionary<long, List<string>>();
            foreach (var f in files)
            {
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                List<string> lst;
                if (!bySize.TryGetValue(len, out lst)) { lst = new List<string>(); bySize[len] = lst; }
                lst.Add(f);
            }

            var multi = bySize.Where(kv => kv.Value.Count > 1).ToList();
            int total = multi.Sum(kv => kv.Value.Count);
            var imgCount = files.Count(IsImage);
            var vidCount = files.Count(IsVideo);
            log(string.Format("扫描到 {0} 个文件（照片 {1} · 视频 {2} · 其他 {3}），{4} 种大小，其中 {5} 组大小相同（只有这些要算哈希）。",
                files.Count, imgCount, vidCount, files.Count - imgCount - vidCount, bySize.Count, multi.Count));

            var result = new List<DupGroup>();
            int done = 0;
            foreach (var kv in multi)
            {
                ct.ThrowIfCancellationRequested();
                var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var f in kv.Value)
                {
                    ct.ThrowIfCancellationRequested();
                    done++;
                    progress(Path.GetFileName(f), done, total);
                    try
                    {
                        var h = HashFile(f, ct);
                        List<string> lst;
                        if (!byHash.TryGetValue(h, out lst)) { lst = new List<string>(); byHash[h] = lst; }
                        lst.Add(f);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { log("读不了，跳过：" + f + "  " + ex.Message); }
                }

                foreach (var g in byHash.Where(p => p.Value.Count > 1))
                {
                    var group = g.Value;
                    group.Sort(delegate (string a, string b)
                    {
                        int sa = HasNumericSuffix(a) ? 1 : 0, sb = HasNumericSuffix(b) ? 1 : 0;
                        if (sa != sb) return sa - sb;
                        int da = Depth(a), db = Depth(b);
                        if (da != db) return da - db;
                        DateTime ca, cb;
                        try { ca = File.GetCreationTimeUtc(a); } catch { ca = DateTime.MaxValue; }
                        try { cb = File.GetCreationTimeUtc(b); } catch { cb = DateTime.MaxValue; }
                        if (ca != cb) return ca.CompareTo(cb);
                        return string.CompareOrdinal(a, b);
                    });
                    var dg = new DupGroup { Size = kv.Key, Hash = g.Key, Index = result.Count + 1 };
                    dg.Files.AddRange(group);
                    result.Add(dg);
                }
            }

            long canFree = result.Sum(d => d.Size * (d.Files.Count - 1));
            log(string.Format("—— 扫描完成：{0} 组重复，可清理 {1} 个文件，释放 {2}。",
                result.Count, result.Sum(d => d.Files.Count - 1), FormatSize(canFree)));
            return result;
        }

        /// <summary>复制，遇到占用类错误重试两次；权限类错误直接抛。</summary>
        private static void CopyWithRetry(string src, string target, CancellationToken ct)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { File.Copy(src, target, false); return; }
                catch (UnauthorizedAccessException) { throw; }
                catch (IOException ex)
                {
                    last = ex;
                    if (File.Exists(target) || Directory.Exists(target)) throw;
                    Thread.Sleep(250);
                }
            }
            throw last;
        }

        /// <summary>移动（同分区瞬时）；跨分区时退化成复制再删源。</summary>
        private static void MoveWithRetry(string src, string target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try { File.Move(src, target); return; }
            catch (IOException)
            {
                if (File.Exists(target)) throw;
                CopyWithRetry(src, target, ct);
                try { File.Delete(src); } catch { }
            }
        }

        public static bool Recycle(string path, out string error)
        {
            error = null;
            try
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        }
    }

    /// <summary>列表排序。</summary>
    internal sealed class RowComparer : IComparer
    {
        private readonly int _col;
        private readonly bool _asc;
        public RowComparer(int col, bool asc) { _col = col; _asc = asc; }

        public int Compare(object x, object y)
        {
            var a = (ListViewItem)x;
            var b = (ListViewItem)y;
            int r;
            var ra = a.Tag as DupRow;
            var rb = b.Tag as DupRow;
            if (_col == 0)
                r = (ra == null ? 0 : ra.Group.Index).CompareTo(rb == null ? 0 : rb.Group.Index);
            else if (_col == 3)
                r = (ra == null ? 0 : ra.Group.Size).CompareTo(rb == null ? 0 : rb.Group.Size);
            else
                r = string.Compare(a.SubItems[_col].Text, b.SubItems[_col].Text, StringComparison.CurrentCultureIgnoreCase);
            return _asc ? r : -r;
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color Primary = Color.FromArgb(0, 120, 212);
        private static readonly Color Danger = Color.FromArgb(196, 43, 28);
        private static readonly Color Muted = Color.FromArgb(96, 96, 96);
        private static readonly Color PageBack = Color.FromArgb(250, 250, 250);

        private readonly AppSettings _cfg = AppSettings.Load();

        private readonly TabControl tabs = new TabControl();
        private readonly TabPage tabImport = new TabPage("  ① 导入（并集）  ");
        private readonly TabPage tabDedupe = new TabPage("  ② 去重  ");

        // 导入页
        private readonly TextBox txtSrc = new TextBox();
        private readonly TextBox txtDst = new TextBox();
        private readonly Button btnSrc = new Button();
        private readonly Button btnDst = new Button();
        private readonly CheckBox chkSub = new CheckBox();
        private readonly CheckBox chkKeep = new CheckBox();
        private readonly ComboBox cboLayout = new ComboBox();
        private readonly ComboBox cboKind = new ComboBox();
        private readonly RadioButton rbCopy = new RadioButton();
        private readonly RadioButton rbMove = new RadioButton();
        private readonly CheckBox chkDry = new CheckBox();
        private readonly CheckBox chkAutoScan = new CheckBox();
        private readonly Button btnRun = new Button();
        private readonly Button btnStop = new Button();
        private readonly Button btnOpen1 = new Button();
        private readonly Button btnTest = new Button();
        private readonly ProgressBar prog1 = new ProgressBar();
        private readonly Label lblStat1 = new Label();

        // 去重页
        private readonly TextBox txtDir = new TextBox();
        private readonly Button btnDir = new Button();
        private readonly CheckBox chkSub2 = new CheckBox();
        private readonly ComboBox cboKind2 = new ComboBox();
        private readonly Button btnScan = new Button();
        private readonly Button btnDelete = new Button();
        private readonly Button btnAll = new Button();
        private readonly Button btnNone = new Button();
        private readonly Button btnOpen2 = new Button();
        private readonly Label lblStat2 = new Label();
        private readonly ListView list = new ListView();

        // 底部
        private readonly TextBox txtLog = new TextBox();
        private readonly StatusStrip status = new StatusStrip();
        private readonly ToolStripStatusLabel statusLeft = new ToolStripStatusLabel("就绪");
        private readonly ToolStripStatusLabel statusRight = new ToolStripStatusLabel("");

        private readonly List<DupGroup> groups = new List<DupGroup>();
        private CancellationTokenSource cts;
        private bool running;
        private int _sortCol = 0;
        private bool _sortAsc = true;

        public MainForm()
        {
            Text = "照片管家";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1060, 800);
            MinimumSize = new Size(900, 660);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PageBack;

            BuildTabs();
            BuildLog();
            BuildStatusBar();

            LoadSettingsIntoUi();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutImport();
            LayoutDedupe();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveUiIntoSettings();
            base.OnFormClosing(e);
        }

        // ── 底部状态栏 ──────────────────────────────────────────────────────
        private void BuildStatusBar()
        {
            status.Items.Add(statusLeft);
            statusLeft.Spring = true;
            statusLeft.TextAlign = ContentAlignment.MiddleLeft;

            var openLog = new ToolStripStatusLabel("查看日志");
            openLog.IsLink = true;
            openLog.Margin = new Padding(6, 3, 10, 2);
            openLog.Click += delegate { Log2.Open(); };
            status.Items.Add(openLog);

            status.Items.Add(statusRight);
            statusRight.ForeColor = Muted;
            status.SizingGrip = false;
            Controls.Add(status);
        }

        private void BuildLog()
        {
            var host = new Panel { Dock = DockStyle.Bottom, Height = 156, BackColor = PageBack, Padding = new Padding(12, 0, 12, 6) };
            var box = new GroupBox { Text = "日志", Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 8) };
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.White;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.Font = new Font("Consolas", 9F);
            box.Controls.Add(txtLog);
            host.Controls.Add(box);
            Controls.Add(host);
        }

        // ── 标签页 ──────────────────────────────────────────────────────────
        private void BuildTabs()
        {
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 5);
            tabs.Font = new Font("Microsoft YaHei UI", 9F);
            tabs.TabPages.Add(tabImport);
            tabs.TabPages.Add(tabDedupe);
            Controls.Add(tabs);

            tabImport.BackColor = PageBack;
            tabDedupe.BackColor = PageBack;

            BuildImport();
            BuildDedupe();

            tabImport.Resize += delegate { LayoutImport(); };
            tabDedupe.Resize += delegate { LayoutDedupe(); };
        }

        private GroupBox Group(string text, int x, int y, int w, int h)
        {
            return new GroupBox
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = PageBack,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
        }

        private static Label MkLabel(string text, int x, int y)
        {
            return new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Color.FromArgb(40, 40, 40) };
        }

        private Button PrimaryButton(string text, int x, int y, int w, int h)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = Primary,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private Button PlainButton(string text, int x, int y, int w, int h)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.System
            };
            return b;
        }

        // ── 导入页 ──────────────────────────────────────────────────────────
        private readonly GroupBox grpFiles = new GroupBox();
        private readonly GroupBox grpOpts = new GroupBox();

        private void BuildImport()
        {
            grpFiles.Text = "文件夹";
            grpFiles.Location = new Point(14, 12);
            grpFiles.BackColor = PageBack;

            var l1 = MkLabel("源文件夹", 14, 30);
            txtSrc.Location = new Point(104, 26);
            StyleBox(txtSrc);
            btnSrc.Text = "浏览…";
            btnSrc.Size = new Size(86, 27);
            btnSrc.FlatStyle = FlatStyle.System;
            btnSrc.Click += delegate { Browse(txtSrc); };

            var l2 = MkLabel("目标文件夹", 14, 66);
            txtDst.Location = new Point(104, 62);
            StyleBox(txtDst);
            btnDst.Text = "浏览…";
            btnDst.Size = new Size(86, 27);
            btnDst.FlatStyle = FlatStyle.System;
            btnDst.Click += delegate { Browse(txtDst); };

            grpFiles.Controls.AddRange(new Control[] { l1, txtSrc, btnSrc, l2, txtDst, btnDst });
            grpFiles.Height = 108;

            grpOpts.Text = "选项";
            grpOpts.Location = new Point(14, 132);
            grpOpts.Height = 138;
            grpOpts.BackColor = PageBack;

            chkSub.Text = "包含子文件夹";
            chkSub.Location = new Point(16, 26);
            chkSub.AutoSize = true;

            chkKeep.Text = "保持源的目录结构";
            chkKeep.Location = new Point(150, 26);
            chkKeep.AutoSize = true;

            chkDry.Text = "试运行（只报告，不写入文件）";
            chkDry.Location = new Point(330, 26);
            chkDry.AutoSize = true;

            var l3 = MkLabel("按拍摄日期归档", 16, 58);
            cboLayout.DropDownStyle = ComboBoxStyle.DropDownList;
            cboLayout.Location = new Point(140, 54);
            cboLayout.Width = 160;
            cboLayout.Items.AddRange(new object[] { "不归档（保持原样）", "年 \\ 年-月", "年" });
            cboLayout.SelectedIndex = 0;

            var l4 = MkLabel("文件类型", 330, 58);
            cboKind.DropDownStyle = ComboBoxStyle.DropDownList;
            cboKind.Location = new Point(404, 54);
            cboKind.Width = 150;
            cboKind.Items.AddRange(new object[] { "全部文件", "仅照片", "仅视频" });
            cboKind.SelectedIndex = 0;

            var l5 = MkLabel("操作方式", 16, 90);
            rbCopy.Text = "复制（保留源文件）";
            rbCopy.Location = new Point(90, 88);
            rbCopy.AutoSize = true;
            rbCopy.Checked = true;
            rbMove.Text = "移动到目标";
            rbMove.Location = new Point(240, 88);
            rbMove.AutoSize = true;

            chkAutoScan.Text = "导入完成后自动扫描重复";
            chkAutoScan.Location = new Point(360, 88);
            chkAutoScan.AutoSize = true;

            grpOpts.Controls.AddRange(new Control[] { chkSub, chkKeep, chkDry, l3, cboLayout, l4, cboKind, l5, rbCopy, rbMove, chkAutoScan });

            btnRun.Text = "开始导入";
            btnRun.Location = new Point(14, 282);
            btnRun.Size = new Size(132, 38);
            btnRun.FlatStyle = FlatStyle.Flat;
            btnRun.BackColor = Primary;
            btnRun.ForeColor = Color.White;
            btnRun.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            btnRun.FlatAppearance.BorderSize = 0;
            btnRun.Click += ImportClick;

            btnStop.Text = "停止";
            btnStop.Location = new Point(156, 282);
            btnStop.Size = new Size(96, 38);
            btnStop.FlatStyle = FlatStyle.System;
            btnStop.Enabled = false;
            btnStop.Click += delegate { if (cts != null) cts.Cancel(); };

            btnOpen1.Text = "打开目标文件夹";
            btnOpen1.Location = new Point(262, 282);
            btnOpen1.Size = new Size(140, 38);
            btnOpen1.FlatStyle = FlatStyle.System;
            btnOpen1.Click += delegate { OpenFolder(txtDst.Text.Trim()); };

            btnTest.Text = "测试写入";
            btnTest.Location = new Point(410, 282);
            btnTest.Size = new Size(120, 38);
            btnTest.FlatStyle = FlatStyle.System;
            btnTest.Click += delegate
            {
                var err = Ops.TestWrite(txtDst.Text.Trim());
                if (err == null)
                    MessageBox.Show(this, "目标文件夹可以正常写入。\n\n如果导入仍然失败，问题不在权限上，请按「查看日志」把内容发我。",
                        "测试写入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, err, "写入被拒绝", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            prog1.Location = new Point(14, 334);
            prog1.Size = new Size(1000, 20);
            prog1.Style = ProgressBarStyle.Continuous;

            lblStat1.Text = "选好两个文件夹，点「开始导入」。第一次建议先勾上「试运行」。";
            lblStat1.Location = new Point(14, 362);
            lblStat1.AutoSize = true;
            lblStat1.ForeColor = Muted;

            tabImport.Controls.AddRange(new Control[] { grpFiles, grpOpts, btnRun, btnStop, btnOpen1, btnTest, prog1, lblStat1 });
        }

        private void StyleBox(TextBox b)
        {
            b.BorderStyle = BorderStyle.FixedSingle;
            b.AllowDrop = true;
            b.DragEnter += delegate (object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            b.DragDrop += delegate (object s, DragEventArgs e)
            {
                var items = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (items != null && items.Length > 0 && Directory.Exists(items[0])) b.Text = items[0];
            };
        }

        private void LayoutImport()
        {
            var w = tabImport.ClientSize.Width;
            var inner = w - 28;
            grpFiles.Width = inner;
            grpOpts.Width = inner;

            var boxW = grpFiles.Width - 104 - 14 - 86 - 12;
            txtSrc.Width = Math.Max(180, boxW);
            txtDst.Width = txtSrc.Width;
            btnSrc.Left = grpFiles.Width - 86 - 12;
            btnDst.Left = btnSrc.Left;
            btnSrc.Top = txtSrc.Top - 1;
            btnDst.Top = txtDst.Top - 1;
            prog1.Width = Math.Max(240, inner);
        }

        // ── 去重页 ──────────────────────────────────────────────────────────
        private readonly GroupBox grpScope = new GroupBox();

        private void BuildDedupe()
        {
            grpScope.Text = "扫描范围";
            grpScope.Location = new Point(14, 12);
            grpScope.Height = 96;
            grpScope.BackColor = PageBack;

            var l = MkLabel("文件夹", 14, 30);
            txtDir.Location = new Point(104, 26);
            StyleBox(txtDir);
            btnDir.Text = "浏览…";
            btnDir.Size = new Size(86, 27);
            btnDir.FlatStyle = FlatStyle.System;
            btnDir.Click += delegate { Browse(txtDir); };

            chkSub2.Text = "包含子文件夹";
            chkSub2.Location = new Point(16, 62);
            chkSub2.AutoSize = true;

            var lk = MkLabel("文件类型", 140, 64);
            cboKind2.DropDownStyle = ComboBoxStyle.DropDownList;
            cboKind2.Location = new Point(214, 60);
            cboKind2.Width = 150;
            cboKind2.Items.AddRange(new object[] { "全部文件", "仅照片", "仅视频" });
            cboKind2.SelectedIndex = 0;

            grpScope.Controls.AddRange(new Control[] { l, txtDir, btnDir, chkSub2, lk, cboKind2 });

            btnScan.Text = "扫描重复";
            btnScan.Location = new Point(14, 120);
            btnScan.Size = new Size(132, 38);
            btnScan.FlatStyle = FlatStyle.Flat;
            btnScan.BackColor = Primary;
            btnScan.ForeColor = Color.White;
            btnScan.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            btnScan.FlatAppearance.BorderSize = 0;
            btnScan.Click += delegate { ScanClick(); };

            btnAll.Text = "全选重复";
            btnAll.Location = new Point(156, 120);
            btnAll.Size = new Size(96, 38);
            btnAll.FlatStyle = FlatStyle.System;
            btnAll.Click += delegate { SetAllChecked(true); };

            btnNone.Text = "全不选";
            btnNone.Location = new Point(258, 120);
            btnNone.Size = new Size(86, 38);
            btnNone.FlatStyle = FlatStyle.System;
            btnNone.Click += delegate { SetAllChecked(false); };

            btnOpen2.Text = "打开文件夹";
            btnOpen2.Location = new Point(350, 120);
            btnOpen2.Size = new Size(120, 38);
            btnOpen2.FlatStyle = FlatStyle.System;
            btnOpen2.Click += delegate { OpenFolder(txtDir.Text.Trim()); };

            btnDelete.Text = "删除选中（到回收站）";
            btnDelete.Location = new Point(760, 120);
            btnDelete.Size = new Size(230, 38);
            btnDelete.FlatStyle = FlatStyle.Flat;
            btnDelete.BackColor = Danger;
            btnDelete.ForeColor = Color.White;
            btnDelete.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            btnDelete.FlatAppearance.BorderSize = 0;
            btnDelete.Enabled = false;
            btnDelete.Click += DeleteClick;

            lblStat2.Text = "还没有扫描。选个文件夹，点「扫描重复」。";
            lblStat2.Location = new Point(14, 170);
            lblStat2.AutoSize = true;
            lblStat2.ForeColor = Muted;

            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.GridLines = false;
            list.Location = new Point(14, 196);
            list.BorderStyle = BorderStyle.FixedSingle;
            list.Columns.Add("组", 50);
            list.Columns.Add("操作", 62);
            list.Columns.Add("文件名", 260);
            list.Columns.Add("大小", 86);
            list.Columns.Add("所在文件夹", 520);
            list.ColumnClick += ColumnClick;
            list.DoubleClick += delegate { RevealSelected(); };

            tabDedupe.Controls.AddRange(new Control[] { grpScope, btnScan, btnAll, btnNone, btnOpen2, btnDelete, lblStat2, list });
        }

        private void LayoutDedupe()
        {
            var w = tabDedupe.ClientSize.Width;
            var h = tabDedupe.ClientSize.Height;
            var inner = w - 28;
            grpScope.Width = inner;

            var boxW = grpScope.Width - 104 - 14 - 86 - 12;
            txtDir.Width = Math.Max(180, boxW);
            btnDir.Left = grpScope.Width - 86 - 12;
            btnDir.Top = txtDir.Top - 1;

            btnDelete.Left = Math.Max(370, inner - 230);
            list.Width = inner;
            list.Height = Math.Max(140, h - 196 - 12);
        }

        // ── 公共行为 ────────────────────────────────────────────────────────
        private void OpenSecuritySettings()
        {
            string[] uris = new string[] { "windowsdefender://ransomwareprotection", "windowsdefender://", "ms-settings:windowsdefender" };
            foreach (var u in uris)
            {
                try { System.Diagnostics.Process.Start(u); return; } catch { }
            }
            MessageBox.Show(this, "没能自动打开设置。请手动打开：\nWindows 安全中心 → 病毒和威胁防护 → 管理勒索软件防护 → 受控文件夹访问",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Browse(TextBox box)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.ShowNewFolderButton = true;
                dlg.Description = "选择文件夹";
                if (Directory.Exists(box.Text.Trim())) dlg.SelectedPath = box.Text.Trim();
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
            }
        }

        private void OpenFolder(string dir)
        {
            if (!Directory.Exists(dir)) { MessageBox.Show(this, "文件夹不存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        private void RevealSelected()
        {
            if (list.SelectedItems.Count != 1) return;
            var row = list.SelectedItems[0].Tag as DupRow;
            if (row == null || !File.Exists(row.Path)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + row.Path + "\""); }
            catch { }
        }

        private void ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == _sortCol) _sortAsc = !_sortAsc;
            else { _sortCol = e.Column; _sortAsc = true; }
            list.ListViewItemSorter = new RowComparer(_sortCol, _sortAsc);
            list.Sort();
        }

        private void Log(string line)
        {
            if (IsDisposed) return;
            if (txtLog.InvokeRequired) { try { txtLog.BeginInvoke(new Action<string>(Log), line); } catch { } return; }
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\r\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        private void SetStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(SetStatus), text); } catch { } return; }
            statusLeft.Text = text;
        }

        private void SetBusy(bool busy)
        {
            running = busy;
            btnRun.Enabled = !busy;
            btnScan.Enabled = !busy;
            btnAll.Enabled = !busy;
            btnNone.Enabled = !busy;
            btnDelete.Enabled = !busy && list.Items.Count > 0;
            btnStop.Enabled = busy;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }

        private CancellationToken BeginWork()
        {
            cts = new CancellationTokenSource();
            SetBusy(true);
            return cts.Token;
        }

        // ── 导入 ────────────────────────────────────────────────────────────
        private void ImportClick(object sender, EventArgs e)
        {
            if (running) return;
            var src = txtSrc.Text.Trim();
            var dst = txtDst.Text.Trim();
            if (src.Length == 0) { MessageBox.Show(this, "请先选「源文件夹」。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (dst.Length == 0) { MessageBox.Show(this, "请先选「目标文件夹」。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!Directory.Exists(src)) { MessageBox.Show(this, "源文件夹不存在：\n" + src, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!Directory.Exists(dst) && !chkDry.Checked)
            {
                if (MessageBox.Show(this, "目标文件夹还不存在：\n" + dst + "\n\n要现在创建吗？", "创建文件夹",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                try { Directory.CreateDirectory(dst); }
                catch (Exception ex) { MessageBox.Show(this, "创建失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            }
            if (string.Equals(Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar),
                              Path.GetFullPath(dst).TrimEnd(Path.DirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "源和目标是同一个文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            if (chkDry.Checked &&
                MessageBox.Show(this, "当前是试运行：只输出报告，不复制任何文件。继续？", "试运行",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            var moveMode = rbMove.Checked;
            if (moveMode && !chkDry.Checked &&
                MessageBox.Show(this, "「移动」会把源文件搬到目标文件夹，源目录里就没有了。\n\n确定用移动方式吗？",
                    "确认移动", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            var ct = BeginWork();
            txtLog.Clear();
            prog1.Value = 0;
            var includeSub = chkSub.Checked;
            var keep = chkKeep.Checked;
            var dry = chkDry.Checked;
            var layout = cboLayout.SelectedIndex == 1 ? DateLayout.YearMonth
                       : cboLayout.SelectedIndex == 2 ? DateLayout.Year : DateLayout.None;
            var kind = cboKind.SelectedIndex == 1 ? FileKind.Images
                     : cboKind.SelectedIndex == 2 ? FileKind.Videos : FileKind.All;
            int[] result = null;
            string fatal = null;

            SetStatus("正在导入…");
            Log("=== 导入 ===");
            Log("源：" + src);
            Log("目标：" + dst);
            Log("选项：子文件夹=" + (includeSub ? "是" : "否")
                + "，保持结构=" + (keep ? "是" : "否")
                + "，归档=" + cboLayout.SelectedItem
                + "，类型=" + cboKind.SelectedItem
                + "，方式=" + (moveMode ? "移动" : "复制")
                + "，试运行=" + (dry ? "是" : "否"));

            Task.Run(delegate
            {
                try { result = Ops.UnionImport(src, dst, includeSub, keep, kind, layout, moveMode, dry, ct, Progress1, Log); }
                catch (OperationCanceledException) { Log("已停止。"); fatal = "已停止"; }
                catch (Exception ex) { Log("出错：" + ex); Log2.Write("导入异常: " + ex); fatal = ex.Message; }
                finally
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            SetBusy(false);
                            lblStat1.Text = "完成。";
                            SetStatus("导入完成");

                            if (fatal != null && result == null)
                            {
                                MessageBox.Show(this, "导入没有完成：\n" + fatal + "\n\n日志：" + Log2.FilePath,
                                    "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                return;
                            }
                            if (result != null)
                            {
                                var msg = string.Format("导入完成\n\n{0}：{1} 个\n跳过（内容已存在）：{2} 个\n同名冲突：{3} 个\n失败：{4} 个",
                                    moveMode ? "移动" : "复制", result[1], result[0], result[2], result[3]);
                                if (dry) msg += "\n\n（这是试运行，没有写入任何文件）";
                                if (Ops.LastErrors.Count > 0)
                                {
                                    msg += "\n\n失败详情：";
                                    for (int k = 0; k < Ops.LastErrors.Count; k++)
                                        msg += "\n· " + Ops.LastErrors[k];
                                }
                                if (Ops.UnauthorizedCount > 0)
                                {
                                    msg += "\n\n⚠ 有 " + Ops.UnauthorizedCount + " 个文件被 Windows 拒绝写入。\n\n"
                                         + "最常见的原因：「受控文件夹访问」（勒索软件防护）把「图片 / 文档 / 桌面」等目录保护起来，\n"
                                         + "只允许它信任的程序写入。请在 Windows 安全中心核对是否有本程序被拦截的记录。\n\n"
                                         + "解决办法（任选其一）：\n"
                                         + "1. 把本程序加入白名单（推荐）\n"
                                         + "2. 关闭「受控文件夹访问」\n\n"
                                         + "要现在打开 Windows 安全中心的设置页吗？";
                                    if (MessageBox.Show(this, msg, "写入被系统拦截", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                                        OpenSecuritySettings();
                                }
                                else
                                {
                                    MessageBox.Show(this, msg, "导入结果", MessageBoxButtons.OK,
                                        result[3] > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                                }
                            }

                            if (chkAutoScan.Checked && !dry)
                            {
                                tabs.SelectedTab = tabDedupe;
                                txtDir.Text = dst;
                                ScanClick();
                            }
                        }));
                    }
                    catch { }
                }
            });
        }

        private void Progress1(string current, int done, int total)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, int, int>(Progress1), current, done, total); } catch { }
                return;
            }
            prog1.Maximum = Math.Max(1, total);
            prog1.Value = Math.Min(total, done);
            lblStat1.Text = string.Format("{0} / {1}   {2}", done, total, current);
            statusRight.Text = string.Format("{0} / {1}", done, total);
        }

        // ── 扫描 ────────────────────────────────────────────────────────────
        private void ScanClick()
        {
            if (running) return;
            var dir = txtDir.Text.Trim();
            if (!Directory.Exists(dir)) { MessageBox.Show(this, "请选择存在的文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            var ct = BeginWork();
            groups.Clear();
            list.Items.Clear();
            btnDelete.Enabled = false;
            lblStat2.Text = "扫描中…";
            SetStatus("正在扫描重复…");
            Log("=== 扫描重复：" + dir + " ===");

            var sub = chkSub2.Checked;
            var kind2 = cboKind2.SelectedIndex == 1 ? FileKind.Images
                      : cboKind2.SelectedIndex == 2 ? FileKind.Videos : FileKind.All;
            Task.Run(delegate
            {
                try
                {
                    var found = Ops.ScanDuplicates(dir, sub, kind2, ct, Progress2, Log);
                    BeginInvoke(new Action(delegate
                    {
                        FillList(found);
                        SetBusy(false);
                        SetStatus("扫描完成");
                    }));
                }
                catch (OperationCanceledException)
                {
                    Log("已停止。");
                    try { BeginInvoke(new Action(delegate { SetBusy(false); SetStatus("已停止"); })); } catch { }
                }
                catch (Exception ex)
                {
                    Log("出错：" + ex.Message);
                    try { BeginInvoke(new Action(delegate { SetBusy(false); SetStatus("出错"); })); } catch { }
                }
            });
        }

        private void Progress2(string current, int done, int total)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, int, int>(Progress2), current, done, total); } catch { }
                return;
            }
            lblStat2.Text = string.Format("扫描中 {0} / {1}   {2}", done, total, current);
            statusRight.Text = string.Format("{0} / {1}", done, total);
        }

        private void FillList(List<DupGroup> found)
        {
            groups.Clear();
            groups.AddRange(found);
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var g in groups)
            {
                for (int k = 0; k < g.Files.Count; k++)
                {
                    var f = g.Files[k];
                    var it = new ListViewItem(g.Index.ToString());
                    it.SubItems.Add(k == 0 ? "保留" : "删除");
                    it.SubItems.Add(Path.GetFileName(f));
                    it.SubItems.Add(Ops.FormatSize(g.Size));
                    it.SubItems.Add(Path.GetDirectoryName(f));
                    it.Tag = new DupRow { Group = g, Path = f, IsKeeper = k == 0 };
                    it.Checked = k > 0;
                    if (k == 0) it.ForeColor = Muted;
                    else if (g.Index % 2 == 0) it.BackColor = Color.FromArgb(246, 249, 253);
                    list.Items.Add(it);
                }
            }
            list.EndUpdate();

            var dupCount = groups.Sum(g => g.Files.Count - 1);
            var free = groups.Sum(g => g.Size * (g.Files.Count - 1));
            if (groups.Count == 0)
            {
                lblStat2.Text = "没有发现重复文件。";
                lblStat2.ForeColor = Color.FromArgb(16, 124, 16);
            }
            else
            {
                lblStat2.Text = string.Format("{0} 组重复，{1} 个可删，可释放 {2}。双击某行可在资源管理器里定位。",
                    groups.Count, dupCount, Ops.FormatSize(free));
                lblStat2.ForeColor = Muted;
            }
            btnDelete.Enabled = groups.Count > 0;
            Log2.Write("扫描完成：" + groups.Count + " 组重复，共 " + list.Items.Count + " 行");

            if (groups.Count == 0)
            {
                MessageBox.Show(this,
                    "没有发现重复文件。\n\n可能的几个原因：\n"
                    + "· 这个文件夹里确实没有内容完全相同的文件\n"
                    + "· 重复的两个文件不在同一个文件夹里——把范围选到它们的共同上级，并勾选「包含子文件夹」\n"
                    + "· 同一张图被重新压缩过（字节不同），本程序按字节判重，不会把这种当重复",
                    "扫描结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void SetAllChecked(bool on)
        {
            foreach (ListViewItem it in list.Items)
            {
                var row = it.Tag as DupRow;
                if (row == null) continue;
                it.Checked = on && !row.IsKeeper;
            }
            list.Refresh();
        }

        private void DeleteClick(object sender, EventArgs e)
        {
            if (running) return;

            var checkedRows = new List<ListViewItem>();
            foreach (ListViewItem it in list.CheckedItems) checkedRows.Add(it);
            if (checkedRows.Count == 0) { MessageBox.Show(this, "没有勾选要删除的文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            var touched = new HashSet<DupGroup>();
            foreach (var it in checkedRows) touched.Add(((DupRow)it.Tag).Group);
            foreach (var g in touched)
            {
                bool kept = false;
                foreach (ListViewItem it in list.Items)
                {
                    var row = (DupRow)it.Tag;
                    if (ReferenceEquals(row.Group, g) && !it.Checked) { kept = true; break; }
                }
                if (!kept)
                {
                    foreach (ListViewItem it in list.Items)
                    {
                        var row = (DupRow)it.Tag;
                        if (ReferenceEquals(row.Group, g) && row.IsKeeper)
                        {
                            it.Checked = false;
                            Log("这组被全勾了，自动保留一份：" + row.Path);
                            break;
                        }
                    }
                }
            }

            checkedRows.Clear();
            foreach (ListViewItem it in list.CheckedItems) checkedRows.Add(it);
            if (checkedRows.Count == 0) { MessageBox.Show(this, "没有可删除的文件（每组至少留一份）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            long size = 0;
            foreach (var it in checkedRows) size += ((DupRow)it.Tag).Group.Size;
            if (MessageBox.Show(this, string.Format("把 {0} 个文件（{1}）删除到回收站？\n\n每组都保留一份，删掉的随时可以从回收站还原。",
                    checkedRows.Count, Ops.FormatSize(size)), "确认删除",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            int ok = 0, fail = 0;
            foreach (var it in checkedRows)
            {
                var row = (DupRow)it.Tag;
                string err;
                if (Ops.Recycle(row.Path, out err))
                {
                    ok++;
                    row.Group.Files.Remove(row.Path);
                    list.Items.Remove(it);
                }
                else { fail++; Log("删除失败：" + row.Path + "  " + (err ?? "未知原因")); }
            }

            Log(string.Format("—— 删除完成：成功 {0}，失败 {1}", ok, fail));
            int leftGroups = groups.Count(g => g.Files.Count > 1);
            int leftFiles = groups.Sum(g => Math.Max(0, g.Files.Count - 1));
            if (leftGroups == 0) lblStat2.Text = "重复已经清理干净。";
            else lblStat2.Text = string.Format("剩余 {0} 组重复，{1} 个可删。", leftGroups, leftFiles);
            btnDelete.Enabled = list.CheckedItems.Count > 0;
            SetStatus(string.Format("删除完成：{0} 个", ok));
        }

        // ── 配置读写 ────────────────────────────────────────────────────────
        private void LoadSettingsIntoUi()
        {
            txtSrc.Text = _cfg.Source;
            txtDst.Text = _cfg.Dest;
            txtDir.Text = string.IsNullOrEmpty(_cfg.DedupeDir) ? _cfg.Dest : _cfg.DedupeDir;
            chkSub.Checked = _cfg.IncludeSub;
            chkKeep.Checked = _cfg.KeepStructure;
            chkDry.Checked = _cfg.DryRun;
            chkAutoScan.Checked = _cfg.AutoScan;
            chkSub2.Checked = _cfg.DedupeSub;
            cboLayout.SelectedIndex = Math.Max(0, Math.Min(2, _cfg.LayoutIndex));
            cboKind.SelectedIndex = Math.Max(0, Math.Min(2, _cfg.KindIndex));
            rbMove.Checked = _cfg.MoveMode;
            rbCopy.Checked = !_cfg.MoveMode;
            statusRight.Text = "v1.0.0";
        }

        private void SaveUiIntoSettings()
        {
            _cfg.Source = txtSrc.Text.Trim();
            _cfg.Dest = txtDst.Text.Trim();
            _cfg.DedupeDir = txtDir.Text.Trim();
            _cfg.IncludeSub = chkSub.Checked;
            _cfg.KeepStructure = chkKeep.Checked;
            _cfg.DryRun = chkDry.Checked;
            _cfg.AutoScan = chkAutoScan.Checked;
            _cfg.DedupeSub = chkSub2.Checked;
            _cfg.LayoutIndex = cboLayout.SelectedIndex;
            _cfg.KindIndex = cboKind.SelectedIndex;
            _cfg.MoveMode = rbMove.Checked;
            _cfg.Save();
        }
    }
}
