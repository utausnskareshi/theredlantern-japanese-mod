using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TheRedLanternJP.Core
{
    /// <summary>%LOCALAPPDATA%\TheRedLanternJP\TheRedLanternJP.log への簡易ログ。書けなくても処理は止めない。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 512 * 1024;

        /// <summary>
        /// テスト用。設定すると、利用者のログファイルではなく、このファイルに書く
        /// （単体テストが利用者の実際のログを汚したり、上限を超えさせて消したりしないようにするため）。
        /// </summary>
        internal static string PathOverride { get; set; }

        public static string FilePath
        {
            get
            {
                if (PathOverride != null) return PathOverride;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheRedLanternJP");
                return Path.Combine(dir, "TheRedLanternJP.log");
            }
        }

        public static void Info(string message)
        {
            Write("INFO ", message);
        }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);
        }

        /// <summary>
        /// ログの日時。Windows の暦の設定（和暦など）に左右されないよう、常に西暦の固定書式にする
        /// （和暦の設定では、書式を指定しただけだと 2026 年が「08」と出力される）。
        /// </summary>
        internal static string FormatTimestamp(DateTime time)
        {
            return time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var file = new FileInfo(path);
                    if (file.Exists && file.Length > MaxBytes) file.Delete();
                    File.AppendAllText(
                        path,
                        FormatTimestamp(DateTime.Now) + " " + level + " " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // ログが書けないことで本処理を失敗させない
            }
        }
    }
}
