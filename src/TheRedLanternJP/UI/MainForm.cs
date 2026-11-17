using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using TheRedLanternJP.Core;

namespace TheRedLanternJP.UI
{
    /// <summary>
    /// メイン画面。ボタンは1つだけで、ゲームの状態に応じて「日本語化する」と「元に戻す」が切り替わる。
    /// 状態は毎回ファイルの中身から判定するので、Steamの整合性チェックなどで元に戻った場合にも追従する。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private static readonly Color ColorOk = Color.FromArgb(0, 122, 61);
        private static readonly Color ColorWarn = Color.FromArgb(176, 52, 36);
        private static readonly Color ColorMuted = Color.FromArgb(96, 96, 96);

        private readonly GameInstall _install;
        private readonly PatchEngine _engine;
        private readonly Supplement _supplement;
        private readonly string _supplementProblem;

        /// <summary>補完訳ファイルを使えない理由（起動時にメッセージ欄へ出す）。問題が無ければ null。</summary>
        private readonly string _supplementNote;

        private readonly Label _stateValue;
        private readonly Label _supplementValue;
        private readonly Label _message;
        private readonly Button _action;
        private readonly ProgressBar _progress;

        private GameStatus _status;
        private bool _busy;
        private DateTime _lastRefresh = DateTime.MinValue;

        /// <summary>実行中（または最後に行った）状態確認。同時に走らせるのは1つだけにする。</summary>
        private Task _refresh = Task.CompletedTask;

        /// <summary>メッセージ欄に「未対応」の理由を表示している間だけ true。</summary>
        private bool _reasonShown;

        /// <summary>
        /// ボタンが押せるようになったら、キーボードのフォーカスをボタンに移す（起動直後と、処理の後）。
        /// ボタンは状態の確認中や処理中は押せないため、その間にフォーカスが下のリンクへ移ってしまい、
        /// そのまま Enter キーを押すとフォルダが開いてしまう。
        /// </summary>
        private bool _focusActionWhenReady = true;

