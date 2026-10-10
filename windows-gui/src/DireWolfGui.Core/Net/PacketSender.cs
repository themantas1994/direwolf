using System.Text;

namespace DireWolfGui.Core.Net;

/// <summary>Something that can ask the TNC to transmit a UI frame. Only called on explicit user action.</summary>
public interface IPacketSender
{
    bool CanSend { get; }
    Task SendUnprotoAsync(int port, string source, string destination, IReadOnlyList<string> path, string info, CancellationToken ct = default);
}

/// <summary>Sends UI frames through Dire Wolf's AGW interface ('V' with a path, 'M' without).</summary>
public sealed class AgwPacketSender(AgwClient client) : IPacketSender
{
    public bool CanSend => client.IsConnected;

    public Task SendUnprotoAsync(int port, string source, string destination, IReadOnlyList<string> path, string info, CancellationToken ct = default)
    {
        if (!client.IsConnected) throw new InvalidOperationException("Not connected to Dire Wolf's AGW port; nothing was sent.");
        var data = Encoding.UTF8.GetBytes(info);
        return path.Count > 0
            ? client.SendUnprotoViaAsync(port, source.ToUpperInvariant(), destination.ToUpperInvariant(), path, data, 0xF0, ct)
            : client.SendUnprotoAsync(port, source.ToUpperInvariant(), destination.ToUpperInvariant(), data, 0xF0, ct);
    }
}
