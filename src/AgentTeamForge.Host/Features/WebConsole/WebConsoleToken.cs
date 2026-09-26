using System.Text;
using AgentTeamForge.Host.Hosting;

namespace AgentTeamForge.Host.Features.WebConsole;

/// <summary>Owner-private console bearer, separate from the daemon IPC credential.</summary>
public static class WebConsoleToken
{
    const string FileName = "web-console.key";

    public static string Read(StateDirectory state)
    {
        var token = Encoding.ASCII.GetString(StateDirectory.ReadPrivateFile(Path.Combine(state.Path, FileName)));
        if (token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new StateDirectoryException("web_token_invalid");
        }
        return token;
    }

    public static string Ensure(StateDirectory state)
    {
        var path = Path.Combine(state.Path, FileName);
        if (File.Exists(path))
        {
            return Read(state);
        }
        var token = WebConsoleServer.NewToken();
        try
        {
            Write(path, token);
            return token;
        }
        catch (IOException) when (File.Exists(path))
        {
            return Read(state);
        }
    }

    public static string Rotate(StateDirectory state)
    {
        var path = Path.Combine(state.Path, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var token = WebConsoleServer.NewToken();
        try
        {
            Write(temporary, token);
            File.Move(temporary, path, overwrite: true);
            return token;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    static void Write(string path, string token)
    {
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = OperatingSystem.IsWindows() ? null : StateDirectory.PrivateFile,
        });
        file.Write(Encoding.ASCII.GetBytes(token));
        file.Flush(flushToDisk: true);
    }
}
