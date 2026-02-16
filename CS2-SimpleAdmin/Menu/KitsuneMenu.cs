using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API.Core;
using KitsuneMenuMenu = KitsuneMenu.Core.Menu;
using Menu.Enums;

namespace Menu
{
    public class MenuValue
    {
        public MenuValue(string text)
        {
            Text = text;
        }

        public string Text { get; }

        public override string ToString() => Text;
    }

    public class MenuItem
    {
        public MenuItem(MenuItemType type, IEnumerable<MenuValue> values)
        {
            Type = type;
            Values = values.ToList();
        }

        public MenuItemType Type { get; }

        public List<MenuValue> Values { get; }
    }

    public class ScrollableMenu
    {
        public int Option { get; set; }
    }

    public class KitsuneMenu : IDisposable
    {
        private bool _disposed;

        public KitsuneMenu(BasePlugin plugin)
        {
            global::KitsuneMenu.KitsuneMenu.Init();
        }

        public void ShowScrollableMenu(
            CCSPlayerController player,
            string title,
            List<MenuItem> items,
            Action<MenuButtons, ScrollableMenu, MenuItem?>? onSelect,
            bool allowBack = false,
            bool freezePlayer = true,
            bool disableDeveloper = false)
        {
            if (player == null || !player.IsValid || items.Count == 0 || onSelect == null)
                return;

            var builder = global::KitsuneMenu.KitsuneMenu.Create(title);
            var maxVisibleItems = Math.Min(Math.Max(items.Count, 1), 8);
            builder.MaxVisibleItems(maxVisibleItems);

            if (freezePlayer)
                builder.ForceFreeze();
            else
                builder.NoFreeze();

            KitsuneMenuMenu? parent = null;
            if (allowBack && global::KitsuneMenu.KitsuneMenu.TryGetSession(player, out var session))
            {
                parent = session.CurrentMenu as KitsuneMenuMenu;
            }

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.Type != MenuItemType.Button)
                    continue;

                var label = string.Join(" ", item.Values.Select(v => v.Text));
                var capturedIndex = index;

                builder.AddButton(label, _ =>
                {
                    onSelect(MenuButtons.Select, new ScrollableMenu { Option = capturedIndex }, item);
                });
            }

            var menu = builder.Build();
            menu.Parent = parent;
            menu.Show(player);
        }

        public void ClearMenus(CCSPlayerController player)
        {
            global::KitsuneMenu.KitsuneMenu.CloseMenu(player);
        }

        public void Dispose()
        {
            if (_disposed) return;

            global::KitsuneMenu.KitsuneMenu.Cleanup();
            _disposed = true;
        }
    }
}

namespace Menu.Enums
{
    public enum MenuButtons
    {
        None = 0,
        Select = 1,
        Back = 2,
        Exit = 4
    }

    public enum MenuItemType
    {
        Button = 0
    }
}
