using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ChessGame
{
    // The "Люди" tab: who is in the room, who sits where, who may talk. The host's controls (seats, mutes, hand-over, removal)
    // open as a menu when a row is clicked; everybody can step down to the spectators and sit back, and silence a voice for themselves.
    public sealed partial class MainForm
    {
        BufferedPanel peoplePanel;
        PeopleList peopleList;
        BufferedTable policyRow;
        FlatButton btnPolicyChat, btnPolicyVoice;

        void BuildPeopleUi(CardPanel card)
        {
            peoplePanel = new BufferedPanel();
            peoplePanel.Dock = DockStyle.Fill;
            peoplePanel.Visible = false;
            onCard.Add(peoplePanel);

            peopleList = new PeopleList();
            peopleList.Dock = DockStyle.Fill;
            peopleList.PersonClicked += delegate(PeopleList.Person person, Point at) { OnPersonClicked(person, at); };

            // the host's switches for all spectators at once
            policyRow = new BufferedTable();
            policyRow.Dock = DockStyle.Bottom;
            policyRow.Height = Ui.S(54);
            policyRow.ColumnCount = 2;
            policyRow.RowCount = 1;
            policyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            policyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            policyRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            policyRow.Padding = new Padding(0, Ui.S(10), 0, 0);
            policyRow.Visible = false;
            onCard.Add(policyRow);
            btnPolicyChat = new FlatButton();
            btnPolicyVoice = new FlatButton();
            foreach (FlatButton b in new FlatButton[] { btnPolicyChat, btnPolicyVoice })
            {
                b.Dock = DockStyle.Fill;
                b.Margin = new Padding(0, 0, Ui.S(4), 0);
            }
            btnPolicyVoice.Margin = new Padding(Ui.S(4), 0, 0, 0);
            btnPolicyChat.Click += delegate { if (InRoom && room.IsHost) room.SetPolicy(!room.SpectatorChatAllowed, room.SpectatorVoiceAllowed); };
            btnPolicyVoice.Click += delegate { if (InRoom && room.IsHost) room.SetPolicy(room.SpectatorChatAllowed, !room.SpectatorVoiceAllowed); };
            policyRow.Controls.Add(btnPolicyChat, 0, 0);
            policyRow.Controls.Add(btnPolicyVoice, 1, 0);

            peoplePanel.Controls.Add(peopleList);
            peoplePanel.Controls.Add(policyRow);
        }

        /// <summary>Rebuilds the list from the room (cheap: nothing is repainted unless something visible changed).</summary>
        void UpdatePeople()
        {
            if (peopleList == null) return;
            List<PeopleList.Person> people = new List<PeopleList.Person>();
            if (InRoom)
            {
                RoomMember[] members = room.Members;
                // the players first (white, black), then the spectators in the order they came
                foreach (Seat seat in new Seat[] { Seat.White, Seat.Black, Seat.None })
                {
                    foreach (RoomMember m in members)
                    {
                        if (m.Seat != seat) continue;
                        PeopleList.Person p = new PeopleList.Person();
                        p.Id = m.Id;
                        p.Name = m.Name;
                        p.IsMe = m.Id == room.MyId;
                        p.IsHost = m.Id == room.HostId;
                        p.Seat = (int)m.Seat;
                        p.ChatMuted = m.ChatMuted || (m.Seat == Seat.None && !room.SpectatorChatAllowed);
                        p.VoiceMuted = m.VoiceMuted || (m.Seat == Seat.None && !room.SpectatorVoiceAllowed);
                        p.Speaking = voice != null && (p.IsMe ? voice.Transmitting : voice.IsSpeaking(m.Id));
                        p.Away = localMuted.Contains(m.Id);
                        people.Add(p);
                    }
                }
            }
            peopleList.SetPeople(people);

            bool host = InRoom && room.IsHost;
            policyRow.Visible = host;
            if (host)
            {
                btnPolicyChat.Text = room.SpectatorChatAllowed ? "Чат зрителей: вкл" : "Чат зрителей: выкл";
                btnPolicyVoice.Text = room.SpectatorVoiceAllowed ? "Голос зрителей: вкл" : "Голос зрителей: выкл";
                btnPolicyChat.Active = room.SpectatorChatAllowed;
                btnPolicyVoice.Active = room.SpectatorVoiceAllowed;
            }
        }

        // ---------------------------------------------------------------- the menu of a person

        void OnPersonClicked(PeopleList.Person person, Point at)
        {
            if (!InRoom) return;
            RoomMember m = room.Find(person.Id);
            if (m == null) return;
            string id = m.Id;
            bool me = id == room.MyId;
            bool host = room.IsHost;
            List<Popup.Item> items = new List<Popup.Item>();

            if (me)
            {
                if (m.Seat != Seat.None)
                {
                    items.Add(Item("Перейти в зрители", delegate { StandUp(); }));
                }
                else
                {
                    if (room.SeatHolder(Seat.White) == null) items.Add(Item("Сесть за белых", delegate { SitDown(Seat.White); }));
                    if (room.SeatHolder(Seat.Black) == null) items.Add(Item("Сесть за чёрных", delegate { SitDown(Seat.Black); }));
                    if (items.Count == 0) items.Add(Disabled("За столом нет свободных мест"));
                }
            }
            else
            {
                if (host)
                {
                    if (m.Seat != Seat.None)
                    {
                        items.Add(Item("Убрать из-за стола", delegate { if (InRoom && room.IsHost) room.SetSeat(id, Seat.None); }));
                    }
                    else
                    {
                        if (room.SeatHolder(Seat.White) == null) items.Add(Item("Посадить за белых", delegate { if (InRoom && room.IsHost) room.SetSeat(id, Seat.White); }));
                        if (room.SeatHolder(Seat.Black) == null) items.Add(Item("Посадить за чёрных", delegate { if (InRoom && room.IsHost) room.SetSeat(id, Seat.Black); }));
                    }
                    if (m.Seat == Seat.None)
                    {
                        if (items.Count > 0) items.Add(Separator());
                        bool chatOff = m.ChatMuted, voiceOff = m.VoiceMuted;
                        items.Add(Item(chatOff ? "Разрешить писать в чат" : "Запретить писать в чат",
                            delegate { if (InRoom && room.IsHost) room.SetMute(id, !chatOff, voiceOff); }));
                        items.Add(Item(voiceOff ? "Разрешить говорить" : "Запретить говорить",
                            delegate { if (InRoom && room.IsHost) room.SetMute(id, chatOff, !voiceOff); }));
                    }
                    items.Add(Separator());
                    items.Add(Item("Передать роль хоста…", delegate { TransferHostTo(id); }));
                    items.Add(Separator());
                }
                Popup.Item mute = Item("Не слышать голос (только у меня)", delegate { SetLocalMute(id, !localMuted.Contains(id)); });
                mute.Checked = localMuted.Contains(id);
                items.Add(mute);
                if (host)
                {
                    items.Add(Separator());
                    Popup.Item kick = Item("Исключить из комнаты", delegate { KickMember(id); });
                    kick.Danger = true;
                    items.Add(kick);
                }
            }
            Popup.Show(this, at, items);
        }

        static Popup.Item Item(string text, Action onClick)
        {
            Popup.Item it = new Popup.Item();
            it.Text = text;
            it.OnClick = onClick;
            return it;
        }

        static Popup.Item Disabled(string text)
        {
            Popup.Item it = new Popup.Item();
            it.Text = text;
            it.Disabled = true;
            return it;
        }

        static Popup.Item Separator()
        {
            Popup.Item it = new Popup.Item();
            it.Separator = true;
            return it;
        }

        void TransferHostTo(string id)
        {
            if (!InRoom || !room.IsHost) return;
            RoomMember m = room.Find(id);
            if (m == null) return;
            string question = roomIsSteam
                ? "Передать роль хоста участнику «" + m.Name + "»? Он будет решать, кого пускать, и управлять комнатой; вы станете обычным участником."
                : "Передать роль хоста участнику «" + m.Name + "»? Комната переедет на его компьютер (это займёт несколько секунд), он будет решать, кого пускать; вы станете обычным участником.";
            if (!Ask("Передать хост", question, "Передать", "Отмена")) return;
            if (!InRoom || !room.IsHost) return;
            if (!room.TransferHost(id)) { netInfo = "Сейчас нельзя передать хост."; RefreshAll(); }
        }

        void KickMember(string id)
        {
            if (!InRoom || !room.IsHost) return;
            RoomMember m = room.Find(id);
            if (m == null) return;
            if (!Ask("Исключить", "Исключить «" + m.Name + "» из комнаты? Вернуться в эту игру он сможет только в новой комнате.", "Исключить", "Отмена")) return;
            if (InRoom && room.IsHost) room.Kick(id);
        }

        // ---------------------------------------------------------------- test access

        internal PeopleList PeopleForTest { get { return peopleList; } }
        internal string TestPeopleText()
        {
            List<string> parts = new List<string>();
            if (InRoom)
                foreach (RoomMember m in room.Members) parts.Add(m.Name + ":" + m.Seat + (m.Id == room.HostId ? ":host" : ""));
            return string.Join(",", parts.ToArray());
        }
    }
}
