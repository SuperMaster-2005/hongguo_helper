using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HongguoRemote
{
    internal sealed class DiagnosticLog
    {
        internal static readonly DiagnosticLog Current = new DiagnosticLog(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "logs")));
        internal const int MaximumBytes = 1024 * 1024;
        readonly object sync = new object();
        readonly string directory;
        readonly StringBuilder memory = new StringBuilder();
        internal string StorageError { get; private set; }
        internal string DirectoryPath { get { return directory; } }
        internal DiagnosticLog(string path) { directory = path; }
        internal static string Sanitize(string message)
        {
            string text = message ?? "";
            text = Regex.Replace(text, @"(?is)text\|.*", "text|[输入内容已隐藏]");
            text = Regex.Replace(text, @"(?<!\d)\d{6}(?!\d)", "[六位数字已隐藏]");
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!String.IsNullOrEmpty(profile)) text = Regex.Replace(text, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
            return text.Length > 16000 ? text.Substring(0, 16000) + " [内容已截断]" : text;
        }
        internal void Write(string level, string message)
        {
            lock (sync)
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + Sanitize(message) + Environment.NewLine;
                memory.Append(line);
                if (memory.Length > 64000) memory.Remove(0, memory.Length - 48000);
                try
                {
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "current.log");
                    if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
                    {
                        string oldest = Path.Combine(directory, "previous-2.log");
                        if (File.Exists(oldest)) File.Delete(oldest);
                        string previous = Path.Combine(directory, "previous-1.log");
                        if (File.Exists(previous)) File.Move(previous, oldest);
                        File.Move(path, previous);
                    }
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                    StorageError = null;
                }
                catch (Exception error) { StorageError = Sanitize(error.Message); }
            }
        }
        internal string Read()
        {
            lock (sync)
            {
                if (StorageError != null) return "日志保存失败：" + StorageError + "\r\n以下为本次内存记录：\r\n" + memory;
                try
                {
                    var result = new StringBuilder();
                    foreach (string name in new[] { "previous-2.log", "previous-1.log", "current.log" })
                    {
                        string path = Path.Combine(directory, name);
                        if (File.Exists(path)) result.Append(File.ReadAllText(path, Encoding.UTF8));
                    }
                    return result.Length == 0 ? memory.ToString() : result.ToString();
                }
                catch (Exception error) { return "日志读取失败：" + Sanitize(error.Message) + "\r\n" + memory; }
            }
        }
    }
}
