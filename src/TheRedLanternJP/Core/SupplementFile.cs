using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TheRedLanternJP.Core
{
    /// <summary>補完訳（公式の日本語訳が無い行のための訳）を読み込んだもの。</summary>
    public sealed class Supplement
    {
        public Dictionary<string, string> Entries { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> Warnings { get; } = new List<string>();
        public string SourcePath { get; set; }
        public string Sha256 { get; set; }
    }

    /// <summary>
    /// 補完訳ファイル（UTF-8 のタブ区切りテキスト）の読み込み。
    /// <para>1行 = <c>キー&lt;TAB&gt;日本語訳</c>。<c>#</c> で始まる行と空行は無視する。</para>
    /// <para>エスケープ: <c>\\</c> <c>\t</c> <c>\n</c> <c>\r</c> と、空白1文字を表す <c>\s</c>（キー先頭の空白を保つため）。</para>
    /// </summary>
    public static class SupplementFile
    {
        /// <summary>ツールと同じ場所から探すときの既定の相対パス。</summary>
        public static readonly string[] DefaultRelativePaths =
        {
            @"translations\ja_supplement.tsv",
            "ja_supplement.tsv",
        };

        /// <summary>既定の場所に補完訳ファイルがあれば、そのパスを返す。無ければ null。</summary>
        public static string FindDefault(string baseDirectory)
        {
            foreach (string relative in DefaultRelativePaths)
            {
                string path = Path.Combine(baseDirectory, relative);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>補完訳ファイルとして読む大きさの上限（実際のファイルは数百KB）。</summary>
        public const long MaxFileBytes = 16 * 1024 * 1024;

        /// <summary>
        /// 補完訳ファイルを読む。文字コードが UTF-8 でない場合や大きすぎる場合は、
        /// 利用者向けの説明を持つ <see cref="PatchException"/> を投げる。
        /// </summary>
        public static Supplement Load(string path)
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                throw new PatchException("補完訳ファイルが大きすぎます（上限 " + (MaxFileBytes / (1024 * 1024)) + " MB）。");
            }
            byte[] bytes = File.ReadAllBytes(path);
            Supplement supplement;
            using (var reader = new StringReader(Decode(bytes)))
            {
                supplement = Parse(reader);
            }
            supplement.SourcePath = path;
            supplement.Sha256 = Hashing.Sha256Hex(bytes);
            return supplement;
        }

        /// <summary>
        /// バイト列を文字列にする。UTF-8（BOM の有無は問わない）と、BOM 付きの UTF-16 を受け付ける。
        /// 不正なバイト列を黙って「?」に置き換えることはしない。Shift_JIS などで保存し直されたファイルを、
        /// 文字化けしたままゲームに書き込まないため。
        /// </summary>
        internal static string Decode(byte[] bytes)
        {
            try
            {
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
                }
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
                }
                int bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                return new UTF8Encoding(false, true).GetString(bytes, bom, bytes.Length - bom);
            }
            catch (DecoderFallbackException)
            {
                throw new PatchException("補完訳ファイルの文字コードが UTF-8 ではありません。UTF-8 で保存し直してください。");
            }
        }

        public static Supplement Parse(TextReader reader)
        {
            var supplement = new Supplement();
            int lineNumber = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (lineNumber == 1 && line.Length > 0 && line[0] == (char)0xFEFF) line = line.Substring(1); // BOM (U+FEFF)
                if (line.Trim().Length == 0 || line[0] == '#') continue;
                if (HasControlCharacter(line))
                {
                    // NUL などの制御文字は、文字コードの取り違えや壊れたファイルでしか現れない。改行やタブは \n \t と書く決まり
                    supplement.Warnings.Add(lineNumber + "行目: 制御文字が含まれているため無視しました。");
                    continue;
                }

                int tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    supplement.Warnings.Add(lineNumber + "行目: 「キー<TAB>訳文」の形式ではないため無視しました。");
                    continue;
                }
                string key = Unescape(line.Substring(0, tab));
                string text = Unescape(line.Substring(tab + 1));
                if (text.Length == 0)
                {
                    supplement.Warnings.Add(lineNumber + "行目: 訳文が空のため無視しました。");
                    continue;
                }
                if (supplement.Entries.ContainsKey(key))
                {
                    supplement.Warnings.Add(lineNumber + "行目: キーが重複しています（後の行を採用）。");
                }
                supplement.Entries[key] = text;
            }
            return supplement;
        }

        /// <summary>区切りのタブ以外の制御文字（U+0000〜U+001F、U+007F〜U+009F）を含むか。</summary>
        private static bool HasControlCharacter(string line)
        {
            foreach (char c in line)
            {
                if (c != '\t' && char.IsControl(c)) return true;
            }
            return false;
        }

        internal static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 's': sb.Append(' '); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}
