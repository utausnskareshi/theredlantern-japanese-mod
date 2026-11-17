using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace TheRedLanternJP.Core
{
    /// <summary>見つかったゲームのインストール情報。</summary>
    public sealed class GameInstall
    {
        public string InstallDir { get; set; }

        /// <summary>Steam のビルドID（appmanifest から。不明なら null）。</summary>
        public string BuildId { get; set; }

        /// <summary>appmanifest の StateFlags（4 = インストール完了。不明なら -1）。</summary>
        public int StateFlags { get; set; } = -1;

        public string ManifestPath { get; set; }

        /// <summary>Steam がこのゲームを更新中・更新待ちにしている可能性がある。</summary>
        public bool UpdatePending
        {
            get { return StateFlags >= 0 && StateFlags != 4; }
        }
    }

    /// <summary>Steam のレジストリ情報とライブラリ定義から、The Red Lantern のインストール先を探す。</summary>
    public static class SteamLocator
    {
        /// <summary>Steam が更新中・更新待ちのときに、書き込みを見送る理由として利用者に見せる文面。</summary>
        public const string UpdatePendingMessage = "Steamがこのゲームを更新中、または更新待ちです。更新が終わってから、もう一度お試しください。";

        /// <summary>インストールされていなければ null。</summary>
        public static GameInstall Find()
        {
            return FindInSteamRoots(GetSteamRootCandidates()) ?? FindViaUninstallKey();
        }

        internal static List<string> GetSteamRootCandidates()
        {
            var roots = new List<string>();
            // HKLM の InstallPath は大文字小文字が実際のフォルダ名どおりなので先に見る（HKCU の SteamPath は小文字で入っている）
            AddCandidate(roots, ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"));
            AddCandidate(roots, ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"));
            AddCandidate(roots, ReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"));
            foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                string dir = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(dir)) AddCandidate(roots, Path.Combine(dir, "Steam"));
            }
            return roots;
        }

        internal static GameInstall FindInSteamRoots(IEnumerable<string> steamRoots)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in steamRoots)
            {
                foreach (string library in GetLibraries(root))
                {
                    if (!seen.Add(library)) continue;
                    var install = ReadAppManifest(library);
                    if (install != null) return install;
                }
            }
            return null;
        }

        /// <summary>Steam 本体のフォルダと、libraryfolders.vdf に書かれた追加ライブラリを返す。</summary>
        internal static List<string> GetLibraries(string steamRoot)
        {
            var libraries = new List<string>();
            if (string.IsNullOrEmpty(steamRoot) || !Directory.Exists(steamRoot)) return libraries;
            AddCandidate(libraries, steamRoot);
            foreach (string vdf in new[]
            {
                Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
            })
            {
                try
                {
                    if (!File.Exists(vdf)) continue;
                    foreach (string path in ParseLibraryFolders(File.ReadAllText(vdf))) AddCandidate(libraries, path);
                }
                catch (Exception ex)
                {
                    Log.Error("libraryfolders.vdf を読めませんでした: " + vdf, ex);
                }
            }
            return libraries;
        }

        /// <summary>libraryfolders.vdf の中身からライブラリのパス一覧を取り出す（新旧どちらの形式にも対応）。</summary>
        internal static List<string> ParseLibraryFolders(string vdfText)
        {
            var result = new List<string>();
            var root = VdfNode.Parse(vdfText);
            var folders = root.Child("libraryfolders");
            if (folders == null || folders.Children == null) return result;
            foreach (var entry in folders.Children)
            {
                int index;
                if (!int.TryParse(entry.Key, out index)) continue;
                // 新形式: "0" { "path" "C:\\..." }   旧形式: "1" "D:\\SteamLibrary"
                string path = entry.Children != null ? entry.ValueOf("path") : entry.Value;
                if (!string.IsNullOrWhiteSpace(path)) result.Add(path);
            }
            return result;
        }

        /// <summary>ライブラリ内の appmanifest を読み、ゲームフォルダが実在すればその情報を返す。</summary>
        internal static GameInstall ReadAppManifest(string libraryPath)
        {
            try
            {
                string manifest = Path.Combine(libraryPath, "steamapps", "appmanifest_" + GameLayout.AppId + ".acf");
                if (!File.Exists(manifest)) return null;
                var state = VdfNode.Parse(File.ReadAllText(manifest)).Child("AppState");
                if (state == null) return null;
                string installDirName = state.ValueOf("installdir");
                if (string.IsNullOrWhiteSpace(installDirName)) return null;
                string dir = Path.Combine(libraryPath, "steamapps", "common", installDirName);
                if (!LooksLikeGameDir(dir)) return null;
                int flags;
                if (!int.TryParse(state.ValueOf("StateFlags"), out flags)) flags = -1;
                return new GameInstall
                {
                    InstallDir = Path.GetFullPath(dir),
                    BuildId = state.ValueOf("buildid"),
                    StateFlags = flags,
                    ManifestPath = manifest,
                };
            }
            catch (Exception ex)
            {
                Log.Error("appmanifest を読めませんでした: " + libraryPath, ex);
                return null;
            }
        }

        internal static bool LooksLikeGameDir(string dir)
        {
            return Directory.Exists(dir)
                && File.Exists(Path.Combine(dir, GameLayout.ExeName))
                && File.Exists(Path.Combine(dir, GameLayout.GameAssemblyRelPath));
        }

        /// <summary>Steam がゲームごとに登録する「アンインストール情報」からも探す（ライブラリ定義が読めない場合の保険）。</summary>
        private static GameInstall FindViaUninstallKey()
        {
            const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + GameLayout.AppId;
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string dir = ReadRegistryString(RegistryHive.LocalMachine, view, key, "InstallLocation");
                if (!string.IsNullOrWhiteSpace(dir) && LooksLikeGameDir(dir))
                {
                    return new GameInstall { InstallDir = Path.GetFullPath(dir) };
                }
            }
            return null;
        }

        private static string ReadRegistryString(RegistryHive hive, RegistryView view, string subKey, string valueName)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (var key = baseKey.OpenSubKey(subKey))
                {
                    return key == null ? null : key.GetValue(valueName) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 区切り文字を揃えた絶対パスにして、末尾の <c>\</c> を取り除く。不正なパスは null。
        /// ドライブ直下（<c>D:\</c>）の <c>\</c> は残す。<c>D:</c> にしてしまうと、
        /// <c>Path.Combine("D:", "steamapps")</c> がカレントフォルダ基準の相対パスになるため。
        /// </summary>
        internal static string NormalizeDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string full = Path.GetFullPath(path.Replace('/', '\\'));
                string root = Path.GetPathRoot(full) ?? string.Empty;
                return full.Length > root.Length ? full.TrimEnd('\\') : full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AddCandidate(List<string> list, string path)
        {
            string normalized = NormalizeDir(path);
            if (normalized == null) return;
            foreach (string existing in list)
            {
                if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(normalized);
        }
    }
}
