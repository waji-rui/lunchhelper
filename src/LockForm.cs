// LunchHelper — 触控锁屏助手
// Copyright (C) 2026  LunchHelper Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 锁屏主窗体（控制模式）。
    /// 黑底白字、全屏置顶：顶部系统时间，中部大字号标语，下方倒计时与「应急解锁」按钮。
    /// 点击应急解锁进入密码界面（数字键盘）；倒计时归零无论何种状态都退出解锁。
    /// </summary>
    internal class LockForm : Form
    {
        private readonly bool _debug;
        private int _guardianPid;
        private Config _cfg;

        private int _remaining;
        private bool _inPasswordMode;
        private bool _exiting;

        private Timer _mainTimer;
        private Timer _lockoutTimer;
        private Timer _guardianTimer;

        private Label _lblTime;
        private Label _lblSlogan;
        private Label _lblCountdown;
        private Button _btnAction;
        private Panel _centerPanel;
        private Panel _pnlPassword;
        private Label _lblPrompt;
        private NumericPad _pad;

        public LockForm(bool debug, int guardianPid)
        {
            _debug = debug;
            _guardianPid = guardianPid;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.BackColor = Color.Black;
            this.ForeColor = Color.White;
            this.FormBorderStyle = FormBorderStyle.None;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.WindowState = FormWindowState.Normal;
            this.Activated += (s, e) => { this.TopMost = true; };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.Black,
                Padding = new Padding(0)
            };
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));

            _lblTime = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 28F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Black
            };
            table.Controls.Add(_lblTime, 0, 0);

            _centerPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

            _lblSlogan = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 48F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Black
            };
            _centerPanel.Controls.Add(_lblSlogan);

            _pnlPassword = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
            _lblPrompt = new Label
            {
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 24F),
                ForeColor = Color.White,
                BackColor = Color.Black,
                Text = "请输入应急解锁密码以解锁"
            };
            _pad = new NumericPad { Dock = DockStyle.Fill };
            _pad.CodeSubmitted += OnCodeSubmitted;
            _pnlPassword.Controls.Add(_pad);
            _pnlPassword.Controls.Add(_lblPrompt);
            _centerPanel.Controls.Add(_pnlPassword);

            table.Controls.Add(_centerPanel, 0, 1);

            _lblCountdown = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 28F),
                ForeColor = Color.White,
                BackColor = Color.Black
            };
            table.Controls.Add(_lblCountdown, 0, 2);

            _btnAction = new Button
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 24F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Text = "应急解锁"
            };
            _btnAction.FlatAppearance.BorderSize = 0;
            _btnAction.Click += OnActionClick;
            table.Controls.Add(_btnAction, 0, 3);

            this.Controls.Add(table);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 覆盖所有显示器
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            foreach (var s in Screen.AllScreens)
                bounds = Rectangle.Union(bounds, s.Bounds);
            this.Bounds = bounds;

            _cfg = ConfigManager.Load();
            _remaining = _cfg.LockSeconds;
            LockSignal.Clear();
            AntiTamper.Enable();

            _lblSlogan.Text = string.IsNullOrWhiteSpace(_cfg.Slogan) ? "设备已锁定" : _cfg.Slogan;
            _lblCountdown.Text = _remaining + "秒后将自动解锁屏幕";
            _lblTime.Text = DateTime.Now.ToString("HH:mm:ss");

            _mainTimer = new Timer { Interval = 1000 };
            _mainTimer.Tick += OnMainTick;
            _mainTimer.Start();

            _lockoutTimer = new Timer { Interval = 5000 };
            _lockoutTimer.Tick += OnLockoutTick;

            _guardianTimer = new Timer { Interval = 3000 };
            _guardianTimer.Tick += OnGuardianTick;
            _guardianTimer.Start();

            Logger.Info("锁屏界面已加载，锁定 " + _remaining + " 秒");
        }

        private void OnMainTick(object sender, EventArgs e)
        {
            if (_exiting) return;
            _lblTime.Text = DateTime.Now.ToString("HH:mm:ss");

            _remaining--;
            if (_remaining <= 0)
            {
                _lblCountdown.Text = "0秒后将自动解锁屏幕";
                Logger.Info("倒计时归零，自动解锁");
                ExitLock();
                return;
            }
            _lblCountdown.Text = _remaining + "秒后将自动解锁屏幕";
        }

        private void OnActionClick(object sender, EventArgs e)
        {
            if (!_inPasswordMode)
                EnterPasswordMode();
            else
                ExitPasswordMode();
        }

        private void EnterPasswordMode()
        {
            _inPasswordMode = true;
            _lblSlogan.Visible = false;
            _pnlPassword.Visible = true;
            _btnAction.Text = "返回";
            _lblPrompt.Text = "请输入应急解锁密码以解锁";
            _pad.Clear();
            _pad.SetLocked(false);
            Logger.Debug("进入密码解锁界面");
        }

        private void ExitPasswordMode()
        {
            _inPasswordMode = false;
            _pnlPassword.Visible = false;
            _lblSlogan.Visible = true;
            _btnAction.Text = "应急解锁";
            _pad.Clear();
            Logger.Debug("返回主锁屏界面");
        }

        private void OnCodeSubmitted(string code)
        {
            if (_exiting || _pad.Locked) return;

            // Verify on a background thread so the UI never freezes during the
            // (still non-trivial) PBKDF2 computation. Keep the 6 dots visible and
            // show a "verifying" hint until the result comes back on the UI thread.
            _pad.SetLocked(true);
            _lblPrompt.Text = "校验中…";

            string hash = _cfg.PasswordHash;
            string salt = _cfg.PasswordSalt;
            Task.Run(() =>
            {
                try
                {
                    bool ok = ConfigManager.VerifyPassword(code, hash, salt, _cfg.PasswordIterations);
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke(new Action(() =>
                    {
                        if (_exiting) return;
                        if (ok)
                        {
                            Logger.Info("应急密码正确，立即解锁");
                            ExitLock();
                            return;
                        }
                        _lblPrompt.Text = "密码错误，请重试";
                        _pad.Clear();
                        StartLockout();
                    }));
                }
                catch { }
            });
        }

        /// <summary>防暴破：错误后锁定键盘 5 秒，每 5 秒限一次尝试。</summary>
        private void StartLockout()
        {
            _pad.SetLocked(true);
            _lblPrompt.Text = "密码错误，请重试（5 秒后可再次尝试）";
            _lockoutTimer.Stop();
            _lockoutTimer.Start();
            Logger.Warn("应急密码错误，键盘锁定 5 秒");
        }

        private void OnLockoutTick(object sender, EventArgs e)
        {
            _lockoutTimer.Stop();
            if (_exiting) return;
            _pad.SetLocked(false);
            _lblPrompt.Text = "请输入应急解锁密码以解锁";
        }

        private void OnGuardianTick(object sender, EventArgs e)
        {
            if (_exiting || _guardianPid <= 0) return;
            if (!ProcessExists(_guardianPid))
            {
                Logger.Warn("守护进程已退出，尝试重新拉起");
                _guardianPid = Program.LaunchGuardian();
            }
        }

        private static bool ProcessExists(int pid)
        {
            try { return Process.GetProcessById(pid) != null; }
            catch { return false; }
        }

        /// <summary>正常解锁退出：写入解锁标记，清理并关闭。</summary>
        private void ExitLock()
        {
            if (_exiting) return;
            _exiting = true;

            try { _mainTimer?.Stop(); } catch { }
            try { _lockoutTimer?.Stop(); } catch { }
            try { _guardianTimer?.Stop(); } catch { }

            LockSignal.SetUnlocked();
            AntiTamper.Cleanup();

            try { this.Close(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            // 防御性清理（ExitLock 已处理，此处幂等）
            LockSignal.SetUnlocked();
            AntiTamper.Cleanup();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // Timer 未加入组件容器，需手动释放避免原生资源泄漏
            try { _mainTimer?.Dispose(); } catch { }
            try { _lockoutTimer?.Dispose(); } catch { }
            try { _guardianTimer?.Dispose(); } catch { }
        }
    }
}
