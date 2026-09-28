using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Limiter;

internal readonly record struct FlowKey(UInt128 Local, ushort LocalPort, UInt128 Remote, ushort RemotePort, byte Protocol);
internal readonly record struct TcpSegmentKey(FlowKey Flow, bool Outbound, uint Sequence, int PayloadLength);

internal readonly record struct PacketInfo(FlowKey Flow, bool Outbound, bool IsTcpControl, TcpSegmentKey? TcpSegment)
{
    internal static bool TryParse(ReadOnlySpan<byte> packet, bool outbound, out PacketInfo info)
    {
        info = default;
        if (packet.Length < 20) return false;
        int version = packet[0] >> 4;
        byte protocol;
        int offset;
        UInt128 source, destination;
        if (version == 4)
        {
            offset = (packet[0] & 15) * 4;
            if (offset < 20 || packet.Length < offset + 4 || (packet[6] & 0x1f) != 0 || packet[7] != 0) return false;
            protocol = packet[9];
            source = MappedV4(packet.Slice(12, 4));
            destination = MappedV4(packet.Slice(16, 4));
        }
        else if (version == 6)
        {
            offset = 40;
            if (packet.Length < offset + 4) return false;
            protocol = packet[6];
            source = BinaryPrimitives.ReadUInt128BigEndian(packet.Slice(8, 16));
            destination = BinaryPrimitives.ReadUInt128BigEndian(packet.Slice(24, 16));
        }
        else return false;
        if (protocol is not (6 or 17)) return false;
        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
        ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
        var flow = outbound
            ? new FlowKey(source, sourcePort, destination, destinationPort, protocol)
            : new FlowKey(destination, destinationPort, source, sourcePort, protocol);
        bool control = false;
        TcpSegmentKey? segment = null;
        if (protocol == 6)
        {
            if (packet.Length < offset + 20) return false;
            int tcpHeaderLength = (packet[offset + 12] >> 4) * 4;
            if (tcpHeaderLength < 20 || packet.Length < offset + tcpHeaderLength) return false;
            int payloadLength = packet.Length - offset - tcpHeaderLength;
            control = payloadLength == 0;
            if (payloadLength > 0)
                segment = new TcpSegmentKey(flow, outbound, BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(offset + 4, 4)), payloadLength);
        }
        info = new PacketInfo(flow, outbound, control, segment);
        return true;
    }

    internal static UInt128 MappedV4(ReadOnlySpan<byte> bytes)
        => ((UInt128)0xffff << 32) | BinaryPrimitives.ReadUInt32BigEndian(bytes);

    internal static UInt128 Address(ReadOnlySpan<byte> bytes)
        => BinaryPrimitives.ReadUInt128BigEndian(bytes);

    internal static UInt128 FlowAddress(ReadOnlySpan<byte> nativeBytes)
    {
        var output = new byte[64];
        if (!Native.WinDivertHelperFormatIPv6Address(nativeBytes.ToArray(), output, (uint)output.Length)) return 0;
        int end = Array.IndexOf(output, (byte)0);
        if (end < 0) return 0;
        try
        {
            var address = IPAddress.Parse(Encoding.ASCII.GetString(output, 0, end)).MapToIPv6();
            return BinaryPrimitives.ReadUInt128BigEndian(address.GetAddressBytes());
        }
        catch { return 0; }
    }
}
