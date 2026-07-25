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
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 关于窗口：展示软件名称、版本、说明与许可证信息。
    /// </summary>
    internal class AboutForm : Form
    {
        public AboutForm()
        {
            this.Text = "关于 " + ConfigManager.AppName;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new System.Drawing.Size(420, 300);
            this.BackColor = System.Drawing.Color.White;

            var lbl = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                Font = new System.Drawing.Font("Segoe UI", 13F),
                TextAlign = System.Drawing.ContentAlignment.TopLeft,
                Text =
                    ConfigManager.AppName + "  v" + ConfigManager.Version + "\n\n" +
                    "一个面向纯触控设备的锁屏小工具：到点（或手动）锁定屏幕，\n" +
                    "倒计时结束后自动解锁；支持应急密码解锁与防撬锁。\n\n" +
                    "作者：LunchHelper Contributors\n" +
                    "许可证：GNU General Public License v3.0（GPL-3.0）\n\n" +
                    "本程序为自由软件，你可以自由使用、修改与再分发；\n" +
                    "但 WITHOUT ANY WARRANTY（不提供任何担保）。"
            };

            var btnClose = new Button
            {
                Text = "关闭",
                Dock = DockStyle.Bottom,
                Height = 44,
                Font = new System.Drawing.Font("Segoe UI", 14F)
            };
            btnClose.Click += (s, e) => this.Close();

            this.Controls.Add(lbl);
            this.Controls.Add(btnClose);
        }
    }
}
