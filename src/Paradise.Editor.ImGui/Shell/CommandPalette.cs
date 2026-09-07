using System.Numerics;
using Hexa.NET.ImGui;
using Paradise.Editor.Core.Extensibility;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;
using Paradise.Ui.ImGui;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>Every registered operator, one fuzzy search away.</summary>
/// <remarks>
/// <para>
/// The reason operators carry a label and a description at all: a palette makes every action
/// reachable without a menu entry for it, so a rarely-used operator costs nothing to ship. It is
/// also the honest test of the operator model — anything that cannot be reached from here is
/// something a panel is doing behind the shell's back.
/// </para>
/// <para>
/// Unavailable operators are LISTED AND DISABLED rather than filtered out. A palette that hides
/// what you are looking for cannot tell you why it is not there, and "why is Undo missing" is a
/// question the greyed-out row answers by itself.
/// </para>
/// </remarks>
public sealed class CommandPalette(IOperatorDispatcher dispatcher, IRegistry<IOperator> operators)
{
    private const string PopupName = "###editor.palette";
    private const int MaxResults = 12;

    private readonly List<IOperator> _matches = [];
    private string _query = string.Empty;
    private int _selected;
    private bool _openRequested;
    private bool _focusRequested;

    public bool IsOpen { get; private set; }

    public void Open()
    {
        _query = string.Empty;
        _selected = 0;
        _openRequested = true;
        _focusRequested = true;
    }

    public void Close() => IsOpen = false;

    public void Draw()
    {
        if (_openRequested)
        {
            _openRequested = false;
            IsOpen = true;
            ImGuiApi.OpenPopup(PopupName);
        }

        if (!IsOpen) return;

        var viewport = ImGuiApi.GetMainViewport();
        var width = Math.Min(viewport.WorkSize.X * 0.5f, 640f);
        ImGuiApi.SetNextWindowPos(
            new Vector2(viewport.WorkPos.X + (viewport.WorkSize.X - width) * 0.5f, viewport.WorkPos.Y + 120f));
        ImGuiApi.SetNextWindowSize(new Vector2(width, 0f));

        if (!ImGuiApi.BeginPopup(PopupName, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar))
        {
            IsOpen = false;
            return;
        }

        if (_focusRequested)
        {
            _focusRequested = false;
            ImGuiApi.SetKeyboardFocusHere();
        }

        ImGuiApi.SetNextItemWidth(-1f);
        ImGuiApi.InputTextWithHint("###query", $"{EditorIcons.Search} Search commands", ref _query, 128);

        Rank();
        Steer();

        ImGuiApi.Separator();
        for (var i = 0; i < _matches.Count; i++)
        {
            var candidate = _matches[i];
            var available = dispatcher.IsAvailable(candidate.Id);

            if (!available) ImGuiApi.BeginDisabled();
            if (ImGuiApi.Selectable($"{candidate.Label}###{candidate.Id}", i == _selected) && available)
            {
                Run(candidate);
            }
            if (!available) ImGuiApi.EndDisabled();

            ImGuiApi.SameLine();
            ImGuiText.Disabled(candidate.Id);
        }

        if (_matches.Count == 0) ImGuiText.Disabled("No matching command.");

        // Enter runs the highlighted row; Escape closes. Handled after the list so a click in the
        // same frame has already been taken.
        if (ImGuiApi.IsKeyPressed(ImGuiKey.Enter) && _selected < _matches.Count
            && dispatcher.IsAvailable(_matches[_selected].Id))
        {
            Run(_matches[_selected]);
        }
        if (ImGuiApi.IsKeyPressed(ImGuiKey.Escape)) ImGuiApi.CloseCurrentPopup();

        ImGuiApi.EndPopup();
    }

    private void Run(IOperator candidate)
    {
        dispatcher.Dispatch(candidate.Id, OperatorArgs.None);
        ImGuiApi.CloseCurrentPopup();
        IsOpen = false;
    }

    /// <summary>Best first. Matched on the label AND the id, so both "Reset layout" and
    /// "editor.layout.reset" find the same row — the id is what documentation and a keymap file
    /// spell it as.</summary>
    private void Rank()
    {
        _matches.Clear();
        var scored = new List<(IOperator Operator, int Score)>();
        foreach (var candidate in operators.Entries)
        {
            var matched = FuzzyMatch.TryScore(_query, candidate.Label, out var labelScore);
            var byId = FuzzyMatch.TryScore(_query, candidate.Id, out var idScore);
            if (!matched && !byId) continue;
            scored.Add((candidate, Math.Max(matched ? labelScore : int.MinValue, byId ? idScore : int.MinValue)));
        }

        foreach (var entry in scored.OrderByDescending(entry => entry.Score).Take(MaxResults))
        {
            _matches.Add(entry.Operator);
        }
    }

    private void Steer()
    {
        if (ImGuiApi.IsKeyPressed(ImGuiKey.DownArrow)) _selected++;
        if (ImGuiApi.IsKeyPressed(ImGuiKey.UpArrow)) _selected--;
        _selected = _matches.Count == 0 ? 0 : Math.Clamp(_selected, 0, _matches.Count - 1);
    }
}
