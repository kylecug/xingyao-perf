using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace XingyaoPerf
{
    // Manual update from GitHub Releases. Nothing here runs unless the user picks "检查更新" in the menu.
    // A release must carry xingyao-perf.exe and xingyao-perf.exe.sha256 (the hex digest as text).
    internal static class Updater
    {
        const string Repo = "kylecug/xingyao-perf";
        const string ExeName = "xingyao-perf.exe";
        static readonly string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

        public sealed class Release { public Version Version; public string Tag, ExeUrl, ShaUrl, Notes; }

        // The release notes are a JSON string; undo its escapes.
        static string Unescape(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') { }
                else if (n == 't') sb.Append('\t');
                else if (n == 'u' && i + 4 < s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                else sb.Append(n); // \" \\ \/
            }
            return sb.ToString().Trim();
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2; the .NET 4.0 default is older
            var c = new WebClient();
            c.Headers[HttpRequestHeader.UserAgent] = "xingyao-perf-updater"; // GitHub's API requires some user agent
            return c;
        }

        public static Version Current { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

        // Returns the newest release, or null if there is none. Throws on network errors.
        public static Release Latest()
        {
            string json;
            using (var c = Client()) json = c.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tag.Success) return null;
            var r = new Release { Tag = tag.Groups[1].Value };
            if (!Version.TryParse(r.Tag.TrimStart('v', 'V'), out r.Version)) return null;
            var body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (body.Success) r.Notes = Unescape(body.Groups[1].Value);
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string url = m.Groups[1].Value;
                if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                if (url.EndsWith("/" + ExeName, StringComparison.Ordinal)) r.ExeUrl = url;
                else if (url.EndsWith("/" + ExeName + ".sha256", StringComparison.Ordinal)) r.ShaUrl = url;
            }
            return r.ExeUrl != null && r.ShaUrl != null ? r : null;
        }

        public static bool IsNewer(Release r)
        {
            var cur = Current;
            // Assembly versions have four parts (0.9.29.0); tags have three.
            var have = new Version(cur.Major, cur.Minor, Math.Max(cur.Build, 0));
            var got = new Version(r.Version.Major, r.Version.Minor, Math.Max(r.Version.Build, 0));
            return got > have;
        }

        // Downloads and checks the new exe, swaps it in (the running exe can be renamed, not overwritten),
        // and returns the path of the exe to start. Throws if anything does not check out; the old exe is then untouched.
        public static string Install(Release r)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string dir = Path.GetDirectoryName(exe);
            foreach (var old in Directory.GetFiles(dir, "*.old")) { try { File.Delete(old); } catch { } }
            string tmp = exe + ".download";
            try
            {
                string expected;
                using (var c = Client())
                {
                    expected = Regex.Match(c.DownloadString(r.ShaUrl), "[0-9a-fA-F]{64}").Value.ToLowerInvariant();
                    c.DownloadFile(r.ExeUrl, tmp);
                }
                if (expected.Length != 64) throw new InvalidDataException("没有找到校验值");
                string actual;
                using (var sha = SHA256.Create())
                using (var f = File.OpenRead(tmp)) actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new InvalidDataException("下载的文件校验不通过");
                var fv = FileVersionInfo.GetVersionInfo(tmp);
                if (fv.FileMajorPart != r.Version.Major || fv.FileMinorPart != r.Version.Minor || fv.FileBuildPart != Math.Max(r.Version.Build, 0))
                    throw new InvalidDataException("下载的文件版本与发布不符：" + fv.FileVersion);
                string backup = exe + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".old";
                File.Move(exe, backup);
                try { File.Move(tmp, exe); }
                catch { File.Move(backup, exe); throw; }
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            return exe;
        }

        // Starts the new exe once this process has exited (it holds the single-instance mutex until then).
        public static void RestartLater(string exe)
        {
            var i = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & start \"\" \"" + exe + "\"") { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(i);
        }
    }
}
