using System;
using System.Windows.Forms;
using TheRedLanternJP.Core;
using TheRedLanternJP.UI;

namespace TheRedLanternJP
{
    internal static class Program
    {
        /// <summary>終了コード: 0 = 正常, 1 = 失敗, 2 = ゲーム未インストール, 3 = 未対応バージョン。</summary>
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0) return CommandLine.Run(args);

            // 想定外の例外は、既定のエラー画面ではなくログに残して日本語で知らせる
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportUnexpected(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("想定外のエラーで終了します。", e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            GameInstall install = null;
            try
            {
                install = SteamLocator.Find();
            }
            catch (Exception ex)
            {
                Log.Error("インストール先の検出に失敗しました。", ex);
            }

            if (install == null)
            {
                Log.Info("Steam版 The Red Lantern が見つからないため終了します。");
                MessageBox.Show(
                    "Steam版「The Red Lantern」がインストールされていないため、日本語化パッチを適用できません。"
                    + Environment.NewLine + Environment.NewLine
                    + "Steamでゲームをインストールしてから、もう一度このツールを起動してください。",
                    AppInfo.Title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 2;
            }

            Application.Run(new MainForm(install));
            return 0;
        }

        private static void ReportUnexpected(Exception ex)
        {
            Log.Error("想定外のエラーが発生しました。", ex);
            MessageBox.Show(
                "予期しないエラーが発生しました。" + Environment.NewLine + (ex == null ? string.Empty : ex.Message)
                + Environment.NewLine + Environment.NewLine + "詳細はログに記録しました: " + Log.FilePath,
                AppInfo.Title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
