using Paradise.Editor.Core.Host;
using Paradise.Editor.Core.Operators;
using Paradise.Editor.Core.Shell;

namespace Paradise.Editor.Core.Extensibility;

/// <summary>Every kind of contribution the editor accepts, in one place.</summary>
/// <remarks>Adding a kind of contribution is adding a registry here and a method on
/// <see cref="EditorRegistrar"/>; nothing else learns about it.</remarks>
public sealed class EditorRegistries
{
    public IRegistry<IOperator> Operators { get; } = new Registry<IOperator>();

    public IRegistry<WindowDescriptor> Windows { get; } = new Registry<WindowDescriptor>();

    public IRegistry<WorkspaceDescriptor> Workspaces { get; } = new Registry<WorkspaceDescriptor>();

    public IRegistry<MenuEntry> Menus { get; } = new Registry<MenuEntry>();

    public IRegistry<KeyBinding> KeyBindings { get; } = new Registry<KeyBinding>();

    public IRegistry<HostKind> HostKinds { get; } = new Registry<HostKind>();

    public void RemoveOwner(OwnerToken owner)
    {
        Operators.RemoveOwner(owner);
        Windows.RemoveOwner(owner);
        Workspaces.RemoveOwner(owner);
        Menus.RemoveOwner(owner);
        KeyBindings.RemoveOwner(owner);
        HostKinds.RemoveOwner(owner);
    }
}

/// <summary>What an extension registers through; stamps every contribution with its owner.</summary>
public sealed class EditorRegistrar(EditorRegistries registries, OwnerToken owner)
{
    public OwnerToken Owner => owner;

    public EditorRegistrar AddOperator(IOperator operatorInstance)
    {
        registries.Operators.Add(owner, operatorInstance);
        return this;
    }

    public EditorRegistrar AddWindow(WindowDescriptor window)
    {
        registries.Windows.Add(owner, window);
        return this;
    }

    public EditorRegistrar AddWorkspace(WorkspaceDescriptor workspace)
    {
        registries.Workspaces.Add(owner, workspace);
        return this;
    }

    public EditorRegistrar AddMenuEntry(MenuEntry entry)
    {
        registries.Menus.Add(owner, entry);
        return this;
    }

    public EditorRegistrar AddKeyBinding(KeyBinding binding)
    {
        registries.KeyBindings.Add(owner, binding);
        return this;
    }

    public EditorRegistrar AddHostKind(HostKind kind)
    {
        registries.HostKinds.Add(owner, kind);
        return this;
    }
}

/// <summary>A unit of contribution: the built-in shell, a built-in panel, a game's own tools.</summary>
public interface IEditorExtension
{
    string Id { get; }

    void Register(EditorRegistrar registrar);
}
