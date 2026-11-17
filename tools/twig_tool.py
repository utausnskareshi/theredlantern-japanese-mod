#!/usr/bin/env python3
"""補完訳づくりのための開発用ツール（Python 3 標準ライブラリのみ）。

    python tools/twig_tool.py stats                         ゲームの台詞テーブルの集計を表示
    python tools/twig_tool.py export-untranslated OUT.tsv   日本語訳の無い行を書き出す（翻訳作業用）
    python tools/twig_tool.py check translations/ja_supplement.tsv   補完訳ファイルを検証

どれも自分のPCにインストールされたゲームを読むだけで、ゲームのファイルは変更しません。
既定のインストール先以外にある場合は --game-dir で指定します。

注意: export-untranslated の出力にはゲームの英語原文が含まれます。作業用にとどめ、
リポジトリへのコミットや再配布はしないでください（work/ フォルダは .gitignore 済みです）。
"""
import argparse
import gzip
import os
import re
import sys
import unicodedata

DEFAULT_GAME_DIR = r"C:\Program Files (x86)\Steam\steamapps\common\The Red Lantern"
TWIG_REL = r"TheRedLantern_sw_Data\StreamingAssets\Flume\TheRedLantern_v001-Built.twig"
EN, JA = 0, 5

# ゲームが解釈する記号。訳文でも原文と同じものを同じ数だけ残す必要がある。
MARKUP = re.compile(
    r"<link=\"[^\"]*\">[^<]*</link>|<link=\"[^\"]*\">|</link>|</?b>|<page>"
    r"|\[(?:A|B|X|Y|Left Stick|Right Stick|LB|RB|LT|RT)\]")
PREFIX = re.compile(r"^\s*(%\?|\?%|<>)( ?)")


def read_varint(buf, pos):
    result = shift = 0
    while True:
        b = buf[pos]
        pos += 1
        result |= (b & 0x7F) << shift
        if not b & 0x80:
            return result, pos
        shift += 7


def fields(buf):
    pos = 0
    while pos < len(buf):
        key, pos = read_varint(buf, pos)
        number, wire = key >> 3, key & 7
        if wire == 0:
            value, pos = read_varint(buf, pos)
        elif wire == 2:
            length, pos = read_varint(buf, pos)
            value = buf[pos:pos + length]
            pos += length
        elif wire == 1:
            value = buf[pos:pos + 8]
            pos += 8
        elif wire == 5:
            value = buf[pos:pos + 4]
            pos += 4
        else:
            raise ValueError("未対応のワイヤータイプ: %d" % wire)
        yield number, wire, value


def load_twig(game_dir):
    """[(key, type, audio_event, {言語番号: 文言}, default_string)] を返す。"""
    path = os.path.join(game_dir, TWIG_REL)
    if not os.path.exists(path):
        sys.exit("台詞テーブルが見つかりません: %s\n--game-dir でゲームのフォルダを指定してください。" % path)
    with open(path, "rb") as f:
        payload = gzip.decompress(f.read())
    rows = []
    # ワイヤータイプ: 0 = 可変長整数, 2 = 長さ付きバイト列。想定と違う型のフィールドは読み飛ばす（ツール本体と同じ扱い）
    for number, wire, entry in fields(payload):
        if number != 1 or wire != 2:
            continue
        key, string_key = None, None
        for n, w, v in fields(entry):
            if w != 2:
                continue
            if n == 1:
                key = v.decode("utf-8")
            elif n == 2:
                string_key = v
        if key is None or string_key is None:
            continue
        kind, audio, default, loc = 0, "", "", {}
        for n, w, v in fields(string_key):
            if n == 2 and w == 2:
                audio = v.decode("utf-8")
            elif n == 4 and w == 0:
                kind = v
            elif n == 5 and w == 2:
                language, text = 0, ""
                for n2, w2, v2 in fields(v):
                    if n2 == 1 and w2 == 0:
                        language = v2
                    elif n2 == 2 and w2 == 2:
                        text = v2.decode("utf-8", errors="replace")
                loc[language] = text
            elif n == 6 and w == 2:
                default = v.decode("utf-8", errors="replace")
        rows.append((key, kind, audio, loc, default))
    return rows


def escape(text):
    return text.replace("\\", "\\\\").replace("\t", "\\t").replace("\r", "\\r").replace("\n", "\\n")


def escape_key(key):
    key = escape(key)
    if key.startswith(" "):
        key = "\\s" + key[1:]
    if key.endswith(" "):
        key = key[:-1] + "\\s"
    return key


def unescape(text):
    out, i = [], 0
    while i < len(text):
        c = text[i]
        if c == "\\" and i + 1 < len(text):
            i += 1
            out.append({"n": "\n", "t": "\t", "r": "\r", "s": " ", "\\": "\\"}.get(text[i], "\\" + text[i]))
        else:
            out.append(c)
        i += 1
    return "".join(out)


def load_supplement(path):
    entries, problems = {}, []
    try:
        with open(path, encoding="utf-8-sig") as f:
            lines = f.read().split("\n")
    except UnicodeDecodeError:
        # ツール本体も、UTF-8 として読めないファイルは（文字化けしたまま適用しないよう）受け付けない
        sys.exit("補完訳ファイルの文字コードが UTF-8 ではありません: %s\nUTF-8 で保存し直してください。" % path)
    for number, line in enumerate(lines, 1):
        line = line.rstrip("\r")
        if not line.strip() or line.startswith("#"):
            continue
        if any(ch != "\t" and unicodedata.category(ch) == "Cc" for ch in line):
            # ツール本体は、制御文字（NUL など）を含む行を無視する。改行やタブは \n \t と書く
            problems.append("%d行目: 制御文字が含まれています（ツールはこの行を無視します）" % number)
            continue
        if "\t" not in line or line.startswith("\t"):
            problems.append("%d行目: 「キー<TAB>訳文」の形式ではありません" % number)
            continue
        key, text = line.split("\t", 1)
        key, text = unescape(key), unescape(text)
        if key in entries:
            problems.append("%d行目: キーが重複しています: %s" % (number, key))
        entries[key] = (number, text)
    return entries, problems


