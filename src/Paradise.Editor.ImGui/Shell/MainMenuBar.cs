using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;
using Paradise.Ui.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>The menu bar, drawn from what is registered rather than written out.</summary>
/// <remarks>
/// <para>
/// Every item is a <see cref="MenuEntry"/> naming an operator id, so an extension adds a menu item
/// the same way the built-in shell does and neither holds code. That is also what makes an item's
/// enabled state honest: it comes from the operator's own <see cref="IOperator.IsAvailable"/>, so
/// Undo greys out because there is nothing to undo, not because somebody remembered to check.
/// </para>
/// <para>
/// Menus appear in the order their FIRST entry was registered, and entries within a menu by
/// <see cref="MenuEntry.Order"/>. Registration order is deterministic and the built-in shell
/// registers first, so File stays leftmost without a hard-coded list of menu names.
/// </para>
/// </remarks>
public sealed class MainMenuBar(IOperatorDispatcher dispatcher, IRegistry<MenuEntry> menus)
{
    public void Draw()
    {
        if (!ImGuiApi.BeginMainMenuBar()) return;

        foreach (var menu in menus.Entries.Select(entry => entry.Menu).Distinct())
        {
            if (!ImGuiApi.BeginMenu(menu)) continue;

            var entries = menus.Entries
                .Where(entry => entry.Menu == menu)
                .OrderBy(entry => entry.Order);

            foreach (var entry in entries)
            {
                if (entry.Label == MenuEntry.Separator)
                {
                    ImGuiApi.Separator();
                    continue;
                }

                var operatorInstance = dispatcher.Find(entry.OperatorId);
                var enabled = dispatcher.IsAvailable(entry.OperatorId);
                // An item whose operator nothing registered is shown DISABLED rather than hidden:
                // a menu that silently loses entries when an extension fails to load is a menu
                // nobody can debug.
                if (ImGuiApi.MenuItem(entry.Label, string.Empty, false, enabled))
                {
                    dispatcher.Dispatch(entry.OperatorId, OperatorArgs.None);
                }

                if (operatorInstance is not null && ImGuiApi.IsItemHovered() && operatorInstance.Description.Length > 0)
                {
                    ImGuiApi.BeginTooltip();
                    ImGuiText.Show(operatorInstance.Description);
                    ImGuiApi.EndTooltip();
                }
            }

            ImGuiApi.EndMenu();
        }

        ImGuiApi.EndMainMenuBar();
    }
}
