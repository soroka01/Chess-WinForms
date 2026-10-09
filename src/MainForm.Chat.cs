using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ChessGame
{
    // Text and voice chat of a room. Both are open to everybody in it (players and spectators) unless the host has switched
    // them off for spectators, or for one person; the room's permissions are checked on the sending and on the receiving side.
    //   CHAT <text>      one chat message (up to 300 characters)
    //   VOICE <base64>   one 100 ms voice frame (IMA ADPCM, 16 kHz), only sent while the speaker is actually talking
    public sealed partial class MainForm
    {
        const int MaxChatChars = 300;
        const int TabMoves = 0, TabChat = 1, TabPeople = 2;

        ChatControl chat;
        ThemedTextBox chatInput;
        FlatButton btnSend, btnMic, btnSpk;
        Segmented segTabs;
        BufferedPanel chatPanel;
        Label movesTabTitle;

        VoiceChannel voice;
        readonly Timer voiceTimer = new Timer();
        int unreadChat;
        readonly Dictionary<string, List<DateTime>> chatArrivals = new Dictionary<string, List<DateTime>>();
        readonly HashSet<string> localMuted = new HashSet<string>();   // people we chose not to hear

#pragma warning disable 649   // assigned only by tests
        /// <summary>Creates the voice channel; tests substitute one with fake audio devices.</summary>
        internal Func<VoiceChannel> VoiceFactory;
#pragma warning restore 649

        // ---------------------------------------------------------------- UI

        /// <summary>Fills the moves card: the move list, the chat and people tabs and the header with the tabs and the voice buttons.</summary>
        void BuildChatUi(CardPanel card, MoveListControl list, Label title)
        {
            movesTabTitle = title;

            // chat tab: messages above, input row below
            chatPanel = new BufferedPanel();
            chatPanel.Dock = DockStyle.Fill;
            chatPanel.Visible = false;
            onCard.Add(chatPanel);

            chat = new ChatControl();
            chat.Dock = DockStyle.Fill;

            BufferedTable inputRow = new BufferedTable();
            inputRow.Dock = DockStyle.Bottom;
            inputRow.Height = Ui.S(54);
            inputRow.ColumnCount = 2;
            inputRow.RowCount = 1;
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(54)));
            inputRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            inputRow.Padding = new Padding(0, Ui.S(10), 0, 0);
            onCard.Add(inputRow);

            chatInput = new ThemedTextBox();
            chatInput.Dock = DockStyle.Fill;
            chatInput.MaxLength = MaxChatChars;
            chatInput.Cue = "Сообщение…";
            chatInput.Margin = new Padding(0, 0, Ui.S(8), 0);
            btnSend = new FlatButton();
            btnSend.Glyph = "";
            btnSend.Fallback = "➤";
            btnSend.Style = ButtonStyle.Primary;
            btnSend.Dock = DockStyle.Fill;
            btnSend.Margin = new Padding(0);
            btnSend.Tip = "Отправить (Enter)";
            inputRow.Controls.Add(chatInput, 0, 0);
            inputRow.Controls.Add(btnSend, 1, 0);

            chatPanel.Controls.Add(chat);
            chatPanel.Controls.Add(inputRow);

            // header: "Ходы" (or the tabs) on the left, microphone and speaker on the right
            BufferedTable head = new BufferedTable();
            head.Dock = DockStyle.Top;
            head.Height = Ui.S(42);
            head.ColumnCount = 4;
            head.RowCount = 1;
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(236)));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(44)));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(44)));
            head.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            onCard.Add(head);

            BufferedPanel tabHost = new BufferedPanel();
            tabHost.Dock = DockStyle.Fill;
            tabHost.Padding = new Padding(0, Ui.S(2), Ui.S(8), Ui.S(2));
            onCard.Add(tabHost);
            segTabs = new Segmented();
            segTabs.Items = new string[] { "Ходы", "Чат", "Люди" };
            segTabs.Dock = DockStyle.Fill;
            segTabs.Visible = false;
            title.Dock = DockStyle.Fill;
            title.Height = Ui.S(28);
            title.TextAlign = ContentAlignment.MiddleLeft;
            tabHost.Controls.Add(segTabs);
            tabHost.Controls.Add(title);
            head.Controls.Add(tabHost, 0, 0);

            btnMic = IconButton("", "M", "Микрофон (V): вас слышно, когда вы говорите");
            btnSpk = IconButton("", "♪", "Звук собеседников");
            btnMic.Style = btnSpk.Style = ButtonStyle.Ghost;
            btnMic.Visible = btnSpk.Visible = false;
            head.Controls.Add(btnMic, 2, 0);
            head.Controls.Add(btnSpk, 3, 0);

            BuildPeopleUi(card);

            card.Controls.Add(list);
            card.Controls.Add(chatPanel);
            card.Controls.Add(peoplePanel);
            card.Controls.Add(head);

            segTabs.SelectedIndexChanged += delegate { OnChatTabChanged(); };
            btnSend.Click += delegate { SendChatFromInput(); };
            chatInput.EnterPressed += delegate { SendChatFromInput(); };
            btnMic.Click += delegate { ToggleMic(); };
            btnSpk.Click += delegate { ToggleSpeaker(); };
            voiceTimer.Interval = 60;
            voiceTimer.Tick += delegate { PumpVoice(); };
        }

        /// <summary>Chat and voice exist while we are in a room.</summary>
        bool ChatAvailable
        {
            get { return mode == Mode.Network && InRoom; }
        }

        /// <summary>Shows the tabs and the voice buttons when they make sense, and the right one of the lists.</summary>
        void UpdateChatUi()
        {
            if (segTabs == null) return;
            bool available = ChatAvailable;
            segTabs.Visible = available;
            movesTabTitle.Visible = !available;
            btnMic.Visible = btnSpk.Visible = available;
            if (!available && segTabs.SelectedIndex != TabMoves) segTabs.SelectSilently(TabMoves);
            bool showChat = available && segTabs.SelectedIndex == TabChat;
            bool showPeople = available && segTabs.SelectedIndex == TabPeople;
            chatPanel.Visible = showChat;
            peoplePanel.Visible = showPeople;
            moveList.Visible = !showChat && !showPeople;

            UpdateChatPermissions();

            bool micOpen = voice != null && voice.MicOpen;
            btnMic.Active = micOpen;
            btnMic.Style = (voice != null && voice.Transmitting) ? ButtonStyle.Primary : ButtonStyle.Ghost;
            bool muted = voice != null && voice.SpeakerMuted;
            btnSpk.Glyph = muted ? "" : "";
            btnSpk.Fallback = muted ? "×" : "♪";
            btnSpk.Tip = muted ? "Включить звук собеседников" : "Выключить звук собеседников";

            string chatLabel = unreadChat > 0 && !showChat ? "Чат · " + unreadChat : "Чат";
            int waiting = (available && room.IsHost) ? shownRequests.Count : 0;
            string peopleLabel = waiting > 0 ? "Люди · " + waiting : "Люди";
            if (segTabs.Items.Length != 3 || segTabs.Items[1] != chatLabel || segTabs.Items[2] != peopleLabel)
                segTabs.Items = new string[] { "Ходы", chatLabel, peopleLabel };
            UpdatePeople();
        }

        /// <summary>The chat box and the microphone follow what the room allows us to do.</summary>
        void UpdateChatPermissions()
        {
            if (chatInput == null) return;
            bool canChat = InRoom && room.CanChat(room.MyId);
            bool canSpeak = InRoom && room.CanSpeak(room.MyId);
            chatInput.Enabled = canChat;
            btnSend.Enabled = canChat;
            chatInput.Cue = canChat ? "Сообщение…" : "Хост отключил вам чат";
            btnMic.Enabled = canSpeak || (voice != null && voice.MicOpen);
            btnMic.Tip = canSpeak ? "Микрофон (V): вас слышно, когда вы говорите" : "Хост отключил вам микрофон";
            if (!canSpeak && voice != null && voice.MicOpen && InRoom)
            {
                voice.StopMic();
                chat.AddSystem("Хост отключил вам микрофон");
            }
            if (!canChat && chatWasAllowed && InRoom) chat.AddSystem("Хост отключил вам чат");
            if (canChat && !chatWasAllowed && InRoom && chatNoticeShown) chat.AddSystem("Хост снова разрешил вам писать");
            chatWasAllowed = canChat;
            chatNoticeShown = InRoom;
        }

        bool chatWasAllowed = true, chatNoticeShown;

        void OnChatTabChanged()
        {
            if (segTabs.SelectedIndex == TabChat)
            {
                unreadChat = 0;
                UpdateChatUi();
                if (chatInput.Enabled) chatInput.Focus();
            }
            else
            {
                UpdateChatUi();
            }
        }

        // ---------------------------------------------------------------- text chat

        /// <summary>Keeps one line of plain text: no control characters, no runs of blanks, at most MaxChatChars.</summary>
        static string CleanChat(string text)
        {
            if (text == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
                if (sb.Length >= MaxChatChars) break;
            }
            return sb.ToString();
        }

        void SendChatFromInput()
        {
            if (!ChatAvailable || !room.CanChat(room.MyId)) return;
            string text = CleanChat(chatInput.Text);
            if (text.Length == 0) return;
            room.Broadcast("CHAT " + text);
            chat.Add(true, text, DateTime.Now);
            chatInput.Text = "";
            chatInput.Focus();
        }

        void OnNetChat(string from, string line)
        {
            if (!InRoom) return;
            string text = CleanChat(line.Length > 5 ? line.Substring(5) : "");
            if (text.Length == 0) return;

            // somebody flooding the chat gets ignored
            DateTime now = DateTime.UtcNow;
            List<DateTime> arrivals;
            if (!chatArrivals.TryGetValue(from, out arrivals)) { arrivals = new List<DateTime>(); chatArrivals[from] = arrivals; }
            arrivals.RemoveAll(delegate(DateTime t) { return (now - t).TotalSeconds > 4; });
            if (arrivals.Count >= 8) return;
            arrivals.Add(now);

            RoomMember sender = room.Find(from);
            chat.Add(false, text, DateTime.Now, sender != null ? sender.Name : "");
            bool looking = segTabs.SelectedIndex == TabChat && chatPanel.Visible && ContainsFocus;
            if (!looking)
            {
                unreadChat++;
                Sounds.Play(Sfx.Notify);
            }
            UpdateChatUi();
        }

        // ---------------------------------------------------------------- voice

        VoiceChannel EnsureVoice()
        {
            if (voice == null)
            {
                voice = VoiceFactory != null ? VoiceFactory() : new VoiceChannel();
                foreach (string id in localMuted) voice.SetSenderMuted(id, true);
                voiceTimer.Start();
            }
            return voice;
        }

        void ToggleMic()
        {
            if (!ChatAvailable) return;
            if (!room.CanSpeak(room.MyId)) { chat.AddSystem("Хост отключил вам микрофон"); return; }
            VoiceChannel v = EnsureVoice();
            if (v.MicOpen)
            {
                v.StopMic();
                chat.AddSystem("Микрофон выключен");
            }
            else
            {
                if (VoiceFactory == null && !VoiceChannel.MicrophoneAvailable)
                {
                    AskDialog.Info(this, "Микрофон", "Микрофон не найден. Подключите его и попробуйте снова.");
                    return;
                }
                string error;
                if (!v.StartMic(out error))
                {
                    AskDialog.Info(this, "Микрофон", string.IsNullOrEmpty(error) ? "Не удалось включить микрофон." : error);
                    return;
                }
                chat.AddSystem("Микрофон включён: вас слышат все в комнате, когда вы говорите");
            }
            UpdateChatUi();
        }

        void ToggleSpeaker()
        {
            if (!ChatAvailable) return;
            VoiceChannel v = EnsureVoice();
            v.SpeakerMuted = !v.SpeakerMuted;
            UpdateChatUi();
        }

        /// <summary>One person's voice is switched off or on for us alone.</summary>
        void SetLocalMute(string id, bool mute)
        {
            if (mute) localMuted.Add(id); else localMuted.Remove(id);
            if (voice != null) voice.SetSenderMuted(id, mute);
            UpdatePeople();
        }

        void OnNetVoice(string from, string line)
        {
            if (!InRoom || line.Length <= 6) return;
            EnsureVoice().Receive(from, line.Substring(6));
        }

        /// <summary>Runs every 60 ms while a voice channel exists: sends what the microphone produced, updates the indicators.</summary>
        void PumpVoice()
        {
            if (voice == null) { voiceTimer.Stop(); return; }
            string[] frames = voice.TakeOutgoing();
            if (ChatAvailable)
                foreach (string frame in frames) room.Broadcast("VOICE " + frame);

            bool transmitting = voice.Transmitting;
            foreach (PlayerBar bar in new PlayerBar[] { topBar, bottomBar })
            {
                bool speaking = false;
                if (ChatAvailable)
                {
                    RoomMember holder = room.SeatHolder(bar.IsWhite ? Seat.White : Seat.Black);
                    if (holder != null) speaking = holder.Id == room.MyId ? transmitting : voice.IsSpeaking(holder.Id);
                }
                bar.Speaking = speaking;
            }
            if (btnMic.Style != (transmitting ? ButtonStyle.Primary : ButtonStyle.Ghost))
                btnMic.Style = transmitting ? ButtonStyle.Primary : ButtonStyle.Ghost;
            UpdatePeople();
        }

        /// <summary>The room is over: chat history and the voice channel go with it.</summary>
        void ResetChat()
        {
            voiceTimer.Stop();
            if (voice != null)
            {
                // stopping the audio devices waits for their threads; a stalled sound driver must not freeze the window
                VoiceChannel old = voice;
                voice = null;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    try { old.Dispose(); } catch (Exception) { }
                });
            }
            if (chat != null) chat.Clear();
            unreadChat = 0;
            chatArrivals.Clear();
            localMuted.Clear();
            chatWasAllowed = true;
            chatNoticeShown = false;
            if (topBar != null) { topBar.Speaking = false; bottomBar.Speaking = false; }
        }

        // ---------------------------------------------------------------- test access

        internal ChatControl ChatForTest { get { return chat; } }
        internal VoiceChannel VoiceForTest { get { return voice; } }
        internal int UnreadChatForTest { get { return unreadChat; } }
        internal bool ChatInputEnabledForTest { get { return chatInput.Enabled; } }
        internal void TestSendChat(string text) { chatInput.Text = text; SendChatFromInput(); }
        internal void TestToggleMic() { ToggleMic(); }
        internal void TestPumpVoice() { PumpVoice(); }
        internal void TestSelectTab(int tab) { segTabs.SelectedIndex = tab; }
        internal void TestSelectChatTab(bool chatTab) { segTabs.SelectedIndex = chatTab ? TabChat : TabMoves; }
    }
}
