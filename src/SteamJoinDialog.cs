using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>Lists chess games that other Steam players have opened (through Spacewar) and lets you pick one.</summary>
    public sealed class SteamJoinDialog : ThemedDialog
    {
        readonly ILobbyService service;
        readonly ThemedListBox list = new ThemedListBox();
        readonly Label lblInfo = new Label();
        readonly Segmented roleSwitch = new Segmented();
        readonly FlatButton btnJoin = new FlatButton();
        readonly FlatButton btnRefresh = new FlatButton();
        readonly Timer autoRefresh = new Timer();
        bool searching;
        bool disposed;

        public ulong SelectedLobby { get; private set; }

        /// <summary>"Играть" (take a seat) or "Смотреть" (join as a spectator); remembered between runs.</summary>
        public bool WantsToPlay { get { return roleSwitch.SelectedIndex == 0; } }

        sealed class Entry
        {
            public LobbyInfo Info;
            public override string ToString()
            {
                string who = Info.HostName.Length > 0 ? Info.HostName : "Игрок Steam";
                if (Info.Players >= 0)
                {
                    string row = who + (Info.IsFriend ? " ★" : "") + " · играют " + Info.Players + "/2";
                    if (Info.Watchers >= 0) row += " · смотрят " + Info.Watchers;
                    return row;
                }
                return (Info.IsFriend ? "★  " : "") + who + "   ·   " + Info.Members + "/2" + (Info.IsFriend ? "   ·   друг" : "");
            }
        }

        public SteamJoinDialog(ILobbyService service)
        {
            this.service = service;
            Text = "Подключение через Steam";
            int w = Ui.S(460), pad = Ui.S(22);
            ClientSize = new Size(w, Ui.S(400));

            Label l1 = new Label();
            l1.Text = "Открытые игры";
            l1.Font = Ui.Font(10.5f, FontStyle.Bold);
            l1.ForeColor = Theme.Current.Text;
            l1.AutoSize = false;
            l1.SetBounds(pad, pad, w - pad * 2 - Ui.S(160), Ui.S(24));
            Controls.Add(l1);
            Label who = new Label();
            who.Text = "вы: " + service.MyName;
            who.Font = Ui.Font(9f);
            who.ForeColor = Theme.Current.Muted;
            who.TextAlign = ContentAlignment.MiddleRight;
            who.AutoSize = false;
            who.SetBounds(w - pad - Ui.S(160), pad, Ui.S(160), Ui.S(24));
            Controls.Add(who);

            list.SetBounds(pad, l1.Bottom + Ui.S(8), w - pad * 2, Ui.S(200));
            list.SelectedIndexChanged += delegate { btnJoin.Enabled = list.SelectedItem != null; };
            list.DoubleClick += delegate { Accept(); };
            Controls.Add(list);

            lblInfo.AutoSize = false;
            lblInfo.Font = Ui.Font(9f);
            lblInfo.ForeColor = Theme.Current.Muted;
            lblInfo.SetBounds(pad, list.Bottom + Ui.S(8), w - pad * 2, Ui.S(44));
            Controls.Add(lblInfo);

            Label l3 = new Label();
            l3.Text = "Войти как";
            l3.Font = Ui.Font(10.5f, FontStyle.Bold);
            l3.ForeColor = Theme.Current.Text;
            l3.AutoSize = false;
            l3.SetBounds(pad, lblInfo.Bottom + Ui.S(2), w - pad * 2, Ui.S(24));
            Controls.Add(l3);
            roleSwitch.Items = new[] { "Играть", "Смотреть" };
            roleSwitch.SelectSilently(Settings.GetBool("joinplay", true) ? 0 : 1);
            roleSwitch.SetBounds(pad, l3.Bottom + Ui.S(6), w - pad * 2, Ui.S(38));
            Controls.Add(roleSwitch);

            int by = roleSwitch.Bottom + Ui.S(18);
            btnJoin.Text = "Подключиться";
            btnJoin.Style = ButtonStyle.Primary;
            btnJoin.Enabled = false;
            btnJoin.SetBounds(pad, by, Ui.S(170), Ui.S(42));
            btnJoin.Click += delegate { Accept(); };
            Controls.Add(btnJoin);

            btnRefresh.Text = "Обновить";
            btnRefresh.SetBounds(btnJoin.Right + Ui.S(10), by, Ui.S(120), Ui.S(42));
            btnRefresh.Click += delegate { Search(); };
            Controls.Add(btnRefresh);

            FlatButton cancel = new FlatButton();
            cancel.Text = "Отмена";
            cancel.SetBounds(w - pad - Ui.S(110), by, Ui.S(110), Ui.S(42));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancel);
            ClientSize = new Size(w, by + Ui.S(42) + pad);

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
                else if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
                else if (e.KeyCode == Keys.F5) Search();
            };

            autoRefresh.Interval = 4000;
            autoRefresh.Tick += delegate { Search(); };
            Shown += delegate { Search(); autoRefresh.Start(); };
        }

        void Search()
        {
            if (searching || disposed) return;
            searching = true;
            lblInfo.Text = "Поиск игр в Steam…";
            service.FindLobbies(delegate(List<LobbyInfo> found)
            {
                searching = false;
                if (disposed) return;
                ShowResults(found);
            });
        }

        void ShowResults(List<LobbyInfo> found)
        {
            ulong selected = list.SelectedItem is Entry ? ((Entry)list.SelectedItem).Info.Id : 0;
            found.Sort(delegate(LobbyInfo a, LobbyInfo b)
            {
                if (a.IsFriend != b.IsFriend) return a.IsFriend ? -1 : 1;   // friends first
                return string.Compare(a.HostName, b.HostName, StringComparison.CurrentCultureIgnoreCase);
            });
            list.BeginUpdate();
            list.Items.Clear();
            foreach (LobbyInfo info in found)
            {
                // with a player count from the host the lobby may hold spectators beyond two members and is still worth joining
                if (info.Players < 0 && info.Members >= 2) continue;   // already full
                int index = list.Items.Add(new Entry { Info = info });
                if (info.Id == selected) list.SelectedIndex = index;
            }
            list.EndUpdate();
            lblInfo.Text = list.Items.Count == 0
                ? "Пока ничего не найдено. Соперник должен выбрать «Steam» и нажать «Создать игру» (оба — в этой программе)."
                : "Выберите игру и нажмите «Подключиться». Друзья отмечены ★ и стоят первыми.";
        }

        void Accept()
        {
            Entry entry = list.SelectedItem as Entry;
            if (entry == null) return;
            SelectedLobby = entry.Info.Id;
            Settings.Set("joinplay", WantsToPlay);
            Settings.Save();
            DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposed = true;
                autoRefresh.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
