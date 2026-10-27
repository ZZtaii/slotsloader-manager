using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.0: the app shows no hover text anywhere. Two checks: a window's controls (no ToolTip component on any
    // form or control, no list, tree or menu item tooltips) and the assembly (no code creates a ToolTip, calls
    // SetToolTip or sets a ToolTipText).
    internal static class UiAudit
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static List<string> ToolTipFindings(Control root)
        {
            var found = new List<string>();
            foreach (Control c in Walk(root))
            {
                for (Type t = c.GetType(); t != null && t.Assembly == typeof(UiAudit).Assembly; t = t.BaseType)
                    foreach (FieldInfo f in t.GetFields(All))
                        if (typeof(ToolTip).IsAssignableFrom(f.FieldType) && f.GetValue(f.IsStatic ? null : c) != null && !ToolTipAllowed.Contains(t.Name + "." + f.Name)) found.Add(t.Name + "." + f.Name + " is a ToolTip");
                var list = c as ListView;
                if (list != null) { if (list.ShowItemToolTips) found.Add(Name(c) + ": ShowItemToolTips"); foreach (ListViewItem i in ListItems(list)) if (!String.IsNullOrEmpty(i.ToolTipText)) { found.Add(Name(c) + " row tooltip: " + i.ToolTipText); break; } }
                var tree = c as TreeView;
                if (tree != null && tree.ShowNodeToolTips) found.Add(Name(c) + ": ShowNodeToolTips");
                if (c.ContextMenuStrip != null) Menu(c.ContextMenuStrip, Name(c), found);
                var strip = c as ToolStrip;
                if (strip != null) Menu(strip, Name(c), found);
            }
            return found;
        }
        private static void Menu(ToolStrip strip, string owner, List<string> found)
        {
            if (strip.ShowItemToolTips && strip.Items.Cast<ToolStripItem>().Any(i => !String.IsNullOrEmpty(i.ToolTipText) || i.AutoToolTip)) found.Add(owner + " menu shows item tooltips");
            foreach (ToolStripItem i in strip.Items) Item(i, owner, found);
        }
        private static void Item(ToolStripItem i, string owner, List<string> found)
        {
            if (!String.IsNullOrEmpty(i.ToolTipText) && !(i.AutoToolTip && i.ToolTipText == i.Text)) found.Add(owner + " menu item '" + i.Text + "' tooltip: " + i.ToolTipText);
            var drop = i as ToolStripDropDownItem;
            if (drop != null) { if (drop.DropDown.ShowItemToolTips && drop.DropDownItems.Cast<ToolStripItem>().Any(x => !String.IsNullOrEmpty(x.ToolTipText))) found.Add(owner + " submenu '" + i.Text + "' shows tooltips"); foreach (ToolStripItem x in drop.DropDownItems) Item(x, owner, found); }
        }
        private static IEnumerable<Control> Walk(Control c) { yield return c; foreach (Control x in c.Controls) foreach (Control y in Walk(x)) yield return y; }
        private static IEnumerable<ListViewItem> ListItems(ListView list) { for (int i = 0; i < list.Items.Count; i++) yield return list.Items[i]; }
        private static string Name(Control c) { return String.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name; }

        // 2.4.1: every ThemedMessageBox.Show/Ask and MessageBox.Show call site in the app (test and check code excluded),
        // as "Type.Method" (a lambda counts as its enclosing method). The ui-polish check compares them with an allow-list,
        // so a new popup needs a deliberate exception: a popup is allowed only for a permanent loss with no Undo, or a
        // decision only the user can make.
        internal static List<string> PopupCalls() { return CallSites(IsPopup); }
        // 2.4.1: every modal window (Form.ShowDialog) outside ThemedMessageBox: the tool windows and viewers the user
        // opens, and the one permanent-delete confirmation. File and folder pickers (CommonDialog) are not counted.
        internal static List<string> DialogCalls() { return CallSites(delegate(MethodBase target) { return target.DeclaringType == typeof(Form) && target.Name == "ShowDialog"; }); }
        private static bool IsPopup(MethodBase target)
        {
            return (target.DeclaringType == typeof(ThemedMessageBox) && (target.Name == "Show" || target.Name == "Ask")) || (target.DeclaringType == typeof(MessageBox) && target.Name == "Show");
        }
        private static List<string> CallSites(Func<MethodBase, bool> matches)
        {
            var sites = new List<string>();
            Assembly app = typeof(UiAudit).Assembly;
            foreach (Type type in app.GetTypes())
            {
                Type owner = type; while (owner.DeclaringType != null && owner.Name.StartsWith("<")) owner = owner.DeclaringType;
                while (owner.DeclaringType != null && owner.Name.Contains("DisplayClass")) owner = owner.DeclaringType;
                string ownerName = owner.Name;
                if (ownerName == "ThemedMessageBox" || ownerName == "ThemedMessageForm" || ownerName.EndsWith("Checks") || ownerName.EndsWith("Tests") || ownerName == "AssistantDemo" || ownerName == "AssistantLiveCheck" || ownerName == "UiAudit") continue;
                foreach (MethodBase method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                {
                    MethodBody body; try { body = method.GetMethodBody(); } catch (Exception) { continue; }
                    if (body == null) continue;
                    byte[] il = body.GetILAsByteArray();
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x28 && il[i] != 0x6F) continue;
                        int token = BitConverter.ToInt32(il, i + 1);
                        if ((token >> 24) != 0x0A && (token >> 24) != 0x06) continue;
                        MethodBase target; try { target = method.Module.ResolveMethod(token, type.IsGenericType ? type.GetGenericArguments() : null, method.IsGenericMethod ? method.GetGenericArguments() : null); } catch (Exception) { continue; }
                        if (target == null || target.DeclaringType == null) continue;
                        bool popup = matches(target);
                        if (!popup) continue;
                        string name = method.Name;
                        if (name.StartsWith("<") && name.IndexOf('>') > 1) name = name.Substring(1, name.IndexOf('>') - 1);
                        else if (type != owner && type.Name.StartsWith("<") && type.Name.IndexOf('>') > 1) name = type.Name.Substring(1, type.Name.IndexOf('>') - 1);
                        sites.Add(ownerName + "." + name);
                    }
                }
            }
            return sites.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        // 2.5.0: the one exception, asked for by the user: the extra-slot hint on the outfit name and Install
        // ("Adds a new slot. Keeps: …" / "Updates this slot."; MainFormSlots.cs). Field and the methods that set it.
        // 2.5.9: the second exception, also asked for: the hover on the status-bar bug and coffee icons (MainFormSupport.cs).
        // 1.0.1: the third, the hover naming the Installed page's icon-only History and Presets buttons (IconHint).
        internal static readonly string[] ToolTipAllowed = { "MainForm.slotHintTip", "MainForm.SlotHintTip", "MainForm.UpdateSlotHint", "MainForm.supportTip", "MainForm.BuildSupportLinks", "MainForm.AddSupportIcon", "MainForm.IconHint" };
        private static bool AllowedMethod(Type type, MethodBase method)
        {
            string name = method.Name.StartsWith("<") && method.Name.IndexOf('>') > 1 ? method.Name.Substring(1, method.Name.IndexOf('>') - 1) : method.Name;
            Type owner = type; while (owner.DeclaringType != null) owner = owner.DeclaringType;
            return ToolTipAllowed.Contains(owner.Name + "." + name);
        }
        // 2.5.1: no user-visible text names another product ("external mod manager" instead). These literals stay:
        // process and folder names used for detection, persisted Data/history/record values, the internal row kind,
        // the old CLI and Assistant names kept as aliases, and the --make-replace-fixture sample data.
        internal static readonly string[] WordAllowed = { "FluffyModManager", "fluffy", "Fluffy", "fluffy:", "fluffy_uninstall", "fluffy-preset:", "sf6-fluffy-removed-v1", "fluffy-removed", "fluffy-ini-backups", "fluffy-backup",
            "import_fluffy_preset", "fluffy-status", "fluffy-uninstall", "fluffy-restore", "[Fluffy Tool]\r\nModName=Fluffy Tool\r\ninstalltype=invalidate\r\nfile=reframework/autorun/fluffy_tool.lua\r\n",
            "fluffy_running" };   // Linux: the host helper's process-status key (linux/host.py), never shown
        // Every string literal (ldstr) of the app outside checks and test helpers that contains `word`, as "Type.Method: text".
        internal static List<string> LiteralFindings(string word, out int scanned)
        {
            var found = new List<string>(); scanned = 0;
            foreach (Type type in typeof(UiAudit).Assembly.GetTypes())
            {
                Type owner = type; while (owner.DeclaringType != null) owner = owner.DeclaringType;
                string ownerName = owner.Name;
                if (ownerName.EndsWith("Checks") || ownerName.EndsWith("Tests") || ownerName == "UiAudit" || ownerName == "AssistantDemo" || ownerName == "AssistantLiveCheck" || ownerName == "AssistantMockServer" || ownerName == "AssistantFixtures" || ownerName == "LocalTestConfig") continue;
                foreach (MethodBase method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                {
                    MethodBody body; try { body = method.GetMethodBody(); } catch (Exception) { continue; }
                    if (body == null) continue;
                    byte[] il = body.GetILAsByteArray(); scanned++;
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x72) continue;   // ldstr
                        int token = BitConverter.ToInt32(il, i + 1);
                        if ((token >> 24) != 0x70) continue;
                        string text; try { text = method.Module.ResolveString(token); } catch (Exception) { continue; }
                        if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0 || WordAllowed.Contains(text)) continue;
                        found.Add(ownerName + "." + method.Name + ": " + text);
                    }
                }
            }
            return found.Distinct().ToList();
        }
        // The texts a window shows (control text, accessible name and description, list, combo and menu items) that contain `word`.
        internal static List<string> TextFindings(Control root, string word)
        {
            var found = new List<string>();
            Action<string, string> look = delegate(string where, string text) { if (!String.IsNullOrEmpty(text) && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) found.Add(where + ": " + text); };
            foreach (Control c in Walk(root))
            {
                string n = Name(c);
                look(n, c.Text); look(n + " (accessible name)", c.AccessibleName); look(n + " (accessible description)", c.AccessibleDescription);
                var combo = c as ComboBox; if (combo != null) foreach (object item in combo.Items) look(n + " item", Convert.ToString(item));
                var box = c as ListBox; if (box != null) foreach (object item in box.Items) look(n + " item", Convert.ToString(item));
                var list = c as ListView; if (list != null) foreach (ListViewItem item in ListItems(list)) foreach (ListViewItem.ListViewSubItem sub in item.SubItems) look(n + " row", sub.Text);
                if (c.ContextMenuStrip != null) MenuTexts(c.ContextMenuStrip.Items, n, look);
                var strip = c as ToolStrip; if (strip != null) MenuTexts(strip.Items, n, look);
            }
            return found;
        }
        private static void MenuTexts(ToolStripItemCollection items, string owner, Action<string, string> look)
        {
            foreach (ToolStripItem i in items) { look(owner + " menu", i.Text); look(owner + " menu (accessible)", i.AccessibleName); var drop = i as ToolStripDropDownItem; if (drop != null) MenuTexts(drop.DropDownItems, owner, look); }
        }

        // Every method body of the app: no `new ToolTip`, no ToolTip method call, no set_ToolTipText (ToolTipAllowed excepted).
        internal static List<string> ToolTipCode(out int scanned)
        {
            var offenders = new List<string>(); scanned = 0;
            Assembly app = typeof(UiAudit).Assembly;
            foreach (Type type in app.GetTypes())
            {
                if (type == typeof(UiAudit)) continue;
                foreach (MethodBase method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                {
                    MethodBody body; try { body = method.GetMethodBody(); } catch (Exception) { continue; }
                    if (body == null) continue;
                    byte[] il = body.GetILAsByteArray(); scanned++;
                    if (AllowedMethod(type, method)) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        if (il[i] != 0x28 && il[i] != 0x6F && il[i] != 0x73) continue;   // call, callvirt, newobj
                        int token = BitConverter.ToInt32(il, i + 1);
                        if ((token >> 24) != 0x0A && (token >> 24) != 0x06) continue;
                        MethodBase target; try { target = method.Module.ResolveMethod(token, type.IsGenericType ? type.GetGenericArguments() : null, method.IsGenericMethod ? method.GetGenericArguments() : null); } catch (Exception) { continue; }
                        if (target == null || target.DeclaringType == null) continue;
                        bool tip = typeof(ToolTip).IsAssignableFrom(target.DeclaringType) || target.Name == "set_ToolTipText" || target.Name == "set_AutoToolTip";
                        if (tip) offenders.Add(type.Name + "." + method.Name + " → " + target.DeclaringType.Name + "." + target.Name);
                    }
                }
            }
            return offenders.Distinct().ToList();
        }
    }
}
