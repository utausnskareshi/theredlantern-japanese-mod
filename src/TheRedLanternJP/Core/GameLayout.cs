namespace TheRedLanternJP.Core
{
    /// <summary>Steam版 The Red Lantern のファイル配置に関する定数。</summary>
    internal static class GameLayout
    {
        public const string AppId = "1053710";
        public const string ExeName = "TheRedLantern_sw.exe";
        public const string ProcessName = "TheRedLantern_sw";

        /// <summary>起動時の言語を決めているコードが入っているファイル。</summary>
        public const string GameAssemblyRelPath = "GameAssembly.dll";

        /// <summary>全言語の台詞・UI文言が入っているテーブル（gzip + protobuf）。</summary>
        public const string TwigRelPath = @"TheRedLantern_sw_Data\StreamingAssets\Flume\TheRedLantern_v001-Built.twig";

        /// <summary>バックアップ置き場（ゲームフォルダ直下）。</summary>
        public const string BackupDirName = "TheRedLanternJP_backup";
        public const string BackupTwigName = "TheRedLantern_v001-Built.twig.orig";
        public const string BackupInfoName = "backup.info";
    }
}
