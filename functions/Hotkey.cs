using System.Windows.Input;

namespace superClipboard
{
    public class Hotkey
    {
        public Key Key { get; set; }
        public ModifierKeys Modifiers { get; set; }
        
        public Hotkey() { }

        public Hotkey(Key key, ModifierKeys modifiers)
        {
            Key = key;
            Modifiers = modifiers;
        }

        /// <summary>
        /// 从 KeyEventArgs 转换为 Hotkey
        /// </summary>
        public static Hotkey FromKeyEventArgs(KeyEventArgs e)
        {
            var modifiers = ModifierKeys.None;
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                modifiers |= ModifierKeys.Control;
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
                modifiers |= ModifierKeys.Alt;
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                modifiers |= ModifierKeys.Shift;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
                modifiers |= ModifierKeys.Windows;

            return new Hotkey(e.Key == Key.System ? e.SystemKey : e.Key, modifiers);
        }

        /// <summary>
        /// 比较两个 Hotkey 是否相等
        /// </summary>
        public bool Equals(Hotkey other)
        {
            return other != null && Key == other.Key && Modifiers == other.Modifiers;
        }

        public override string ToString()
        {
            string result = "";
            if (Modifiers.HasFlag(ModifierKeys.Control)) result += "Ctrl + ";
            if (Modifiers.HasFlag(ModifierKeys.Alt)) result += "Alt + ";
            if (Modifiers.HasFlag(ModifierKeys.Shift)) result += "Shift + ";
            if (Modifiers.HasFlag(ModifierKeys.Windows)) result += "Win + ";
            result += Key.ToString();
            return result;
        }
    }
}