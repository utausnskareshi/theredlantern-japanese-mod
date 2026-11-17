using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TheRedLanternJP.Core
{
    /// <summary>台詞テーブルの集計値。</summary>
    public sealed class TwigStats
    {
        public int TotalKeys;
        public int WithJapanese;
    }

    /// <summary>補完訳を追記した結果。</summary>
    public sealed class TwigPatchStats
    {
        /// <summary>テーブル内のキー総数。</summary>
        public int TotalKeys;

        /// <summary>もともと日本語訳（公式訳）が入っていたキー数。</summary>
        public int OfficialJapanese;

        /// <summary>今回、日本語訳を追記したキー数。</summary>
        public int Added;

        /// <summary>補完訳にあるが、公式訳が既にあるため追記しなかったキー数。</summary>
        public int SkippedAlreadyTranslated;

        /// <summary>補完訳にあるが、ゲームのテーブルに存在しなかったキー数。</summary>
        public int UnknownKeys;

        /// <summary>公式訳のうち、ゲームのフォントに無い文字を含む語を書き直したキー数。</summary>
        public int GlyphFixed;

        /// <summary>既定の文言（DefaultString）を日本語訳にしたキー数。</summary>
        public int DefaultsReplaced;

        /// <summary>テーブルを1か所でも変えたか。</summary>
        public bool Changed
        {
            get { return Added + GlyphFixed + DefaultsReplaced > 0; }
        }
    }

    /// <summary>
    /// ゲームの台詞テーブル（.twig）を扱う。中身は gzip 圧縮された protobuf で、構造は次のとおり:
    /// <code>
    /// message StringKeyLookup { map&lt;string, StringKey&gt; Data = 1; }
    /// message StringKey {
    ///   string Key = 1; string AudioEvent = 2;
    ///   repeated int32 AvailableLanguages = 3;   // EN=0, DE=1, ... JA=5
    ///   int32 StringKeyType = 4;
    ///   map&lt;int32, string&gt; Localizations = 5; // 言語番号 → 文言
    ///   string DefaultString = 6;
    /// }
    /// </code>
    /// 書き換えるのは <see cref="Localize"/> が説明する3種類だけで、ほかのフィールドは1バイトも変えない。
    /// </summary>
    public static class TwigFile
    {
        /// <summary>ゲーム内の言語番号（FlumeLocalizationManager.Localizations.JA）。</summary>
        public const int LangJa = 5;

        /// <summary>英語の言語番号（FlumeLocalizationManager.Localizations.EN）。</summary>
        private const int LangEn = 0;

        /// <summary>StringKeyType のうち「テキスト」（選択肢・画面の文言など）。台詞は 1。</summary>
        private const ulong TypeText = 0;

        private const int FieldMapEntry = 1;
        private const int FieldEntryKey = 1;
        private const int FieldEntryValue = 2;
        private const int FieldAvailableLanguages = 3;
        private const int FieldStringKeyType = 4;
        private const int FieldLocalizations = 5;
        private const int FieldDefaultString = 6;

        /// <summary>
        /// 台詞テーブルとして扱う大きさの上限（ファイル・展開後とも）。実際のテーブルは展開後でも数MBなので、
        /// これを超えるものは台詞テーブルではない。壊れたファイルや細工されたファイルでメモリを使い切らないための歯止め。
        /// </summary>
        public const int MaxPayloadBytes = 64 * 1024 * 1024;

        public static byte[] Decompress(byte[] gzipBytes)
        {
            using (var input = new MemoryStream(gzipBytes))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > MaxPayloadBytes)
                    {
                        throw new InvalidDataException("台詞データの展開後のサイズが大きすぎます。");
                    }
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        public static byte[] Compress(byte[] payload)
        {
            using (var output = new MemoryStream())
            {
                using (var gz = new GZipStream(output, CompressionLevel.Optimal, true))
                {
                    gz.Write(payload, 0, payload.Length);
                }
                return output.ToArray();
            }
        }

        public static TwigStats Inspect(byte[] payload)
        {
            var stats = new TwigStats();
            foreach (var entryField in ProtoWire.Parse(payload))
            {
                if (entryField.Number != FieldMapEntry || entryField.WireType != ProtoField.WireBytes) continue;
                string key;
                ProtoField value;
                if (!ReadEntry(ProtoWire.Parse(entryField.Bytes), out key, out value)) continue;
                stats.TotalKeys++;
                if (HasLocalization(ProtoWire.Parse(value.Bytes), LangJa)) stats.WithJapanese++;
            }
            return stats;
        }

        /// <summary>テーブルから、日本語訳のあるキーとその訳文を取り出す。</summary>
        internal static Dictionary<string, string> ReadJapanese(byte[] payload)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entryField in ProtoWire.Parse(payload))
            {
                if (entryField.Number != FieldMapEntry || entryField.WireType != ProtoField.WireBytes) continue;
                string key;
                ProtoField value;
                if (!ReadEntry(ProtoWire.Parse(entryField.Bytes), out key, out value)) continue;
                foreach (var f in ProtoWire.Parse(value.Bytes))
                {
                    if (f.Number != FieldLocalizations || f.WireType != ProtoField.WireBytes) continue;
                    ulong language = 0; // map のキーが省略されている場合は 0（英語）
                    string text = null;
                    foreach (var e in ProtoWire.Parse(f.Bytes))
                    {
                        if (e.Number == 1 && e.WireType == ProtoField.WireVarint) language = e.Varint;
                        else if (e.Number == 2 && e.WireType == ProtoField.WireBytes) text = e.AsString();
                    }
                    if (language == (ulong)LangJa && text != null) result[key] = text;
                }
            }
            return result;
        }

        /// <summary>
        /// <paramref name="current"/> が「<paramref name="original"/> をこのツールが日本語化したもの」かどうかを、内容で確かめる。
        /// current に入っている日本語訳を使って original を日本語化し直し、1バイトも違わなければ true。
        /// ゲームの更新などで別のテーブルに置き換わっている場合や、壊れている場合は false になる。
        /// <para>
        /// 「元に戻す」は、これが true のテーブルだけをバックアップで上書きする。日本語化の形（<see cref="Localize"/> の出力）を
        /// 変えると、以前の版で日本語化したテーブルが false になって戻せなくなるので、変える場合は旧い形も受け付けること。
        /// いまは、<see cref="Localize"/> の形と、それより前の版の形（<see cref="AddJapanese"/> での追記だけ）の両方を受け付ける。
        /// </para>
        /// </summary>
        /// <param name="fixes">日本語化したときに使った、公式訳の字の置き換え（<see cref="KnownBuild.GlyphFixes"/>）。</param>
        public static bool IsOriginalPlusJapanese(byte[] original, byte[] current, IReadOnlyList<GlyphFix> fixes = null)
        {
            if (original == null || current == null) return false;
            try
            {
                var japanese = ReadJapanese(current);
                TwigPatchStats stats;
                if (FileUtil.BytesEqual(Localize(original, japanese, fixes, out stats), current)) return true;
                return FileUtil.BytesEqual(AddJapanese(original, japanese, out stats), current);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// 台詞テーブルを日本語化した新しいテーブルを返す。書き換えるのは次の3つだけ:
        /// <list type="number">
        /// <item>公式の日本語訳が無いキーに、補完訳を追記する（<see cref="AddJapanese"/> と同じ形）。</item>
        /// <item>公式の日本語訳のうち、ゲームの日本語フォントに無い文字を含む語を <paramref name="fixes"/> のとおり書き直す
        ///   （そのままだと「□」と表示されるため）。</item>
        /// <item>種類が「テキスト」（StringKeyType 0）で日本語訳のあるキーは、既定の文言（DefaultString）を日本語訳にする。
        ///   ゲームは選択肢を表示するとき、テキストの種類のキーについては言語の設定に関係なく既定の文言を使うため
        ///   （FlumeUnity.HandleDisplayBox）、こうしないと選択肢だけが英語のままになる。
        ///   既定の文言が英語の訳文と同じキーだけを対象にし、それ以外（制御用の文字列など）は変えない。</item>
        /// </list>
        /// 何も変えなかった場合は元の配列をそのまま返す。
        /// </summary>
        public static byte[] Localize(byte[] payload, IDictionary<string, string> supplement, IReadOnlyList<GlyphFix> fixes, out TwigPatchStats stats)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (supplement == null) throw new ArgumentNullException(nameof(supplement));
            var fixesByKey = new Dictionary<string, List<GlyphFix>>(StringComparer.Ordinal);
            if (fixes != null)
            {
                foreach (var fix in fixes)
                {
                    List<GlyphFix> list;
                    if (!fixesByKey.TryGetValue(fix.Key, out list)) fixesByKey[fix.Key] = list = new List<GlyphFix>();
                    list.Add(fix);
                }
            }

            stats = new TwigPatchStats();
            var matched = new HashSet<string>(StringComparer.Ordinal);
            var top = ProtoWire.Parse(payload);
            foreach (var entryField in top)
            {
                if (entryField.Number != FieldMapEntry || entryField.WireType != ProtoField.WireBytes) continue;
                var entry = ProtoWire.Parse(entryField.Bytes);
                string key;
                ProtoField value;
                if (!ReadEntry(entry, out key, out value)) continue;
                stats.TotalKeys++;

                var stringKey = ProtoWire.Parse(value.Bytes);
                bool official = HasLocalization(stringKey, LangJa);
                if (official) stats.OfficialJapanese++;
                bool changed = false;

                // 1) 公式訳の無いキーに補完訳を追記する
                string japanese;
                if (supplement.TryGetValue(key, out japanese))
                {
                    matched.Add(key);
                    if (official)
                    {
                        stats.SkippedAlreadyTranslated++;
                    }
                    else
                    {
                        InsertJapanese(stringKey, japanese);
                        stats.Added++;
                        changed = true;
                    }
                }

                // 2) 公式訳の中の、フォントに無い文字を含む語を書き直す
                List<GlyphFix> keyFixes;
                if (official && fixesByKey.TryGetValue(key, out keyFixes) && FixGlyphs(stringKey, keyFixes))
                {
                    stats.GlyphFixed++;
                    changed = true;
                }

                // 3) テキストの種類のキーは、既定の文言を日本語訳にする
                if (UseJapaneseAsDefault(stringKey))
                {
                    stats.DefaultsReplaced++;
                    changed = true;
                }

                if (!changed) continue;
                value.Bytes = ProtoWire.Serialize(stringKey);
                entryField.Bytes = ProtoWire.Serialize(entry);
            }
            stats.UnknownKeys = supplement.Count - matched.Count;
            return stats.Changed ? ProtoWire.Serialize(top) : payload;
        }

        /// <summary>
        /// 日本語訳の無いキーに、補完訳を追記した新しいテーブルを返す（この版より前の日本語化の形。
        /// いまは <see cref="Localize"/> を使い、これは以前の版が書いたテーブルを見分けるためにだけ使う）。
        /// 公式の日本語訳が既にあるキーは変更しない。1件も追記しなかった場合は元の配列をそのまま返す。
        /// </summary>
        public static byte[] AddJapanese(byte[] payload, IDictionary<string, string> supplement, out TwigPatchStats stats)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (supplement == null) throw new ArgumentNullException(nameof(supplement));
            stats = new TwigPatchStats();
            var matched = new HashSet<string>(StringComparer.Ordinal);
            var top = ProtoWire.Parse(payload);
            foreach (var entryField in top)
            {
                if (entryField.Number != FieldMapEntry || entryField.WireType != ProtoField.WireBytes) continue;
                var entry = ProtoWire.Parse(entryField.Bytes);
                string key;
                ProtoField value;
                if (!ReadEntry(entry, out key, out value)) continue;
                stats.TotalKeys++;

                var stringKey = ProtoWire.Parse(value.Bytes);
                bool hasJapanese = HasLocalization(stringKey, LangJa);
                if (hasJapanese) stats.OfficialJapanese++;

                string japanese;
                if (!supplement.TryGetValue(key, out japanese)) continue;
                matched.Add(key);
                if (hasJapanese)
                {
                    stats.SkippedAlreadyTranslated++;
                    continue;
                }

                InsertJapanese(stringKey, japanese);
                value.Bytes = ProtoWire.Serialize(stringKey);
                entryField.Bytes = ProtoWire.Serialize(entry);
                stats.Added++;
            }
            stats.UnknownKeys = supplement.Count - matched.Count;
            return stats.Added == 0 ? payload : ProtoWire.Serialize(top);
        }

        private static bool ReadEntry(List<ProtoField> entry, out string key, out ProtoField value)
        {
            key = null;
            value = null;
            foreach (var f in entry)
            {
                if (f.WireType != ProtoField.WireBytes) continue;
                if (f.Number == FieldEntryKey) key = f.AsString();
                else if (f.Number == FieldEntryValue) value = f;
            }
            return key != null && value != null;
        }

        private static bool HasLocalization(List<ProtoField> stringKey, int language)
        {
            foreach (var f in stringKey)
            {
                if (f.Number != FieldLocalizations || f.WireType != ProtoField.WireBytes) continue;
                ulong entryLanguage = 0; // map のキーが省略されている場合は 0（英語）
                foreach (var e in ProtoWire.Parse(f.Bytes))
                {
                    if (e.Number == 1 && e.WireType == ProtoField.WireVarint) entryLanguage = e.Varint;
                }
                if (entryLanguage == (ulong)language) return true;
            }
            return false;
        }

        /// <summary>Localizations の1項目（言語番号と文言）。</summary>
        private sealed class LocalizationItem
        {
            public ProtoField Field;          // StringKey の Localizations フィールド
            public List<ProtoField> Parts;    // その中身（1: 言語番号, 2: 文言）
            public ProtoField Text;           // 文言のフィールド（無ければ null）
        }

        /// <summary>
        /// Localizations のうち、指定した言語の項目。無ければ null。
        /// 同じ言語が複数あるときは最後のもの（protobuf の map と同じ扱いで、<see cref="ReadJapanese"/> とも同じ）。
        /// </summary>
        private static LocalizationItem FindLocalization(List<ProtoField> stringKey, int language)
        {
            LocalizationItem found = null;
            foreach (var f in stringKey)
            {
                if (f.Number != FieldLocalizations || f.WireType != ProtoField.WireBytes) continue;
                var parts = ProtoWire.Parse(f.Bytes);
                ulong entryLanguage = 0; // map のキーが省略されている場合は 0（英語）
                ProtoField text = null;
                foreach (var e in parts)
                {
                    if (e.Number == 1 && e.WireType == ProtoField.WireVarint) entryLanguage = e.Varint;
                    else if (e.Number == 2 && e.WireType == ProtoField.WireBytes) text = e;
                }
                if (entryLanguage == (ulong)language) found = new LocalizationItem { Field = f, Parts = parts, Text = text };
            }
            return found;
        }

        /// <summary>日本語の訳文の中の語を書き直す。変えた場合は true。</summary>
        private static bool FixGlyphs(List<ProtoField> stringKey, List<GlyphFix> fixes)
        {
            var japanese = FindLocalization(stringKey, LangJa);
            if (japanese == null || japanese.Text == null) return false;
            string before = japanese.Text.AsString();
            // UTF-8 として正しく読めない訳文は触らない（読み替えると、関係の無いバイトまで変わってしまう）
            if (!FileUtil.BytesEqual(Encoding.UTF8.GetBytes(before), japanese.Text.Bytes)) return false;
            string after = before;
            foreach (var fix in fixes) after = after.Replace(fix.From, fix.To);
            if (after == before) return false;
            japanese.Text.Bytes = Encoding.UTF8.GetBytes(after);
            japanese.Field.Bytes = ProtoWire.Serialize(japanese.Parts);
            return true;
        }

        /// <summary>
        /// 種類が「テキスト」のキーで、既定の文言が英語の訳文と同じなら、既定の文言を日本語訳にする。変えた場合は true。
        /// 台詞（種類 1）は言語ごとの訳文が使われるので触らない。既定の文言が無いキーに新しく足すこともしない。
        /// </summary>
        private static bool UseJapaneseAsDefault(List<ProtoField> stringKey)
        {
            ulong type = TypeText; // 省略時は 0
            ProtoField defaultString = null;
            foreach (var f in stringKey)
            {
                if (f.Number == FieldStringKeyType && f.WireType == ProtoField.WireVarint) type = f.Varint;
                else if (f.Number == FieldDefaultString && f.WireType == ProtoField.WireBytes) defaultString = f;
            }
            if (type != TypeText || defaultString == null) return false;

            var english = FindLocalization(stringKey, LangEn);
            var japanese = FindLocalization(stringKey, LangJa);
            if (english == null || english.Text == null || japanese == null || japanese.Text == null) return false;
            if (!FileUtil.BytesEqual(defaultString.Bytes, english.Text.Bytes)) return false;
            if (FileUtil.BytesEqual(defaultString.Bytes, japanese.Text.Bytes)) return false;
            defaultString.Bytes = (byte[])japanese.Text.Bytes.Clone();
            return true;
        }

        private static void InsertJapanese(List<ProtoField> stringKey, string japanese)
        {
            // 1) Localizations に { 5: 日本語 } を追加（既存の Localizations の直後、無ければ番号順の位置）
            byte[] localization = ProtoWire.Serialize(new[]
            {
                ProtoField.FromVarint(1, LangJa),
                ProtoField.FromString(2, japanese),
            });
            stringKey.Insert(InsertPosition(stringKey, FieldLocalizations), ProtoField.FromBytes(FieldLocalizations, localization));

            // 2) AvailableLanguages に 5 を追加
            int lastIndex = -1;
            bool listed = false;
            for (int i = 0; i < stringKey.Count; i++)
            {
                var f = stringKey[i];
                if (f.Number != FieldAvailableLanguages) continue;
                lastIndex = i;
                if (f.WireType == ProtoField.WireBytes)
                {
                    if (ProtoWire.ReadPackedVarints(f.Bytes).Contains(LangJa)) listed = true;
                }
                else if (f.WireType == ProtoField.WireVarint && f.Varint == LangJa)
                {
                    listed = true;
                }
            }
            if (listed) return;

            if (lastIndex >= 0 && stringKey[lastIndex].WireType == ProtoField.WireBytes)
            {
                var packed = stringKey[lastIndex];
                var bytes = new byte[packed.Bytes.Length + 1];
                Buffer.BlockCopy(packed.Bytes, 0, bytes, 0, packed.Bytes.Length);
                bytes[bytes.Length - 1] = LangJa; // 5 は1バイトの可変長整数
                packed.Bytes = bytes;
            }
            else if (lastIndex >= 0)
            {
                stringKey.Insert(lastIndex + 1, ProtoField.FromVarint(FieldAvailableLanguages, LangJa));
            }
            else
            {
                stringKey.Insert(InsertPosition(stringKey, FieldAvailableLanguages), ProtoField.FromBytes(FieldAvailableLanguages, new byte[] { LangJa }));
            }
        }

        /// <summary>同じ番号の最後のフィールドの直後。無ければ、より大きい番号の最初のフィールドの手前。</summary>
        private static int InsertPosition(List<ProtoField> fields, int number)
        {
            int position = -1;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Number == number) position = i + 1;
            }
            if (position >= 0) return position;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Number > number) return i;
            }
            return fields.Count;
        }
    }
}