        public MainForm(GameInstall install)
        {
            _install = install;
            _engine = new PatchEngine(install.InstallDir);

            // 補完訳ファイルはツールと同じ場所から探す。無ければ公式の日本語訳だけを有効にする。
            try
            {
                string path = SupplementFile.FindDefault(AppDomain.CurrentDomain.BaseDirectory);
                if (path != null)
                {
                    _supplement = SupplementFile.Load(path);
                    foreach (string warning in _supplement.Warnings) Log.Info("補完訳ファイル: " + warning);
                    if (_supplement.Entries.Count == 0)
                    {
                        _supplement = null;
                        _supplementProblem = "補完訳ファイルに有効な行がありません（公式の日本語訳のみ有効にします）";
                    }
                }
            }
            catch (PatchException ex)
            {
                // 文字コードが UTF-8 でない、など。文字化けしたまま適用しないよう、補完訳は使わない
                Log.Error("補完訳ファイルを使えません: " + ex.Message);
                _supplement = null;
                _supplementProblem = "補完訳ファイルを使えません（公式の日本語訳のみ有効にします）";
                _supplementNote = ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("補完訳ファイルを読み込めませんでした。", ex);
                _supplement = null;
                _supplementProblem = "補完訳ファイルを読み込めませんでした（公式の日本語訳のみ有効にします）";
            }

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = AppInfo.Title;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 440);
            Padding = new Padding(20, 16, 20, 12);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // アイコンが取れなくても既定のアイコンで動く
            }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(new Label
            {
                Text = "The Red Lantern 日本語化パッチ",
                Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2),
            }, 0, 0);
            root.Controls.Add(new Label
            {
                Text = "非公式ツール ・ Steam版専用",
                ForeColor = ColorMuted,
                AutoSize = true,
                Margin = new Padding(2, 0, 0, 0),
            }, 0, 1);

            var info = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 14, 0, 10),
            };
            info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            var pathValue = AddInfoRow(info, "インストール先", install.InstallDir);
            pathValue.AutoSize = false;
            pathValue.AutoEllipsis = true;
            pathValue.Dock = DockStyle.Fill;
            pathValue.Height = Font.Height + 2;
            new ToolTip().SetToolTip(pathValue, install.InstallDir);
            AddInfoRow(info, "SteamのビルドID", string.IsNullOrEmpty(install.BuildId) ? "不明" : install.BuildId);
            _stateValue = AddInfoRow(info, "現在の状態", "確認しています…");
            _stateValue.Font = new Font(Font, FontStyle.Bold);
            _supplementValue = AddInfoRow(info, "補完訳", DescribeSupplement());
            root.Controls.Add(info, 0, 2);

            _action = new Button
            {
                Name = "btnAction",
                Text = "日本語化する",
                Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 54,
                Margin = new Padding(0, 4, 0, 6),
                Enabled = false,
            };
            _action.Click += OnActionClick;
            root.Controls.Add(_action, 0, 3);

            _progress = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 6,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 25,
                Visible = false,
                Margin = new Padding(0, 0, 0, 6),
            };
            root.Controls.Add(_progress, 0, 4);

            _message = new Label
            {
                Name = "lblMessage",
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 4, 0, 4),
            };
            root.Controls.Add(_message, 0, 5);

            var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            var openFolder = new LinkLabel { Text = "ゲームのフォルダを開く", AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            openFolder.LinkClicked += (s, e) => OpenInShell("explorer.exe", "\"" + _install.InstallDir + "\"");
            var openLog = new LinkLabel { Text = "ログを開く", AutoSize = true, Margin = new Padding(0) };
            openLog.LinkClicked += (s, e) => OpenLog();
            footer.Controls.Add(openFolder, 0, 0);
            footer.Controls.Add(openLog, 1, 0);
            footer.Controls.Add(new Label
            {
                Text = "バージョン " + AppInfo.Version,
                ForeColor = ColorMuted,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0),
            }, 2, 0);
            root.Controls.Add(footer, 0, 6);

            Controls.Add(root);
            ResumeLayout(false);
            PerformLayout();
            if (_supplementNote != null) SetMessage(_supplementNote, ColorWarn);

            Shown += async (s, e) => await RefreshStatusAsync();
            Activated += async (s, e) =>
            {
                // 別の画面（Steamなど）から戻ってきたときに状態を取り直す
                if (!_busy && (DateTime.UtcNow - _lastRefresh).TotalSeconds > 3) await RefreshStatusAsync();
            };
            FormClosing += (s, e) =>
            {
                if (_busy) e.Cancel = true; // 書き込みの途中では閉じさせない
            };
        }

        private Label AddInfoRow(TableLayoutPanel table, string caption, string value)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label
            {
                Text = caption,
                ForeColor = ColorMuted,
                AutoSize = true,
                Margin = new Padding(0, 3, 18, 3),
            }, 0, row);
            var valueLabel = new Label { Text = value, AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
            table.Controls.Add(valueLabel, 1, row);
            return valueLabel;
        }

        private string DescribeSupplement()
        {
            if (_supplementProblem != null) return _supplementProblem;
            if (_supplement == null) return "ファイルがありません（公式の日本語訳のみ有効にします）";
            return _supplement.Entries.Count.ToString("N0") + " 行（公式の日本語訳が無い行に追記します）";
        }

        /// <summary>
        /// ゲームの状態を取り直して画面に反映する。確認が実行中なら新しく始めず、それが終わるのを待つ
        /// （画面を開いた直後は Activated と Shown の両方から呼ばれる。約46MBのファイルを二重に読まない）。
        /// </summary>
        private Task RefreshStatusAsync()
        {
            if (_refresh.IsCompleted) _refresh = RefreshStatusCoreAsync();
            return _refresh;
        }

        private async Task RefreshStatusCoreAsync()
        {
            _lastRefresh = DateTime.UtcNow;
            GameStatus status;
            try
            {
                status = await Task.Run(() => _engine.GetStatus());
            }
            catch (Exception ex)
            {
                Log.Error("状態の確認に失敗しました。", ex);
                status = new GameStatus
                {
                    State = PatchState.Unsupported,
                    Reason = "ゲームのファイルを確認できませんでした。" + Environment.NewLine + ex.Message,
                };
            }
            if (IsDisposed) return;
            _status = status;
            ShowStatus();
        }

        private void ShowStatus()
        {
            switch (_status.State)
            {
                case PatchState.NotPatched:
                    _stateValue.Text = "日本語化されていません（英語）";
                    _stateValue.ForeColor = SystemColors.ControlText;
                    _action.Text = "日本語化する";
                    break;
                case PatchState.Patched:
                    _stateValue.Text = "日本語化済み";
                    _stateValue.ForeColor = ColorOk;
                    _action.Text = "元に戻す";
                    break;
                case PatchState.PatchedBackupMissing:
                    _stateValue.Text = _status.TwigForeign
                        ? "日本語化済み（台詞データはこのツールが書いた内容ではありません）"
                        : "日本語化済み（台詞データのバックアップが見つかりません）";
                    _stateValue.ForeColor = ColorWarn;
                    _action.Text = "元に戻す";
                    break;
                default:
                    _stateValue.Text = "未対応のバージョン、または変更されたファイル";
                    _stateValue.ForeColor = ColorWarn;
                    _action.Text = "日本語化する";
                    break;
            }

            if (_status.State == PatchState.Unsupported)
            {
                SetMessage(_status.Reason + Environment.NewLine + Environment.NewLine + PatchEngine.VerifyHint, ColorWarn);
                _reasonShown = true;
            }
            else if (_reasonShown)
            {
                // 整合性の確認などで対応済みの状態に戻ったら、「未対応」の説明は消す
                SetMessage(string.Empty, SystemColors.ControlText);
            }
            UpdateActionEnabled();
        }

        private async void OnActionClick(object sender, EventArgs e)
        {
            if (_busy || _status == null) return;
            PatchState shown = _status.State; // ボタンを押した時点で画面に出ていた状態
            SetBusy(true);
            string error = null;
            try
            {
                // 別の画面から戻った直後は状態を確認している最中のことがある。同じファイルを同時に読み書きしないよう、終わるのを待つ
                await _refresh;
                if (_status.State != shown)
                {
                    if (_status.State != PatchState.Unsupported)
                    {
                        SetMessage("ゲームの状態が変わっていたため、表示を更新しました。内容を確認して、もう一度ボタンを押してください。", SystemColors.ControlText);
                    }
                    return;
                }

                bool apply = shown == PatchState.NotPatched;
                SetMessage(apply ? "日本語化しています…" : "元に戻しています…", SystemColors.ControlText);
                if (apply)
                {
                    var result = await Task.Run(() =>
                    {
                        // Steamが更新中のファイルには書き込まない
                        var current = SteamLocator.Find();
                        if (current != null && current.UpdatePending)
                        {
                            throw new PatchException(SteamLocator.UpdatePendingMessage);
                        }
                        return _engine.Apply(_supplement);
                    });
                    string detail = result.SupplementLines > 0
                        ? "公式の日本語訳 " + result.OfficialLines.ToString("N0") + " 行と、補完訳 " + result.SupplementLines.ToString("N0") + " 行を有効にしました。"
                        : "公式の日本語訳 " + result.OfficialLines.ToString("N0") + " 行を有効にしました。公式訳の無い行は英語のまま表示されます。";
                    SetMessage("日本語化が完了しました。Steamからゲームを起動してください。" + Environment.NewLine + detail, ColorOk);
                }
                else
                {
                    await Task.Run(() => _engine.Restore());
                    SetMessage("パッチを当てる前の状態に戻しました。", ColorOk);
                }
            }
            catch (PatchException ex)
            {
                error = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error("書き込みが拒否されました。", ex);
                error = "ゲームのファイルに書き込めませんでした。このツールを右クリックして「管理者として実行」でお試しください。";
            }
            catch (IOException ex)
            {
                Log.Error("ファイルの読み書きに失敗しました。", ex);
                error = "ゲームのファイルを読み書きできませんでした。ゲームを終了してから、もう一度お試しください。"
                    + Environment.NewLine + "（" + ex.Message + "）";
            }
            catch (Exception ex)
            {
                Log.Error("予期しないエラーが発生しました。", ex);
                error = "予期しないエラーが発生しました。" + Environment.NewLine + ex.Message;
            }
            finally
            {
                // 結果を画面に反映してから「処理中」を解除する（それまでは画面を閉じられず、別の状態確認も割り込まない）
                await RefreshStatusAsync();
                _focusActionWhenReady = true;
                if (!IsDisposed) SetBusy(false);
            }

            // エラーのダイアログは、処理中の表示を消してから出す
            if (error != null && !IsDisposed) ShowError(error);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _progress.Visible = busy;
            UseWaitCursor = busy;
            UpdateActionEnabled();
        }

        /// <summary>ボタンを押せるのは、処理中でなく、状態が確認できていて、対応しているファイルのときだけ。</summary>
        private void UpdateActionEnabled()
        {
            _action.Enabled = !_busy && _status != null && _status.State != PatchState.Unsupported;
            if (_action.Enabled && _focusActionWhenReady)
            {
                _focusActionWhenReady = false;
                ActiveControl = _action;
            }
        }

        private void SetMessage(string text, Color color)
        {
            _message.ForeColor = color;
            _message.Text = text;
            _reasonShown = false;
        }

        private void ShowError(string text)
        {
            SetMessage(text, ColorWarn);
            MessageBox.Show(this, text, AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OpenLog()
        {
            if (File.Exists(Log.FilePath)) OpenInShell(Log.FilePath, null);
            else MessageBox.Show(this, "ログはまだありません。", AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenInShell(string fileName, string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(fileName, arguments ?? string.Empty) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("開けませんでした: " + fileName, ex);
            }
        }
    }
}
