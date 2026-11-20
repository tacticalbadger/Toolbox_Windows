namespace Toolbox.Model;

internal static class ConnectionKindExtensions
{
    public static string Label(this ConnectionKind kind) => kind switch
    {
        ConnectionKind.Ssh => "SSH",
        ConnectionKind.Rdp => "RDP",
        ConnectionKind.Vnc => "VNC",
        ConnectionKind.Sftp => "SFTP",
        ConnectionKind.Ftp => "FTP",
        ConnectionKind.Ftps => "FTPS",
        _ => kind.ToString(),
    };

    public static int DefaultPort(this ConnectionKind kind) => kind switch
    {
        ConnectionKind.Ssh or ConnectionKind.Sftp => 22,
        ConnectionKind.Rdp => 3389,
        ConnectionKind.Vnc => 5900,
        _ => 21,
    };

    /// <summary>File Transfer connections open the file browser instead of a session.</summary>
    public static bool IsFileTransfer(this ConnectionKind kind) =>
        kind is ConnectionKind.Sftp or ConnectionKind.Ftp or ConnectionKind.Ftps;

    /// <summary>Whether SSH authentication (keys, agent) applies.</summary>
    public static bool UsesSsh(this ConnectionKind kind) =>
        kind is ConnectionKind.Ssh or ConnectionKind.Sftp;
}
