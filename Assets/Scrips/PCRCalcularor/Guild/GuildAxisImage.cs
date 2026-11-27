#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PCRCaculator.Guild
{
    internal static class GuildAxisImage
    {
        internal static Task<string> RecognizeFileAsync(string path, CancellationToken token)
        {
            var file = Path.GetFullPath(path);
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") throw new FormatException("请选择 PNG 或 JPEG 图片。");
            if (!File.Exists(file)) throw new FileNotFoundException("找不到所选图片。", file);
            return ReadAsync(file, token);
        }

        internal static Task<string> RecognizeClipboardAsync(CancellationToken token) => ReadAsync(null, token);

        private static Task<string> ReadAsync(string file, CancellationToken token)
        {
            string assets = Application.streamingAssetsPath;
            string python = Environment.GetEnvironmentVariable("PCR_AXIS_PYTHON");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(python))
            {
                python = Path.Combine(local, "PCRCalculator", "AxisOCR", "Scripts", "python.exe");
                var baseDir = Path.Combine(local, "Programs", "Python");
                if (!File.Exists(python) && Directory.Exists(baseDir))
                    foreach (var dir in Directory.EnumerateDirectories(baseDir, "Python3*"))
                        if (Directory.Exists(Path.Combine(dir, "Lib", "site-packages", "rapidocr_onnxruntime-1.4.4.dist-info")) && File.Exists(Path.Combine(dir, "python.exe"))) { python = Path.Combine(dir, "python.exe"); break; }
            }
            bool neural = File.Exists(python);
            string helper = Path.Combine(assets, neural ? "GuildAxisRead.py" : "GuildAxisOcr.ps1");
            return Task.Run(async () =>
            {
                string executable = neural ? python : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                string args = neural ? "-I -B -X utf8 " : "-NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File ";
                var options = new ProcessStartInfo(executable, args + "\"" + helper + "\"") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                options.EnvironmentVariables["GUILD_AXIS_IMAGE"] = file ?? "";
                using (var process = Process.Start(options))
                {
                    var text = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    try
                    {
                        var elapsed = Stopwatch.StartNew();
                        while (!process.WaitForExit(150))
                        {
                            token.ThrowIfCancellationRequested();
                            if (elapsed.Elapsed.TotalSeconds > 90) throw new TimeoutException("识别超时，可裁剪图片或粘贴文字。");
                        }
                        token.ThrowIfCancellationRequested();
                        var message = (await error).Trim(); var result = (await text).Trim();
                        if (process.ExitCode != 0 || message.Length != 0) throw new InvalidDataException("图片识别失败：" + message);
                        if (result.Length == 0) throw new InvalidDataException("图片中未识别到文字。");
                        return result;
                    }
                    finally { if (!process.HasExited) process.Kill(); }
                }
            }, token);
        }
    }
}
#endif
