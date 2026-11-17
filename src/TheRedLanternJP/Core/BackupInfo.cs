using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TheRedLanternJP.Core
{
    /// <summary>
    /// バックアップフォルダに置く控え（<c>キー=値</c> のテキスト）。
    /// 復元の可否はファイルのハッシュだけで判断するので、この控えは記録と更新日時の復元のためのもの。
    /// </summary>
    internal sealed class BackupInfo
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string this[string key]
        {
            get
            {
                string value;
                return _values.TryGetValue(key, out value) ? value : null;
            }
            set
            {
                if (value == null) _values.Remove(key);
                else _values[key] = value;
            }
        }

        /// <summary>更新日時（UTC）の読み書き。読めなければ null。</summary>
        public DateTime? GetTimeUtc(string key)
        {
            long ticks;
            if (!long.TryParse(this[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return null;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        public void SetTimeUtc(string key, DateTime utc)
        {
            this[key] = utc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# The Red Lantern 日本語化パッチのバックアップ情報です。「元に戻す」で使います。削除しないでください。");
            foreach (var pair in _values)
            {
                sb.Append(pair.Key).Append('=').AppendLine(pair.Value);
            }
            FileUtil.TryClearAttributes(path); // 以前の残りが読み取り専用でも上書きできるようにする
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>この控えとして読む大きさの上限。実際は数百バイトなので、それより極端に大きいものは控えとして扱わない。</summary>
        private const long MaxBytes = 64 * 1024;

        /// <summary>
        /// 控えを読む。無い・読めない・壊れている場合は空の控えを返す
        /// （控えは記録用なので、読めないことを理由に「元に戻す」を失敗させない。更新日時が戻らないだけ）。
        /// </summary>
        public static BackupInfo Load(string path)
        {
            var info = new BackupInfo();
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > MaxBytes) return info;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    info[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                Log.Error("バックアップ情報を読めませんでした（更新日時は復元しません）: " + path, ex);
            }
            return info;
        }
    }
}
