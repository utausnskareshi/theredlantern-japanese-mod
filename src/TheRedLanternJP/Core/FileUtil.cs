using System;
using System.IO;

namespace TheRedLanternJP.Core
{
    internal static class FileUtil
    {
        /// <summary>
        /// 一時ファイルに書いてから置き換える。書き込みの途中で失敗しても、元のファイルが壊れた状態で残らない。
        /// </summary>
        public static void WriteAtomic(string path, byte[] bytes)
        {
            string temp = path + ".jptmp";
            try
            {
                // 一時ファイルの書き込み自体が失敗した場合（空き容量不足など）も、書きかけを残さない
                TryClearAttributes(temp); // 以前の実行が強制終了されて残った一時ファイルがあっても、上書きできるようにする
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (IOException)
                    {
                        // ReplaceFile が使えないファイルシステム向けの代替
                        File.Copy(temp, path, true);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>このツールが作ったファイル（バックアップ・一時ファイル）を消す。読み取り専用になっていても消す。</summary>
        public static void TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception)
            {
                // 後始末の失敗は無視する
            }
        }

        /// <summary>
        /// このツールが作ったファイルの属性（読み取り専用など）を外す。ゲームのファイルが読み取り専用だった場合、
        /// その写しであるバックアップも読み取り専用になり、次回の上書きや削除ができなくなるため。
        /// </summary>
        public static void TryClearAttributes(string path)
        {
            try
            {
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            }
            catch (Exception)
            {
                // 外せなければ、続く書き込みが失敗して呼び出し元に伝わる
            }
        }

        /// <summary>このツールが管理するファイル（バックアップ）を、以前の残りがあっても上書きして書く。</summary>
        public static void WriteOwnFile(string path, byte[] bytes)
        {
            TryClearAttributes(path);
            File.WriteAllBytes(path, bytes);
        }

        public static void TrySetLastWriteTimeUtc(string path, DateTime? utc)
        {
            if (utc == null) return;
            try
            {
                File.SetLastWriteTimeUtc(path, utc.Value);
            }
            catch (Exception)
            {
                // 更新日時を戻せなくても、内容は元どおりなので問題にしない
            }
        }

        public static void ReadFully(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        public static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary><paramref name="data"/> の <paramref name="offset"/> 位置が <paramref name="expected"/> と一致するか。</summary>
        public static bool RegionEquals(byte[] data, long offset, byte[] expected)
        {
            if (offset < 0 || offset + expected.Length > data.Length) return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (data[offset + i] != expected[i]) return false;
            }
            return true;
        }
    }
}
