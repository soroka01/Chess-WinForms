using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ChessGame
{
    /// <summary>Choose a game found on the local network, or type the host's address by hand.</summary>
    public sealed class JoinDialog : ThemedDialog
    {
        sealed class FoundGame
        {
            public string Name, Address;
            public int Port;
            public DateTime Seen;

            public string Key { get { return Address + ":" + Port; } }

            public override string ToString()
            {
                string who = Name.Length > 0 ? Name : "Игрок";
                return who + "   ·   " + Address + (Port != NetLink.DefaultPort ? ":" + Port : "");
            }
        }

        static string lastAddress = "";

        public string HostName { get; private set; }
        public int Port { get; private set; }

        /// <summary>"Играть" (take a seat) or "Смотреть" (join as a spectator); remembered between runs.</summary>
        public bool WantsToPlay { get { return roleSwitch.SelectedIndex == 0; } }

        readonly List<FoundGame> games = new List<FoundGame>();
        readonly ThemedListBox list = new ThemedListBox();
        readonly ThemedTextBox txtAddress = new ThemedTextBox();
        readonly Label lblSearch = new Label();
        readonly Segmented roleSwitch = new Segmented();
        readonly Timer timer = new Timer();
        readonly HostFinder finder;
        string shownItems = "";

        public JoinDialog()
        {
            Text = "Подключение по локальной сети";
            int w = Ui.S(460), pad = Ui.S(22);
            ClientSize = new Size(w, Ui.S(420));

            Label l1 = Heading("Игры в вашей сети");
            l1.SetBounds(pad, pad, w - pad * 2, Ui.S(24));
            Controls.Add(l1);

            list.SetBounds(pad, l1.Bottom + Ui.S(8), w - pad * 2, Ui.S(150));
            list.SelectedIndexChanged += delegate
            {
                FoundGame g = list.SelectedItem as FoundGame;
                if (g != null) txtAddress.Text = g.Port == NetLink.DefaultPort ? g.Address : g.Key;
            };
            list.DoubleClick += delegate { if (list.SelectedItem != null) Accept(); };
            Controls.Add(list);

            lblSearch.AutoSize = false;
            lblSearch.ForeColor = Theme.Current.Muted;
            lblSearch.Font = Ui.Font(9f);
            lblSearch.SetBounds(pad, list.Bottom + Ui.S(6), w - pad * 2, Ui.S(40));
            Controls.Add(lblSearch);

            Label l2 = Heading("Или введите адрес вручную");
            l2.SetBounds(pad, lblSearch.Bottom + Ui.S(6), w - pad * 2, Ui.S(24));
            Controls.Add(l2);

            txtAddress.SetBounds(pad, l2.Bottom + Ui.S(8), w - pad * 2, Ui.S(42));
            txtAddress.Text = lastAddress;
            Controls.Add(txtAddress);
            Label hint = new Label();
            hint.Text = "IP-адрес хоста, например 192.168.0.5 или 192.168.0.5:5757";
            hint.ForeColor = Theme.Current.Muted;
            hint.Font = Ui.Font(8.5f);
            hint.AutoSize = false;
            hint.SetBounds(pad, txtAddress.Bottom + Ui.S(4), w - pad * 2, Ui.S(20));
            Controls.Add(hint);

            Label l3 = Heading("Войти как");
            l3.SetBounds(pad, hint.Bottom + Ui.S(10), w - pad * 2, Ui.S(24));
            Controls.Add(l3);
            roleSwitch.Items = new[] { "Играть", "Смотреть" };
            roleSwitch.SelectSilently(Settings.GetBool("joinplay", true) ? 0 : 1);
            roleSwitch.SetBounds(pad, l3.Bottom + Ui.S(6), w - pad * 2, Ui.S(38));
            Controls.Add(roleSwitch);

            int by = roleSwitch.Bottom + Ui.S(18);
            FlatButton ok = new FlatButton();
            ok.Text = "Подключиться";
            ok.Style = ButtonStyle.Primary;
            ok.SetBounds(pad, by, Ui.S(180), Ui.S(42));
            ok.Click += delegate { Accept(); };
            Controls.Add(ok);

            FlatButton cancel = new FlatButton();
            cancel.Text = "Отмена";
            cancel.SetBounds(w - pad - Ui.S(120), by, Ui.S(120), Ui.S(42));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancel);
            ClientSize = new Size(w, by + Ui.S(42) + pad);

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
                else if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
            };

            finder = new HostFinder();
            finder.Found += OnFound;
            lblSearch.Text = finder.IsRunning ? "Идёт поиск игр… Хост должен нажать «Создать игру»." : "Автопоиск недоступен — введите адрес вручную.";

            timer.Interval = 1000;
            timer.Tick += delegate { Prune(); };
            timer.Start();
        }

        static Label Heading(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Ui.Font(10.5f, FontStyle.Bold);
            l.ForeColor = Theme.Current.Text;
            l.AutoSize = false;
            return l;
        }

        void OnFound(string name, string address, int port)
        {
            FoundGame found = null;
            foreach (FoundGame g in games)
                if (g.Address == address && g.Port == port) found = g;
            if (found == null)
            {
                found = new FoundGame();
                found.Address = address;
                found.Port = port;
                games.Add(found);
            }
            found.Name = name;
            found.Seen = DateTime.UtcNow;
            RefreshList();
        }

        void Prune()
        {
            DateTime limit = DateTime.UtcNow.AddSeconds(-4);
            games.RemoveAll(delegate(FoundGame g) { return g.Seen < limit; });
            RefreshList();
        }

        void RefreshList()
        {
            string signature = string.Join("\n", games.ConvertAll(delegate(FoundGame g) { return g.ToString(); }).ToArray());
            if (signature == shownItems) return;
            shownItems = signature;

            FoundGame selected = list.SelectedItem as FoundGame;
            string selectedKey = selected != null ? selected.Key : null;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (FoundGame g in games)
            {
                int index = list.Items.Add(g);
                if (g.Key == selectedKey) list.SelectedIndex = index;
            }
            list.EndUpdate();
        }

        void Accept()
        {
            string host;
            int port;
            if (!TryParseAddress(txtAddress.Text, out host, out port))
            {
                AskDialog.Info(this, "Неверный адрес", "Введите адрес в виде 192.168.0.5 или 192.168.0.5:5757.");
                return;
            }
            lastAddress = txtAddress.Text.Trim();
            Settings.Set("joinplay", WantsToPlay);
            Settings.Save();
            HostName = host;
            Port = port;
            DialogResult = DialogResult.OK;
        }

        public static bool TryParseAddress(string text, out string host, out int port)
        {
            host = (text ?? "").Trim();
            port = NetLink.DefaultPort;
            int colon = host.LastIndexOf(':');
            if (colon >= 0)
            {
                if (host.IndexOf(':') != colon) return false;   // IPv6 is not supported
                if (!int.TryParse(host.Substring(colon + 1), out port) || port < 1 || port > 65535) return false;
                host = host.Substring(0, colon).Trim();
            }
            return host.Length > 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (finder != null) finder.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
