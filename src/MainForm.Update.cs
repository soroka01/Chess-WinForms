using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>The version line at the bottom of the side panel and the "a newer version exists" notice.</summary>
    public sealed partial class MainForm
    {
        Label lblVersion;
        readonly ToolTip versionTip = new ToolTip();
        UpdateInfo update;            // the newest release, kept only while it is newer than this build
        bool checkingForUpdate;
        bool showChecking;            // a check the user asked for is running: say so on the label

        Label BuildVersionLabel()
        {
            lblVersion = new Label();
            lblVersion.AutoSize = false;
            lblVersion.Dock = DockStyle.Fill;
            lblVersion.TextAlign = ContentAlignment.MiddleLeft;
            lblVersion.Padding = new Padding(Ui.S(4), 0, 0, 0);
            lblVersion.Cursor = Cursors.Hand;
            lblVersion.Click += delegate { OnVersionClicked(); };
            versionTip.SetToolTip(lblVersion, "Нажмите, чтобы проверить обновления");
            return lblVersion;
        }

        internal string VersionText { get { return lblVersion.Text; } }

        void ShowVersion()
        {
            Theme t = Theme.Current;
            bool newer = update != null && update.IsNewer && !showChecking;
            string text = "Версия " + AppInfo.Version;
            if (showChecking) text += " · проверяем…";
            else if (newer) text += " · доступна " + update.LatestText + " — обновить";
            lblVersion.Text = text;
            lblVersion.Font = Ui.Font(8.75f, newer ? FontStyle.Bold : FontStyle.Regular);
            lblVersion.ForeColor = newer ? t.Accent : t.Muted;
            lblVersion.BackColor = t.Window;
        }

        /// <summary>Right after the window appears: show what the last check found and, if it is time, look again.</summary>
        internal void AutoCheckForUpdate()
        {
            if (!UpdateChecker.AutoCheckAllowed) return;
            UpdateInfo known = UpdateChecker.Remembered();
            update = known != null && known.IsNewer ? known : null;
            ShowVersion();
            if (UpdateChecker.DueForAutoCheck()) CheckForUpdate(false);
        }

        void OnVersionClicked()
        {
            if (update != null) OfferUpdate();
            else CheckForUpdate(true);
        }

        /// <summary>manual = the user asked, so the answer is shown even when there is nothing new or the check failed.</summary>
        void CheckForUpdate(bool manual)
        {
            if (checkingForUpdate) return;
            checkingForUpdate = true;
            showChecking = manual;
            if (manual) ShowVersion();
            UpdateChecker.CheckAsync(this, delegate(UpdateInfo info)
            {
                checkingForUpdate = false;
                showChecking = false;
                if (closing || IsDisposed) return;
                if (info != null)
                {
                    UpdateChecker.Record(info);
                    update = info.IsNewer ? info : null;
                }
                ShowVersion();
                if (!manual) return;
                if (info == null)
                    AskDialog.Info(this, "Обновления", "Не удалось проверить обновления. Проверьте подключение к интернету и попробуйте ещё раз.");
                else if (info.IsNewer)
                    OfferUpdate();
                else
                    AskDialog.Info(this, "Обновления", "У вас последняя версия: " + AppInfo.Version + ".");
            });
        }

        void OfferUpdate()
        {
            UpdateInfo info = update;
            if (info == null) return;
            string text = "Вышла новая версия игры: " + info.LatestText + " (у вас " + AppInfo.Version + ").\n\n" +
                          "Открыть страницу загрузки на GitHub? Игра сама ничего не скачивает и не заменяет.";
            if (!AskDialog.Ask(this, "Доступно обновление", text, "Открыть", "Позже")) return;
            try { Process.Start(info.Url); }
            catch (Exception) { AskDialog.Info(this, "Обновление", "Не удалось открыть браузер. Страница загрузки:\n" + info.Url); }
        }
    }
}
