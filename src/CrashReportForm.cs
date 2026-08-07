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
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 崩溃报告弹窗：仿 ClassIsland 风格，集中展示未捕获异常信息并提供复制/反馈/忽略/退出/重启操作。
    /// 仅在发生未处理异常时弹出，平时不实例化。
    /// </summary>
    internal partial class CrashReportForm : Form
    {
        private readonly Exception _exception;
        private readonly bool _critical;

        public CrashReportForm(Exception ex, bool critical)
        {
            _exception = ex;
            _critical = critical;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // 基础窗体：深色内容、原生标题栏（保证崩溃弹窗本身最稳健，不自绘标题栏以免自身失败）
            Text = "LunchHelper 已崩溃";
            Size = new Size(760, 540);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            BackColor = Color.FromArgb(0x2D, 0x2D, 0x30);
            ForeColor = Color.White;
            Padding = new Padding(0);

            // 标题
            var title = new Label
            {
                Text = "崩溃啦！（T_T）",
                Font = new Font("Segoe UI", 20F, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(24, 18)
            };
            Controls.Add(title);

            // 红色感叹号图标（自绘，避免依赖外部资源文件）
            var iconPanel = new Panel
            {
                Size = new Size(44, 44),
                Location = new Point(24, 68),
                BackColor = Color.Transparent
            };
            iconPanel.Paint += (s, e) => DrawExclamation(e.Graphics);
            Controls.Add(iconPanel);

            // 说明文本
            var desc = new Label
            {
                Text = "LunchHelper 碰到了严重错误而无法继续运行。您可以保存下方的错误信息并向他人寻求帮助。如果您认为这是由软件本身的错误所致，请点击下方【反馈问题】按钮。",
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0),
                Location = new Point(80, 68),
                Size = new Size(640, 56),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(desc);

            // 关键异常提示条（默认折叠）
            var criticalPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 0,
                BackColor = Color.FromArgb(0xC5, 0x1E, 0x1E),
                Padding = new Padding(16, 8, 16, 8)
            };
            var criticalLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F),
                Text = "这个异常是无法被忽略的关键异常，您只能重启或退出应用。很抱歉对您的使用造成不便。",
                TextAlign = ContentAlignment.MiddleLeft
            };
            criticalPanel.Controls.Add(criticalLabel);
            Controls.Add(criticalPanel);

            // 异常详情文本框
            var detailBox = new TextBox
            {
                Name = "detailBox",
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E),
                ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0),
                Font = new Font("Consolas", 9F),
                Text = BuildDetail(),
                Location = new Point(24, 136),
                Size = new Size(704, 272),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(detailBox);

            // 底部按钮面板
            var btnPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 64,
                BackColor = Color.FromArgb(0x2D, 0x2D, 0x30),
                Padding = new Padding(16, 12, 16, 12)
            };
            Controls.Add(btnPanel);

            // 复制
            var copyBtn = CreateFlatButton("复制", Color.FromArgb(0x3C, 0x3C, 0x41));
            copyBtn.Location = new Point(16, 12);
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(detailBox.Text);
                    copyBtn.Text = "已复制";
                }
                catch
                {
                    copyBtn.Text = "复制失败";
                }
            };
            btnPanel.Controls.Add(copyBtn);

            // 反馈问题
            var feedbackBtn = CreateFlatButton("反馈问题", Color.FromArgb(0x3C, 0x3C, 0x41));
            feedbackBtn.Location = new Point(118, 12);
            feedbackBtn.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/creaconception/lunchhelper/issues",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            btnPanel.Controls.Add(feedbackBtn);

            // 忽略错误（非关键异常可用）
            var ignoreBtn = CreateFlatButton("忽略错误", Color.FromArgb(0x3C, 0x3C, 0x41));
            ignoreBtn.Location = new Point(390, 12);
            ignoreBtn.DialogResult = DialogResult.Ignore;
            btnPanel.Controls.Add(ignoreBtn);

            // 调试（关键异常时替换“忽略错误”）
            var debugBtn = CreateFlatButton("调试", Color.FromArgb(0x3C, 0x3C, 0x41));
            debugBtn.Location = new Point(390, 12);
            debugBtn.Visible = false;
            debugBtn.Click += (s, e) =>
            {
                try { Debugger.Launch(); }
                catch { }
                DialogResult = DialogResult.No;
                Close();
            };
            btnPanel.Controls.Add(debugBtn);

            // 退出应用
            var exitBtn = CreateFlatButton("退出应用", Color.FromArgb(0x3C, 0x3C, 0x41));
            exitBtn.Location = new Point(502, 12);
            exitBtn.DialogResult = DialogResult.Abort;
            btnPanel.Controls.Add(exitBtn);

            // 重启应用
            var restartBtn = CreateFlatButton("重启应用", Color.FromArgb(0x00, 0x78, 0xD4));
            restartBtn.Location = new Point(614, 12);
            restartBtn.DialogResult = DialogResult.Retry;
            btnPanel.Controls.Add(restartBtn);

            if (_critical)
            {
                ignoreBtn.Visible = false;
                debugBtn.Visible = true;
                criticalPanel.Height = 40;
                AcceptButton = restartBtn;
            }
            else
            {
                AcceptButton = ignoreBtn;
            }
            CancelButton = exitBtn;
        }

        private static Button CreateFlatButton(string text, Color backColor)
        {
            return new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.White,
                Size = new Size(96, 32),
                Font = new Font("Segoe UI", 9F),
                FlatAppearance = { BorderSize = 0 }
            };
        }

        private string BuildDetail()
        {
            var sb = new StringBuilder();
            if (_exception != null)
            {
                sb.AppendLine(_exception.GetType().FullName + ": " + _exception.Message);
                if (!string.IsNullOrEmpty(_exception.StackTrace))
                    sb.AppendLine(_exception.StackTrace);
                Exception inner = _exception.InnerException;
                while (inner != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Inner Exception ---");
                    sb.AppendLine(inner.GetType().FullName + ": " + inner.Message);
                    if (!string.IsNullOrEmpty(inner.StackTrace))
                        sb.AppendLine(inner.StackTrace);
                    inner = inner.InnerException;
                }
            }
            else
            {
                sb.AppendLine("未知异常");
            }
            sb.AppendLine();
            sb.AppendLine("OS: " + Environment.OSVersion);
            sb.AppendLine("Version: " + ConfigManager.Version);
            sb.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            return sb.ToString();
        }

        private void DrawExclamation(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Color.FromArgb(0xF0, 0x3C, 0x3C)))
                g.FillEllipse(brush, 0, 0, 44, 44);
            using (var pen = new Pen(Color.White, 3))
            {
                g.DrawLine(pen, 22, 10, 22, 28);
                g.FillEllipse(Brushes.White, 19, 32, 6, 6);
            }
        }
    }
}
