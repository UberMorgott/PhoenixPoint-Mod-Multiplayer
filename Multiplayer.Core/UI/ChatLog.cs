using System.Collections.Generic;

namespace Multiplayer.UI
{
    public struct ChatLine
    {
        public string Sender;
        public string Text;
        public bool IsSystem;
    }

    /// <summary>
    /// Bounded ring buffer of chat lines (user + system). The UI reads <see cref="Version"/> to
    /// cheaply detect changes and only re-render the log when it differs from last frame.
    /// Pure — no Unity references — so it is unit-tested directly.
    /// </summary>
    public class ChatLog
    {
        private readonly Queue<ChatLine> _lines;
        private readonly int _cap;

        public int Version { get; private set; }

        public ChatLog(int capacity = 100)
        {
            _cap = capacity < 1 ? 1 : capacity;
            _lines = new Queue<ChatLine>(_cap);
        }

        public void Append(string sender, string text, bool isSystem)
        {
            if (_lines.Count >= _cap) _lines.Dequeue();
            _lines.Enqueue(new ChatLine { Sender = sender, Text = text, IsSystem = isSystem });
            Version++;
        }

        public void AppendSystem(string text) => Append(null, text, true);

        public IReadOnlyList<ChatLine> Lines => new List<ChatLine>(_lines);
    }
}
