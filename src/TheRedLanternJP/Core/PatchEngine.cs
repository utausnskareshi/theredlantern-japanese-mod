using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TheRedLanternJP.Core
{
    public enum PatchState
    {
        /// <summary>未適用（ゲームのファイルは原本のまま）。</summary>
        NotPatched,

        /// <summary>日本語化済みで、元に戻せる。</summary>
        Patched,

        /// <summary>
        /// 日本語化済みだが、台詞データは元に戻せない（バックアップが無い、または台詞データがこのツールの書いた内容ではない）。
        /// プログラム本体だけ戻せる。
        /// </summary>
        PatchedBackupMissing,

        /// <summary>未対応のバージョン、またはこのツール以外で変更されたファイル。何も書き込まない。</summary>
        Unsupported,
    }

    public sealed class GameStatus
    {
        public PatchState State { get; set; }
        public KnownBuild Build { get; set; }
        public bool DllPatched { get; set; }
        public bool TwigModified { get; set; }

        /// <summary>台詞データの原本のバックアップがあり、その内容が原本と一致している。</summary>
        public bool BackupAvailable { get; set; }

        /// <summary>
        /// 台詞データが変更されているが、このツールが書き込んだもの（原本をこのツールが日本語化しただけのもの）ではない。
        /// ゲームの更新などで別の内容に置き換わっているので、バックアップがあっても上書きしない。
        /// </summary>
        public bool TwigForeign { get; set; }

        /// <summary>変更されている台詞データを、バックアップから原本に戻せる。</summary>
        public bool TwigRestorable
        {
            get { return TwigModified && BackupAvailable && !TwigForeign; }
        }

        /// <summary>Unsupported のときの理由（利用者向け）。</summary>
        public string Reason { get; set; }
    }

    public sealed class ApplyResult
    {
        public int TotalKeys { get; set; }

        /// <summary>ゲームに元から入っている公式日本語訳の行数。</summary>
        public int OfficialLines { get; set; }

        /// <summary>補完訳として追記した行数（補完訳を使わなかった場合は 0）。</summary>
        public int SupplementLines { get; set; }

        /// <summary>補完訳ファイルにあったが、ゲーム側に該当キーが無かった件数。</summary>
        public int UnknownKeys { get; set; }

        /// <summary>選択肢などの既定の文言を日本語訳にした件数。</summary>
        public int DefaultsReplaced { get; set; }

        /// <summary>公式訳のうち、フォントに無い文字を含む語を書き直した件数。</summary>
        public int GlyphFixed { get; set; }
    }

    /// <summary>利用者にそのまま見せられるメッセージを持つ例外。</summary>
    public sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// 日本語化の適用と復元。書き込む前に必ずファイルの中身で状態を確かめ、
    /// 想定と違うファイルには手を付けない。
    /// </summary>
    public sealed class PatchEngine
    {
        public const string VerifyHint = "Steamのライブラリでゲームを右クリックし、「プロパティ」→「インストール済みファイル」→「ゲームファイルの整合性を確認」を実行すると、ゲームを正規の状態に戻せます。";

        private const string GameRunningMessage = "ゲームが起動中です。ゲームを終了してから、もう一度お試しください。";
        private const string BusyMessage = "このゲームのファイルは、別の画面（またはコマンド）で処理中です。そちらが終わってから、もう一度お試しください。";
        private const string ReadOnlyMessage = "ゲームのファイルが読み取り専用になっているため、変更できません。エクスプローラーでファイルのプロパティを開き、「読み取り専用」のチェックを外してから、もう一度お試しください。";

        private readonly string _installDir;
        private readonly IReadOnlyList<KnownBuild> _builds;
        private readonly Func<bool> _isGameRunning;

        public PatchEngine(string installDir, IReadOnlyList<KnownBuild> builds = null, Func<bool> isGameRunning = null)
        {
            if (string.IsNullOrEmpty(installDir)) throw new ArgumentNullException(nameof(installDir));
            _installDir = installDir;
            _builds = builds ?? KnownBuild.All;
            _isGameRunning = isGameRunning ?? IsGameProcessRunning;
        }

        /// <summary>
        /// テスト用。適用・復元の各段階の直後に、段階の名前を渡して呼ばれる
        /// （その時点での障害の注入や、「ここで電源が落ちたら」の状態の写しを取るために使う）。
        /// </summary>
        internal Action<string> StepHook { get; set; }

        public string GameAssemblyPath
        {
            get { return Path.Combine(_installDir, GameLayout.GameAssemblyRelPath); }
        }

        public string TwigPath
        {
            get { return Path.Combine(_installDir, GameLayout.TwigRelPath); }
        }

        public string BackupDir
        {
            get { return Path.Combine(_installDir, GameLayout.BackupDirName); }
        }

        private string BackupTwigPath
        {
            get { return Path.Combine(BackupDir, GameLayout.BackupTwigName); }
        }

        private string BackupInfoPath
        {
            get { return Path.Combine(BackupDir, GameLayout.BackupInfoName); }
        }

        public static bool IsGameProcessRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName(GameLayout.ProcessName);
                foreach (var p in processes) p.Dispose();
                return processes.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>ファイルの中身から現在の状態を判定する。何も書き込まない。</summary>
        public GameStatus GetStatus()
        {
            var status = new GameStatus { State = PatchState.Unsupported };
            if (!File.Exists(GameAssemblyPath) || !File.Exists(TwigPath))
            {
                status.Reason = "ゲームのファイルが見つかりません。Steamでゲームのインストール状態を確認してください。";
                return status;
            }

            long length = new FileInfo(GameAssemblyPath).Length;
            byte[] dll = null;
            string dllHash = null;
            foreach (var build in _builds)
            {
                if (length != build.GameAssemblySize) continue;
                if (dll == null) dll = File.ReadAllBytes(GameAssemblyPath);

                // パッチ箇所を先に見て、ファイル全体（約46MB）のハッシュ計算は必要な1回だけにする
                if (FileUtil.RegionEquals(dll, build.PatchOffset, build.OriginalBytes))
                {
                    if (dllHash == null) dllHash = Hashing.Sha256Hex(dll);
                    if (!Hashing.HexEquals(dllHash, build.GameAssemblySha256)) continue;
                    status.Build = build;
                    status.DllPatched = false;
                    break;
                }
                if (FileUtil.RegionEquals(dll, build.PatchOffset, build.PatchedBytes))
                {
                    // パッチ箇所だけを原本のバイト列に戻したものが原本と一致すれば、「このツールが当てたパッチだけが入った状態」
                    Buffer.BlockCopy(build.OriginalBytes, 0, dll, (int)build.PatchOffset, build.OriginalBytes.Length);
                    bool match = Hashing.HexEquals(Hashing.Sha256Hex(dll), build.GameAssemblySha256);
                    Buffer.BlockCopy(build.PatchedBytes, 0, dll, (int)build.PatchOffset, build.PatchedBytes.Length);
                    if (match)
                    {
                        status.Build = build;
                        status.DllPatched = true;
                        break;
                    }
                }
            }
            if (status.Build == null)
            {
                status.Reason = "このバージョンのゲームには対応していません（ゲームが更新されたか、ファイルが変更されています）。安全のため、何も変更しません。";
                return status;
            }

            byte[] twig = ReadIfReasonableSize(TwigPath);
            bool twigOriginal = twig != null && Hashing.HexEquals(Hashing.Sha256Hex(twig), status.Build.TwigSha256);
            status.TwigModified = !twigOriginal;
            byte[] backup = ReadValidBackup(status.Build);
            status.BackupAvailable = backup != null;

            // 変更されている台詞データが「このツールが書いたもの」かどうかを、内容で確かめる。
            // ゲームの更新などで別のテーブルに置き換わっている場合に、古いバックアップで上書きしてしまわないため
            if (!twigOriginal && backup != null) status.TwigForeign = !IsOurTwig(status.Build, backup, twig);

            if (twigOriginal)
            {
                status.State = status.DllPatched ? PatchState.Patched : PatchState.NotPatched;
            }
            else if (status.TwigRestorable)
            {
                status.State = PatchState.Patched;
            }
            else if (status.DllPatched)
            {
                status.State = PatchState.PatchedBackupMissing;
            }
            else
            {
                status.Reason = "台詞データが想定と異なります（ゲームが更新されたか、ファイルが変更されています）。安全のため、何も変更しません。";
            }
            return status;
        }

        /// <summary>
        /// 日本語化する。<paramref name="supplement"/> が null なら、ゲームに同梱されている公式の日本語訳を有効にするだけで、
        /// 台詞データは書き換えない。
        /// </summary>
        public ApplyResult Apply(Supplement supplement)
        {
            using (AcquireLock())
            {
                var status = GetStatus();
                if (status.State == PatchState.Unsupported) throw new PatchException(status.Reason);
                if (status.State != PatchState.NotPatched) throw new PatchException("すでに日本語化されています。");
                if (_isGameRunning()) throw new PatchException(GameRunningMessage);
                Step("apply:checked");

                var build = status.Build;

                // ここから先で使う台詞データは、この1回で読んだ内容だけにする（確かめた内容と、書き換える内容を一致させる）
                byte[] original = ReadIfReasonableSize(TwigPath);
                if (original == null || !Hashing.HexEquals(Hashing.Sha256Hex(original), build.TwigSha256))
                {
                    throw new PatchException("台詞データが、確認の後で変更されました。もう一度お試しください。");
                }

                // 何かを変更する前に、2つのファイルに実際に書き込めることを確かめる
                EnsureWritable(true, true);

                var result = new ApplyResult();
                DateTime dllTime = File.GetLastWriteTimeUtc(GameAssemblyPath);
                DateTime twigTime = File.GetLastWriteTimeUtc(TwigPath);

                bool twigWritten = false;
                try
                {
                    // 1) 台詞データの原本をバックアップ
                    Directory.CreateDirectory(BackupDir);
                    FileUtil.WriteOwnFile(BackupTwigPath, original);
                    if (ReadValidBackup(build) == null)
                    {
                        throw new PatchException("バックアップを作成できませんでした。ディスクの空き容量や書き込み権限を確認してください。");
                    }
                    var info = new BackupInfo();
                    info["toolVersion"] = AppInfo.Version;
                    info["build"] = build.Label;
                    info["state"] = "applying";
                    info["appliedAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    info["gameAssemblySha256"] = build.GameAssemblySha256;
                    info["twigSha256"] = build.TwigSha256;
                    info.SetTimeUtc("gameAssemblyLastWriteUtc", dllTime);
                    info.SetTimeUtc("twigLastWriteUtc", twigTime);
                    info.Save(BackupInfoPath);
                    Step("apply:backup");

                    // 2) 台詞データを日本語化する（補完訳の追記、公式訳のうち表示できない字の書き直し、
                    //    選択肢などの既定の文言の日本語化）。補完訳が無くても、公式訳だけで日本語化する
                    byte[] payload = TwigFile.Decompress(original);
                    IDictionary<string, string> lines = supplement != null ? supplement.Entries : new Dictionary<string, string>();
                    TwigPatchStats stats;
                    byte[] patched = TwigFile.Localize(payload, lines, build.GlyphFixes, out stats);
                    result.TotalKeys = stats.TotalKeys;
                    result.OfficialLines = stats.OfficialJapanese;
                    result.SupplementLines = stats.Added;
                    result.UnknownKeys = stats.UnknownKeys;
                    result.DefaultsReplaced = stats.DefaultsReplaced;
                    result.GlyphFixed = stats.GlyphFixed;
                    if (stats.Changed)
                    {
                        // 書き込む前に、書き換えたテーブルを読み直して件数が合うことを確かめる
                        var check = TwigFile.Inspect(patched);
                        if (check.TotalKeys != stats.TotalKeys || check.WithJapanese != stats.OfficialJapanese + stats.Added)
                        {
                            throw new PatchException("台詞データの書き換え結果を検証できませんでした。");
                        }
                        byte[] compressed = TwigFile.Compress(patched);
                        if (!FileUtil.BytesEqual(TwigFile.Decompress(compressed), patched))
                        {
                            throw new PatchException("台詞データの再圧縮結果を検証できませんでした。");
                        }
                        FileUtil.WriteAtomic(TwigPath, compressed);
                        twigWritten = true;
                        info["twigPatchedSha256"] = Hashing.Sha256Hex(compressed);
                        if (supplement != null) info["supplementSha256"] = supplement.Sha256;
                        info["supplementLines"] = stats.Added.ToString(CultureInfo.InvariantCulture);
                        info["defaultsReplaced"] = stats.DefaultsReplaced.ToString(CultureInfo.InvariantCulture);
                        info["glyphFixed"] = stats.GlyphFixed.ToString(CultureInfo.InvariantCulture);
                        Step("apply:twig");
                    }

                    // 3) 起動時の言語を日本語に切り替える
                    ReplacePatchBytes(build, build.OriginalBytes, build.PatchedBytes);
                    Step("apply:dll");

                    // 4) 書き込んだ結果をファイルから読み直して確かめる（「元に戻す」が使える状態になっていること。
                    //    台詞データを書いた場合は、それが「原本＋日本語訳の追記」だけの内容であることもここで確かめられる）
                    var after = GetStatus();
                    if (after.State != PatchState.Patched || !after.DllPatched || !after.BackupAvailable || after.TwigModified != twigWritten)
                    {
                        throw new PatchException("適用後の確認で、ファイルが想定どおりの内容になっていませんでした。変更を取り消します。");
                    }

                    info["state"] = "applied";
                    info.Save(BackupInfoPath);
                    Log.Info("日本語化を適用しました: 公式訳 " + result.OfficialLines + " 行 / 補完訳 " + result.SupplementLines + " 行"
                        + " / 既定の文言 " + result.DefaultsReplaced + " 件 / 字の書き直し " + result.GlyphFixed + " 件"
                        + (result.UnknownKeys > 0 ? " / ゲームに無いキー " + result.UnknownKeys + " 件は無視" : string.Empty));
                    return result;
                }
                catch (Exception ex)
                {
                    Log.Error("日本語化の適用に失敗したため、変更を取り消します。", ex);
                    Rollback(build, dllTime, twigTime);
                    throw;
                }
            }
        }

        /// <summary>パッチ適用前の状態に戻す（原本とハッシュが一致することまで確認する）。</summary>
        public void Restore()
        {
            using (AcquireLock())
            {
                var status = GetStatus();
                if (status.State == PatchState.Unsupported) throw new PatchException(status.Reason);
                if (status.State == PatchState.NotPatched) throw new PatchException("日本語化されていません（すでに元の状態です）。");
                if (_isGameRunning()) throw new PatchException(GameRunningMessage);
                Step("restore:checked");

                var build = status.Build;
                bool restoreTwig = status.TwigRestorable;

                // 書き戻す内容は、ここで1回だけ読んで、その内容そのものが原本であることを確かめてから使う
                byte[] backup = null;
                if (restoreTwig)
                {
                    backup = ReadValidBackup(build);
                    if (backup == null)
                    {
                        throw new PatchException("台詞データのバックアップが、確認の後で変更されました。もう一度お試しください。");
                    }
                }

                // 何かを変更する前に、これから書くファイルに実際に書き込めることを確かめる
                // （プログラム本体だけ戻して、台詞データで失敗する、という中途半端な結果を避ける）
                EnsureWritable(status.DllPatched, restoreTwig);

                var info = BackupInfo.Load(BackupInfoPath);

                if (status.DllPatched)
                {
                    ReplacePatchBytes(build, build.PatchedBytes, build.OriginalBytes);
                    if (!Hashing.HexEquals(Hashing.Sha256HexOfFile(GameAssemblyPath), build.GameAssemblySha256))
                    {
                        throw new PatchException("プログラム本体を元の状態に戻せませんでした。" + Environment.NewLine + VerifyHint);
                    }
                    FileUtil.TrySetLastWriteTimeUtc(GameAssemblyPath, info.GetTimeUtc("gameAssemblyLastWriteUtc"));
                    Step("restore:dll");
                }

                if (status.TwigModified)
                {
                    if (!restoreTwig)
                    {
                        throw new PatchException(
                            (status.TwigForeign
                                ? "プログラム本体は元に戻しました。台詞データは、このツールが書き込んだ内容と異なっていたため（ゲームの更新などで置き換わった可能性があります）、変更していません。"
                                : "プログラム本体は元に戻しましたが、台詞データのバックアップが見つからないため、台詞データは戻せませんでした。")
                            + Environment.NewLine + VerifyHint);
                    }
                    FileUtil.WriteAtomic(TwigPath, backup);
                    if (!Hashing.HexEquals(Hashing.Sha256HexOfFile(TwigPath), build.TwigSha256))
                    {
                        throw new PatchException("台詞データを元の状態に戻せませんでした。" + Environment.NewLine + VerifyHint);
                    }
                    FileUtil.TrySetLastWriteTimeUtc(TwigPath, info.GetTimeUtc("twigLastWriteUtc"));
                    Step("restore:twig");
                }

                DeleteBackup();
                Log.Info("元の状態に戻しました。");
            }
        }

        /// <summary>
        /// 失敗した適用を取り消す。プログラム本体と台詞データは別々に、それぞれ実際のファイルの中身を見て戻す
        /// （片方が読めない・書けない状態でも、もう片方は戻せるようにするため）。
        /// 適用は「両方とも原本」であることを確かめてから始めており、処理中は他のツールの書き込みを排他しているので、
        /// 原本と違っていれば、それはこの適用が書いた分である。
        /// </summary>
        private void Rollback(KnownBuild build, DateTime dllTime, DateTime twigTime)
        {
            bool reverted = true;
            try
            {
                byte[] region = ReadPatchRegion(build);
                if (FileUtil.BytesEqual(region, build.PatchedBytes))
                {
                    ReplacePatchBytes(build, build.PatchedBytes, build.OriginalBytes);
                    FileUtil.TrySetLastWriteTimeUtc(GameAssemblyPath, dllTime);
                }
                else if (!FileUtil.BytesEqual(region, build.OriginalBytes))
                {
                    reverted = false;
                }
            }
            catch (Exception ex)
            {
                reverted = false;
                Log.Error("プログラム本体の変更を取り消せませんでした。", ex);
            }

            try
            {
                byte[] twig = ReadIfReasonableSize(TwigPath);
                if (twig == null || !Hashing.HexEquals(Hashing.Sha256Hex(twig), build.TwigSha256))
                {
                    byte[] backup = ReadValidBackup(build);
                    if (backup != null)
                    {
                        FileUtil.WriteAtomic(TwigPath, backup);
                        FileUtil.TrySetLastWriteTimeUtc(TwigPath, twigTime);
                    }
                    else
                    {
                        reverted = false;
                    }
                }
            }
            catch (Exception ex)
            {
                reverted = false;
                Log.Error("台詞データの変更を取り消せませんでした。", ex);
            }

            try
            {
                // バックアップを消すのは、原本に戻ったことをファイルから確かめられたときだけ。
                // 取り消しきれなかった場合は残しておく（次回起動時に「元に戻す」で復元できる）
                if (reverted && GetStatus().State == PatchState.NotPatched) DeleteBackup();
            }
            catch (Exception ex)
            {
                Log.Error("取り消し後の確認に失敗しました。", ex);
            }
        }

        /// <summary>
        /// 適用・復元の間、同じゲームフォルダに対する他の適用・復元（別の画面やコマンド）を締め出す。
        /// 2つが同時に走ると、互いの書き込みを「想定外」と見なして取り消し合い、どちらでもない状態が残り得るため。
        /// </summary>
        private IDisposable AcquireLock()
        {
            Mutex mutex;
            try
            {
                string dir = Path.GetFullPath(_installDir).TrimEnd('\\').ToUpperInvariant();
                mutex = new Mutex(false, "TheRedLanternJP.patch." + Hashing.Sha256Hex(Encoding.UTF8.GetBytes(dir)).Substring(0, 32));
            }
            catch (Exception ex)
            {
                Log.Error("排他制御を用意できませんでした（排他なしで続行します）。", ex);
                return new OperationLock(null);
            }

            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // 前の持ち主が処理の途中で強制終了された。排他は取れている
            }
            if (!acquired)
            {
                mutex.Dispose();
                throw new PatchException(BusyMessage);
            }
            return new OperationLock(mutex);
        }

        private sealed class OperationLock : IDisposable
        {
            private readonly Mutex _mutex;

            public OperationLock(Mutex mutex)
            {
                _mutex = mutex;
            }

            public void Dispose()
            {
                if (_mutex == null) return;
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 取得したスレッド以外から解放しようとした場合など。プロセス終了時に解放される
                }
                _mutex.Dispose();
            }
        }

        private void Step(string name)
        {
            var hook = StepHook;
            if (hook != null) hook(name);
        }

        /// <summary>台詞データとして妥当な大きさのファイルなら中身を返す。大きすぎるものは読まずに null。</summary>
        private static byte[] ReadIfReasonableSize(string path)
        {
            return new FileInfo(path).Length <= TwigFile.MaxPayloadBytes ? File.ReadAllBytes(path) : null;
        }

        /// <summary>バックアップがあり、その内容が原本と一致していれば、中身を返す。そうでなければ null。</summary>
        private byte[] ReadValidBackup(KnownBuild build)
        {
            if (!File.Exists(BackupTwigPath)) return null;
            byte[] bytes = ReadIfReasonableSize(BackupTwigPath);
            return bytes != null && Hashing.HexEquals(Hashing.Sha256Hex(bytes), build.TwigSha256) ? bytes : null;
        }

        /// <summary>変更されている台詞データが、原本（バックアップ）をこのツールが日本語化しただけのものかどうか。</summary>
        private static bool IsOurTwig(KnownBuild build, byte[] originalGzip, byte[] currentGzip)
        {
            if (currentGzip == null) return false;
            try
            {
                return TwigFile.IsOriginalPlusJapanese(TwigFile.Decompress(originalGzip), TwigFile.Decompress(currentGzip), build.GlyphFixes);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is FormatException || ex is IOException)
            {
                return false; // gzip として、またはテーブルとして読めない
            }
        }

        /// <summary>
        /// これから書くファイルを、実際に書き込み用に開けることを確かめる。読み取り専用・権限不足・他のプログラムが使用中の
        /// いずれでも、ここで例外になる（まだ何も変更していない段階で止められる）。
        /// </summary>
        private void EnsureWritable(bool programFile, bool textTable)
        {
            if (programFile) EnsureWritable(GameAssemblyPath, FileShare.None);
            if (textTable) EnsureWritable(TwigPath, FileShare.Read);
        }

        private static void EnsureWritable(string path, FileShare share)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                throw new PatchException(ReadOnlyMessage + Environment.NewLine + "（" + path + "）");
            }
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, share))
            {
                // 開けることだけを確かめる（何も書かない）
            }
        }

        /// <summary>プログラム本体のパッチ箇所のバイト列を読む。ファイルのサイズが想定と違えば null。</summary>
        private byte[] ReadPatchRegion(KnownBuild build)
        {
            using (var fs = new FileStream(GameAssemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length != build.GameAssemblySize) return null;
                var bytes = new byte[build.OriginalBytes.Length];
                fs.Position = build.PatchOffset;
                FileUtil.ReadFully(fs, bytes);
                return bytes;
            }
        }

        /// <summary>パッチ箇所が <paramref name="expected"/> であることを確かめてから <paramref name="replacement"/> を書く。</summary>
        private void ReplacePatchBytes(KnownBuild build, byte[] expected, byte[] replacement)
        {
            using (var fs = new FileStream(GameAssemblyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (fs.Length != build.GameAssemblySize)
                {
                    throw new PatchException("プログラム本体のサイズが想定と異なるため、変更を中止しました。");
                }
                var current = new byte[expected.Length];
                fs.Position = build.PatchOffset;
                FileUtil.ReadFully(fs, current);
                if (!FileUtil.BytesEqual(current, expected))
                {
                    throw new PatchException("プログラム本体の内容が想定と異なるため、変更を中止しました。");
                }
                fs.Position = build.PatchOffset;
                fs.Write(replacement, 0, replacement.Length);
                fs.Flush(true);
            }
        }

        private void DeleteBackup()
        {
            FileUtil.TryDelete(BackupTwigPath);
            FileUtil.TryDelete(BackupInfoPath);
            try
            {
                if (Directory.Exists(BackupDir) && Directory.GetFileSystemEntries(BackupDir).Length == 0)
                {
                    Directory.Delete(BackupDir);
                }
            }
            catch (Exception)
            {
                // 空フォルダが残っても動作に影響しない
            }
        }
    }
}
