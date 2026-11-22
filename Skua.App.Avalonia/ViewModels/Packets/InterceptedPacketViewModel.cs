namespace Skua.App.Avalonia.ViewModels.Packets;

public record InterceptedPacketViewModel(string Packet, bool? Outbound)
{
    public bool IsBlocked => Outbound is null;

    public string Direction => Outbound switch { true => "OUT", false => "IN", null => "BLOCK" };

    private string[]? _commands;

    public string[] Commands => _commands ??= PacketCommands.Parse(Packet);

    public override string ToString()
    {
        return Packet;
    }
}