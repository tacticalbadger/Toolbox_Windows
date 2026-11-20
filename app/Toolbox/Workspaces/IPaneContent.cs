using System.Windows;

namespace Toolbox.Workspaces;

/// <summary>
/// Something that can live in a pane: a terminal, a file browser, an RDP or
/// VNC surface. All members are used on the UI thread.
/// </summary>
internal interface IPaneContent
{
    /// <summary>The element shown below the pane's header.</summary>
    FrameworkElement View { get; }

    /// <summary>The connection this session belongs to.</summary>
    Connection Connection { get; }

    string Title { get; }

    /// <summary>Connected right now (drives the header's status dot and the "disconnect?" questions).</summary>
    bool IsLive { get; }

    event Action? TitleChanged;
    event Action? StateChanged;

    /// <summary>The content was clicked or took the keyboard; its pane should become the active one.</summary>
    event Action? FocusRequested;

    /// <summary>
    /// The session is over. True for a clean end (sign-out, logout, closed by
    /// us), false after a failure worth leaving on screen.
    /// </summary>
    event Action<bool>? SessionEnded;

    /// <summary>Gives the session the keyboard.</summary>
    void FocusSession();

    /// <summary>Ends the session for good. The content is not used afterwards.</summary>
    void Terminate();
}
