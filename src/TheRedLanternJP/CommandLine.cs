using System;
using System.IO;
using System.Runtime.InteropServices;
using TheRedLanternJP.Core;

namespace TheRedLanternJP
{
    /// <summary>
    /// 画面を出さずに操作するためのコマンドライン（自動テストや上級者向け）。
    /// <code>
    /// TheRedLanternJP.exe --status  [--game-dir &lt;フォルダ&gt;]
    /// TheRedLanternJP.exe --apply   [--game-dir &lt;フォルダ&gt;] [--supplement &lt;ファイル&gt; | --no-supplement]
    /// TheRedLanternJP.exe --restore [--game-dir &lt;フォルダ&gt;]
    /// </code>
    /// </summary>
    internal static class CommandLine
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        public static int Run(string[] args)
        {
            // ウィンドウアプリとしてビルドしているので、コンソールから起動されたときは親のコンソールに出力する。
            // ファイルやパイプに出力するときは、文字化けしないよう UTF-8 にする。
            if (Console.IsOutputRedirected)
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            }
            else
            {
                AttachConsole(-1);
            }

            string command = null;
            string gameDir = null;
            string supplementPath = null;
            bool noSupplement = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--status":
                    case "--apply":
                    case "--restore":
                        if (command != null) return Usage(); // 操作は1つだけ（--apply --restore のような指定は受け付けない）
                        command = args[i].ToLowerInvariant();
                        break;
                    case "--game-dir":
                        if (++i >= args.Length) return Usage();
                        gameDir = args[i];
                        break;
                    case "--supplement":
                        if (++i >= args.Length) return Usage();
                        supplementPath = args[i];
                        break;
                    case "--no-supplement":
                        noSupplement = true;
                        break;
                    case "--help":
                    case "-h":
                    case "-?":
                    case "/?":
                        return Usage(0); // 使い方を尋ねられた場合は、正常終了
                    default:
                        return Usage();
                }
            }
            if (command == null) return Usage();
            if (noSupplement && supplementPath != null) return Usage(); // 補完訳を「使う」と「使わない」の両方は指定できない

            try
            {
                GameInstall install = null; // --game-dir を指定したときは null（Steamの情報は見ない）
                if (gameDir == null)
                {
                    install = SteamLocator.Find();
                    if (install == null)
                    {
                        Console.WriteLine("not-installed: Steam版 The Red Lantern が見つかりません。");
                        return 2;
                    }
                    gameDir = install.InstallDir;
                }
                var engine = new PatchEngine(gameDir);
                Console.WriteLine("game-dir: " + gameDir);

                if (command == "--status")
                {
                    return PrintStatus(engine.GetStatus());
                }
                if (command == "--apply")
                {
                    // Steamが更新中のファイルには書き込まない（画面から操作したときと同じ）
                    if (install != null && install.UpdatePending)
                    {
                        Console.WriteLine("error: " + SteamLocator.UpdatePendingMessage);
                        return 1;
                    }

                    Supplement supplement = null;
                    if (!noSupplement)
                    {
                        string path = supplementPath ?? SupplementFile.FindDefault(AppDomain.CurrentDomain.BaseDirectory);
                        if (supplementPath != null && !File.Exists(supplementPath))
                        {
                            Console.WriteLine("error: 補完訳ファイルが見つかりません: " + supplementPath);
                            return 1;
                        }
                        if (path != null)
                        {
                            supplement = SupplementFile.Load(path);
                            foreach (string warning in supplement.Warnings) Console.WriteLine("warning: " + warning);
                        }
                    }
                    var result = engine.Apply(supplement);
                    Console.WriteLine("applied: official=" + result.OfficialLines + " supplement=" + result.SupplementLines
                        + " unknown-keys=" + result.UnknownKeys + " total=" + result.TotalKeys
                        + " defaults=" + result.DefaultsReplaced + " glyph-fixes=" + result.GlyphFixed);
                    return PrintStatus(engine.GetStatus());
                }

                engine.Restore();
                Console.WriteLine("restored");
                return PrintStatus(engine.GetStatus());
            }
            catch (PatchException ex)
            {
                Console.WriteLine("error: " + ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Log.Error("コマンドラインの実行に失敗しました。", ex);
                Console.WriteLine("error: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static int PrintStatus(GameStatus status)
        {
            Console.WriteLine("state: " + status.State);
            if (status.Build != null) Console.WriteLine("build: " + status.Build.Label);
            Console.WriteLine("dll-patched: " + status.DllPatched + "  twig-modified: " + status.TwigModified + "  backup: " + status.BackupAvailable
                + (status.TwigForeign ? "  twig-foreign: True" : string.Empty));
            if (status.Reason != null) Console.WriteLine("reason: " + status.Reason);
            return status.State == PatchState.Unsupported ? 3 : 0;
        }

        /// <summary>使い方を表示する。<paramref name="exitCode"/> は、引数の誤りなら 1、--help などで尋ねられた場合は 0。</summary>
        private static int Usage(int exitCode = 1)
        {
            Console.WriteLine("使い方: TheRedLanternJP.exe [--status | --apply | --restore] [--game-dir <フォルダ>] [--supplement <ファイル> | --no-supplement]");
            Console.WriteLine("引数なしで起動すると画面が開きます。");
            return exitCode;
        }
    }
}