def cmd_stats(args):
    rows = load_twig(args.game_dir)
    with_en = [r for r in rows if EN in r[3]]
    with_ja = [r for r in with_en if JA in r[3]]
    print("キー総数: %d" % len(rows))
    print("英語あり: %d  うち公式の日本語訳あり: %d  日本語訳なし: %d" % (len(with_en), len(with_ja), len(with_en) - len(with_ja)))
    missing = [r for r in with_en if JA not in r[3]]
    print("日本語訳なしの内訳: 音声付きの台詞 %d / それ以外（UI・選択肢など） %d" % (
        sum(1 for r in missing if r[2]), sum(1 for r in missing if not r[2])))
    print("日本語訳なしの英語の語数: %d" % sum(len(r[3][EN].split()) for r in missing))


def cmd_export(args):
    rows = [r for r in load_twig(args.game_dir) if EN in r[3] and JA not in r[3]]
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("# キー<TAB>種別(1=音声付き台詞, 0=UI・選択肢)<TAB>音声イベント名<TAB>英語原文  ※作業用。再配布しないこと\n")
        for key, kind, audio, loc, default in rows:
            f.write("\t".join([escape_key(key), str(kind), audio, escape(loc[EN])]) + "\n")
    print("%d 行を書き出しました: %s" % (len(rows), args.out))


def load_font_chars(args):
    """ゲーム同梱の日本語フォントで表示できる文字の一覧（無ければ None）。"""
    path = args.font_chars or os.path.join(os.path.dirname(os.path.abspath(args.supplement)), "font_chars.txt")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8-sig") as f:
        return set(f.read()) - {"\n", "\r"}


def cmd_check(args):
    game = {r[0]: r for r in load_twig(args.game_dir)}
    entries, problems = load_supplement(args.supplement)
    errors = list(problems)
    warnings = []
    font_chars = load_font_chars(args)
    if font_chars is None:
        warnings.append("font_chars.txt が見つからないため、フォントに無い文字のチェックを省略しました")
    for key, (number, text) in entries.items():
        where = "%d行目 (%s)" % (number, key)
        row = game.get(key)
        if row is None:
            errors.append("%s: ゲームに存在しないキーです" % where)
            continue
        if JA in row[3]:
            warnings.append("%s: 公式の日本語訳がある行です（ツールは公式訳を優先し、この行は使いません）" % where)
            continue
        english = row[3].get(EN, row[4])
        if not text.strip():
            errors.append("%s: 訳文が空です" % where)
        if sorted(MARKUP.findall(english)) != sorted(MARKUP.findall(text)):
            errors.append("%s: タグ・ボタン記号が原文と一致しません" % where)
        want = PREFIX.match(english)
        have = PREFIX.match(text)
        if (want.group(0).lstrip() if want else "") != (have.group(0) if have else ""):
            errors.append("%s: 行頭の記号（%%? や <>）が原文と一致しません" % where)
        if "\u3000" in text or "..." in text or re.search(r"[\uFF61-\uFF9F]", text):
            warnings.append("%s: 全角スペース・「...」・半角カナが含まれています" % where)
        if font_chars is not None:
            # ゲームの日本語フォントは収録文字が固定で、無い文字は「□」になる。
            # 取り除くのはタグだけ（<link ...> と </link> の間の文字は画面に表示されるので検査する）
            visible = re.sub(r"<[^>]+>", "", text)
            missing = sorted({ch for ch in visible if ch not in font_chars})
            if missing:
                errors.append("%s: ゲームのフォントに無い文字があります: %s" % (where, "".join(missing)))
    need = [k for k, r in game.items() if EN in r[3] and JA not in r[3]]
    covered = sum(1 for k in need if k in entries)
    for message in errors:
        print("エラー:", message)
    for message in warnings[:30]:
        print("警告:", message)
    if len(warnings) > 30:
        print("警告: ほか %d 件" % (len(warnings) - 30))
    print("補完訳: %d 行 / 日本語訳の無い行: %d 行中 %d 行をカバー（未カバー %d 行は英語のまま表示）" % (
        len(entries), len(need), covered, len(need) - covered))
    print("エラー %d 件、警告 %d 件" % (len(errors), len(warnings)))
    return 1 if errors else 0


def main():
    # ファイルやパイプに出力しても文字化けしないよう、標準出力・標準エラーとも UTF-8 にする
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="The Red Lantern 日本語補完訳の開発用ツール")
    parser.add_argument("--game-dir", default=DEFAULT_GAME_DIR, help="ゲームのインストール先")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("stats", help="台詞テーブルの集計")
    export = sub.add_parser("export-untranslated", help="日本語訳の無い行を書き出す")
    export.add_argument("out")
    check = sub.add_parser("check", help="補完訳ファイルを検証")
    check.add_argument("supplement")
    check.add_argument("--font-chars", help="フォント収録文字の一覧（既定: 補完訳と同じフォルダの font_chars.txt）")
    args = parser.parse_args()
    if args.command == "stats":
        return cmd_stats(args)
    if args.command == "export-untranslated":
        return cmd_export(args)
    return cmd_check(args)


if __name__ == "__main__":
    sys.exit(main())
