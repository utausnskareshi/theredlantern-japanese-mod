using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TheRedLanternJP.Core
{
    /// <summary>protobuf のフィールド1つ（ワイヤー形式のまま保持する）。</summary>
    internal sealed class ProtoField
    {
        public const int WireVarint = 0;
        public const int WireFixed64 = 1;
        public const int WireBytes = 2;
        public const int WireFixed32 = 5;

        public int Number;
        public int WireType;

        /// <summary>WireType が Varint のときの値。</summary>
        public ulong Varint;

        /// <summary>WireType が Bytes / Fixed64 / Fixed32 のときの中身。</summary>
        public byte[] Bytes;

        public static ProtoField FromVarint(int number, ulong value)
        {
            return new ProtoField { Number = number, WireType = WireVarint, Varint = value };
        }

        public static ProtoField FromBytes(int number, byte[] value)
        {
            return new ProtoField { Number = number, WireType = WireBytes, Bytes = value };
        }

        public static ProtoField FromString(int number, string value)
        {
            return FromBytes(number, Encoding.UTF8.GetBytes(value));
        }

        public string AsString()
        {
            return Encoding.UTF8.GetString(Bytes);
        }
    }

    /// <summary>
    /// スキーマ無しで protobuf メッセージを「フィールドの並び」として読み書きする。
    /// 触らないフィールドは1バイトも変えずに書き戻せる
    /// （<see cref="Parse"/> が受け付けたデータは、<see cref="Serialize"/> で必ず同じバイト列に戻る。
    /// そうならない書き方のデータは <see cref="FormatException"/> で断る）。
    /// </summary>
    internal static class ProtoWire
    {
        /// <summary>protobuf で使えるフィールド番号の上限（2^29 - 1）。</summary>
        public const int MaxFieldNumber = 536870911;

        public static List<ProtoField> Parse(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var fields = new List<ProtoField>();
            int pos = 0;
            int end = data.Length;
            while (pos < end)
            {
                ulong key = ReadVarint(data, ref pos, end);
                ulong number = key >> 3;
                int wireType = (int)(key & 7);
                // 範囲外の番号を int に切り詰めると、別の番号として書き戻してしまう
                if (number == 0 || number > MaxFieldNumber) throw new FormatException("protobuf: フィールド番号 " + number + " は不正です。");
                var field = new ProtoField { Number = (int)number, WireType = wireType };
                switch (wireType)
                {
                    case ProtoField.WireVarint:
                        field.Varint = ReadVarint(data, ref pos, end);
                        break;
                    case ProtoField.WireFixed64:
                        field.Bytes = Take(data, ref pos, end, 8);
                        break;
                    case ProtoField.WireBytes:
                        ulong length = ReadVarint(data, ref pos, end);
                        if (length > (ulong)(end - pos)) throw new FormatException("protobuf: 長さがデータの終端を超えています。");
                        field.Bytes = Take(data, ref pos, end, (int)length);
                        break;
                    case ProtoField.WireFixed32:
                        field.Bytes = Take(data, ref pos, end, 4);
                        break;
                    default:
                        throw new FormatException("protobuf: 未対応のワイヤータイプ " + wireType + " です。");
                }
                fields.Add(field);
            }
            return fields;
        }

        public static byte[] Serialize(IList<ProtoField> fields)
        {
            using (var ms = new MemoryStream())
            {
                foreach (var f in fields)
                {
                    WriteVarint(ms, ((ulong)f.Number << 3) | (uint)f.WireType);
                    switch (f.WireType)
                    {
                        case ProtoField.WireVarint:
                            WriteVarint(ms, f.Varint);
                            break;
                        case ProtoField.WireBytes:
                            WriteVarint(ms, (ulong)f.Bytes.Length);
                            ms.Write(f.Bytes, 0, f.Bytes.Length);
                            break;
                        case ProtoField.WireFixed64:
                        case ProtoField.WireFixed32:
                            ms.Write(f.Bytes, 0, f.Bytes.Length);
                            break;
                        default:
                            throw new FormatException("protobuf: 未対応のワイヤータイプ " + f.WireType + " です。");
                    }
                }
                return ms.ToArray();
            }
        }

        public static ulong ReadVarint(byte[] data, ref int pos, int end)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                if (pos >= end) throw new FormatException("protobuf: 可変長整数の途中でデータが終わりました。");
                if (shift > 63) throw new FormatException("protobuf: 可変長整数が長すぎます。");
                byte b = data[pos++];
                // 10バイト目（shift = 63）に入れられるのは最下位の1ビットだけ。それ以上は64ビットに収まらない
                if (shift == 63 && b > 1) throw new FormatException("protobuf: 可変長整数が64ビットに収まりません。");
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    // 最短でない書き方（末尾が 0 のバイト）は、書き戻すと別のバイト列になる。
                    // 「読めたものは1バイトも変えずに書き戻せる」を保つため、受け付けない
                    if (b == 0 && shift > 0) throw new FormatException("protobuf: 可変長整数が最短の形式ではありません。");
                    return result;
                }
                shift += 7;
            }
        }

        public static void WriteVarint(Stream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }

        /// <summary>packed repeated（可変長整数の連続）を読む。</summary>
        public static List<ulong> ReadPackedVarints(byte[] data)
        {
            var values = new List<ulong>();
            int pos = 0;
            while (pos < data.Length) values.Add(ReadVarint(data, ref pos, data.Length));
            return values;
        }

        private static byte[] Take(byte[] data, ref int pos, int end, int count)
        {
            if (count < 0 || count > end - pos) throw new FormatException("protobuf: データが途中で終わりました。");
            var result = new byte[count];
            Buffer.BlockCopy(data, pos, result, 0, count);
            pos += count;
            return result;
        }
    }
}
