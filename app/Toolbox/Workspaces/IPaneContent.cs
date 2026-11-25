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

    /// <summary>The connection this session belongs to; null for one that has none (a local terminal).</summary>
    Connection? Connection { get; }

    string Title { get; }

    /// <summary>
    /// A short name for the workspace's tab. The pane's own title can be
    /// longer and change more often.
    /// </summary>
    string TabTitle => Title;

    /// <summary>Connected right now (drives the header's status dot).</summary>
    bool IsLive { get; }

    /// <summary>
    /// What closing the pane would interrupt, as a sentence for the question
    /// that is asked first; null closes without asking.
    /// </summary>
    string? CloseWarning => IsLive ? "The session will be disconnected." : null;

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

/// <summary>A session whose text can be searched (Edit > Find): the terminals.</summary>
internal interface ISearchablePane
{
    /// <summary>Shows the find bar with the keyboard in it.</summary>
    void Find();

    void FindNext();
    void FindPrevious();

    /// <summary>Testing aid: searches for <paramref name="text"/> and logs what was found.</summary>
    void FindDemo(string text);
}
