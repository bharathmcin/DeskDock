namespace DeskDock;

public sealed record AndroidDevice(string Serial, string State, string Details)
{
    public string Label => State == "device"
        ? $"{Serial}  ·  {Details}"
        : $"{Serial}  ·  {State}";
}
