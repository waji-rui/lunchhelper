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
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 触控配置界面：锁定时长、应急密码（6 位数字）、日志保存天数、锁屏标语，
    /// 以及「恢复默认配置」与「关于」。
    /// 数字项使用上下调节器（无需键盘），文字项依赖系统触摸键盘（配置界面未锁屏，可用）。
    /// </summary>
    internal class ConfigForm : Form
    {
        private readonly bool _debug;
        private Config _cfg;

        private NumericUpDown _numLock;
        private TextBox _txtPassword;
        private Label _lblPwdHint;
        private NumericUpDown _numRetention;
        private TextBox _txtSlogan;
        private CheckBox _chkUiAccess;

        public ConfigForm(bool debug)
        {
            _debug = debug;
            InitializeComponent();
            LoadFields();
        }

        private void InitializeComponent()
        {
            this.Text = ConfigManager.AppName + " 配置";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimizeBox = false;
            this.BackColor = Color.White;
            this.Font = new Font("Segoe UI", 14F);
            this.ClientSize = new Size(560, 560);
            this.AutoScroll = true;

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(20, 20, 20, 10),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowCount = 8;
            for (int i = 0; i < 8; i++)
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // 锁定时长
            table.Controls.Add(Label("锁定时长（秒）："), 0, 0);
            _numLock = new NumericUpDown
            {
                Minimum = 1, Maximum = 3600, Value = 10,
                Font = new Font("Segoe UI", 16F), Height = 40, Width = 200,
                TextAlign = HorizontalAlignment.Center
            };
            table.Controls.Add(_numLock, 1, 0);

            // 应急密码
            table.Controls.Add(Label("应急解锁密码："), 0, 1);
            _txtPassword = new TextBox
            {
                PasswordChar = '●', MaxLength = 12,
                Font = new Font("Segoe UI", 16F), Height = 40, Width = 240,
                TextAlign = HorizontalAlignment.Center
            };
            _txtPassword.KeyPress += (s, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                    e.Handled = true;
            };
            // 清理粘贴/输入中的非数字字符，确保始终为纯数字（KeyPress 拦不住粘贴）
            _txtPassword.TextChanged += (s, e) =>
            {
                string cleaned = Regex.Replace(_txtPassword.Text, @"\D", "");
                if (cleaned.Length > 12) cleaned = cleaned.Substring(0, 12);
                if (cleaned != _txtPassword.Text)
                {
                    int sel = _txtPassword.SelectionStart;
                    _txtPassword.Text = cleaned;
                    _txtPassword.SelectionStart = Math.Min(sel, cleaned.Length);
                }
            };
            table.Controls.Add(_txtPassword, 1, 1);

            _lblPwdHint = new Label
            {
                Text = "（4–12 位纯数字；留空则保持原密码）",
                Font = new Font("Segoe UI", 11F), ForeColor = Color.Gray,
                AutoSize = true
            };
            table.Controls.Add(_lblPwdHint, 1, 2);

            // 日志天数
            table.Controls.Add(Label("日志保存上限天数："), 0, 3);
            _numRetention = new NumericUpDown
            {
                Minimum = 0, Maximum = 3650, Value = 14,
                Font = new Font("Segoe UI", 16F), Height = 40, Width = 200,
                TextAlign = HorizontalAlignment.Center
            };
            table.Controls.Add(_numRetention, 1, 3);

            var lblRetHint = new Label
            {
                Text = "（≥0；0 表示不保存任何日志）",
                Font = new Font("Segoe UI", 11F), ForeColor = Color.Gray,
                AutoSize = true
            };
            table.Controls.Add(lblRetHint, 1, 4);

            // 标语
            table.Controls.Add(Label("锁屏标语："), 0, 5);
            _txtSlogan = new TextBox
            {
                Font = new Font("Segoe UI", 16F), Height = 40, Width = 320,
                TextAlign = HorizontalAlignment.Left
            };
            table.Controls.Add(_txtSlogan, 1, 5);

            var lblSloganHint = new Label
            {
                Text = "（留空则锁屏显示“设备已锁定”）",
                Font = new Font("Segoe UI", 11F), ForeColor = Color.Gray,
                AutoSize = true
            };
            table.Controls.Add(lblSloganHint, 1, 6);

            // UI Access 超级置顶开关
            table.Controls.Add(Label("UI Access 超级置顶："), 0, 7);
            _chkUiAccess = new CheckBox
            {
                Text = "启用（需提权/UAC 或放入 Program Files）",
                Font = new Font("Segoe UI", 13F),
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };
            table.Controls.Add(_chkUiAccess, 1, 7);

            this.Controls.Add(table);

            // 按钮区
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(20),
                Height = 70
            };
            panel.Controls.Add(BigButton("保存", Color.FromArgb(0, 120, 215), (s, e) => Save()));
            panel.Controls.Add(BigButton("恢复默认", Color.Gray, (s, e) => ResetDefault()));
            panel.Controls.Add(BigButton("关于", Color.DarkSlateGray, (s, e) => new AboutForm().ShowDialog(this)));
            panel.Controls.Add(BigButton("退出", Color.DimGray, (s, e) => this.Close()));

            this.Controls.Add(panel);
        }

        private static Label Label(string text)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 14F),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 10, 0, 0)
            };
        }

        private static Button BigButton(string text, Color back, EventHandler click)
        {
            var b = new Button
            {
                Text = text,
                Font = new Font("Segoe UI", 16F),
                Size = new Size(120, 50),
                BackColor = back,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(6)
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += click;
            return b;
        }

        private void LoadFields()
        {
            _cfg = ConfigManager.Load();
            _numLock.Value = _cfg.LockSeconds;
            _numRetention.Value = _cfg.LogRetentionDays;
            _txtSlogan.Text = _cfg.Slogan;
            _chkUiAccess.Checked = _cfg.EnableUiAccess;
            _txtPassword.Text = ""; // 出于安全，密码框始终留空（不回显）
        }

        private void Save()
        {
            try
            {
                var cfg = ConfigManager.Load();
                cfg.LockSeconds = (int)_numLock.Value;
                cfg.LogRetentionDays = (int)_numRetention.Value;
                cfg.Slogan = _txtSlogan.Text ?? "";
                cfg.EnableUiAccess = _chkUiAccess.Checked;

                string pwd = _txtPassword.Text;
                if (pwd.Length > 0)
                {
                    if (!Regex.IsMatch(pwd, @"^\d{4,12}$"))
                    {
                        MessageBox.Show("应急解锁密码必须为 4–12 位纯数字。", "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    ConfigManager.ComputePasswordHash(pwd, out var hash, out var salt);
                    cfg.PasswordHash = hash;
                    cfg.PasswordSalt = salt;
                    cfg.PasswordIterations = ConfigManager.DefaultIterations;
                cfg.PinLength = pwd.Length;
                }

                ConfigManager.Save(cfg);
                _cfg = cfg;
                _txtPassword.Text = "";
                Logger.Info("配置已保存");
                MessageBox.Show("配置已保存。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("保存配置异常: " + ex.Message);
                MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ResetDefault()
        {
            try
            {
                ConfigManager.ResetToDefault();
                LoadFields();
                Logger.Info("已恢复默认配置");
                MessageBox.Show("已恢复默认配置。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("恢复默认配置异常: " + ex.Message);
                MessageBox.Show("恢复默认失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
