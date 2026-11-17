using System.Collections.Generic;

namespace TheRedLanternJP.Core
{
    /// <summary>
    /// パッチ対象として検証済みのゲームビルド1つ分の情報。
    /// ここに載っていないビルド（ハッシュが一致しないファイル）には一切書き込まない。
    /// </summary>
    /// <summary>
    /// 公式の日本語訳のうち、ゲームの日本語フォントに無い文字を含む語を、表示できる書き方に置き換える指定1件分。
    /// （このフォントは収録文字が固定で、無い文字は「□」と表示される。）
    /// ここに持つのは置き換える語だけで、訳文そのものは持たない。
    /// </summary>
    public sealed class GlyphFix
    {
        public GlyphFix(string key, string from, string to)
        {
            Key = key;
            From = from;
            To = to;
        }

        /// <summary>台詞テーブルのキー。</summary>
        public string Key { get; }

        /// <summary>公式訳の中の、置き換える語（フォントに無い文字を含む）。</summary>
        public string From { get; }

        /// <summary>置き換えた後の語（フォントにある文字だけ）。</summary>
        public string To { get; }
    }

    public sealed class KnownBuild
    {
        private static readonly GlyphFix[] NoFixes = new GlyphFix[0];

        public string Label { get; set; }

        /// <summary>未改変の GameAssembly.dll の SHA-256（16進）。</summary>
        public string GameAssemblySha256 { get; set; }
        public long GameAssemblySize { get; set; }

        /// <summary>書き換える位置（ファイル先頭からのオフセット）。</summary>
        public long PatchOffset { get; set; }
        public byte[] OriginalBytes { get; set; }
        public byte[] PatchedBytes { get; set; }

        /// <summary>未改変の台詞テーブル（.twig）の SHA-256（16進）。</summary>
        public string TwigSha256 { get; set; }

        /// <summary>このビルドの公式訳に対する、フォントに無い文字の置き換え（無ければ空）。</summary>
        public IReadOnlyList<GlyphFix> GlyphFixes { get; set; } = NoFixes;

        public static readonly IReadOnlyList<KnownBuild> All = new[]
        {
            new KnownBuild
            {
                Label = "Steam ビルド 20391034",
                GameAssemblySha256 = "20862DF2A38970929DADEC1F426F5A72138EBCCF0ECB5CA2C5B09615FBA9DC33",
                GameAssemblySize = 46446592,

                // FlumeUnity.Init() の末尾は SetLocalization(EN) を呼んでいる:
                //   45 33 C0   xor r8d, r8d
                //   33 D2      xor edx, edx      ; edx = 0 (EN)
                // これを次の1命令に置き換えて、起動時の言語を日本語(5)にする:
                //   BA 05 00 00 00   mov edx, 5  ; edx = 5 (JA)
                // r8d は呼び出し先で使われない（呼び出し先が自分で 0 にする）ため省略して問題ない。
                PatchOffset = 0xE54D14,
                OriginalBytes = new byte[] { 0x45, 0x33, 0xC0, 0x33, 0xD2 },
                PatchedBytes = new byte[] { 0xBA, 0x05, 0x00, 0x00, 0x00 },

                TwigSha256 = "0DD78E9256E21B95BA752D0E34822E71F3EAEDB305309FCF8FB71CC58F636BAE",

                // 公式訳の4行（音声付きの台詞）に、ゲームの日本語フォントに無い漢字が計5文字ある（憧・癖・型・瀬・戸）。
                // そのままだと字幕で「□」になる（1つ目は導入部で必ず流れる）ので、その語だけをフォントにある文字で書き直す。
                GlyphFixes = new[]
                {
                    new GlyphFix("47455af8-3f88-45f0-82f1-428334174dcc", "憧れ", "あこがれ"),
                    new GlyphFix("56a72218-4e63-4a63-adaa-d8e23cb5d26c", "追い癖", "追いぐせ"),
                    new GlyphFix("e586574b-486a-4b3c-8ddd-e9eb1e3a5a26", "小型犬", "小さな犬"),
                    new GlyphFix("7def7414-6e5c-4e0b-b8f6-fc7152548211", "瀬戸際", "せとぎわ"),
                },
            },
        };
    }
}
